using System.Security.Cryptography;
using System.Text;

namespace BatteryBar;

/// <summary>
/// Мыши на универсальном приёмнике SinoWealth 258A:002F (кабель — 258A:002E):
/// HATOR Pulsar 2 / Pulsar 2 Pro Wireless (проверено на реальной мыши), Redragon M910-KS / M690 PRO,
/// Primus Gladius и другие. Протокол — по проекту redragon-m910ks (github.com/dominikake/redragon-m910ks, MIT).
/// Используются только команды чтения: 0x80 (состояние связи) и 0x90 (заряд), как у программы производителя.
/// </summary>
internal static class SinoWealthReader
{
    private const ushort Vid = 0x258A;
    private static readonly Dictionary<ushort, bool> Pids = new() { [0x002F] = false, [0x002E] = true }; // true — кабель
    private const byte ReportId = 0x05, CmdLink = 0x80, CmdBattery = 0x90;
    private const byte LinkWired = 0x10, LinkWireless = 0x11;

    private static readonly Dictionary<string, (int Level, DateTime At)> LastKnown = new();
    private static readonly TimeSpan LastKnownTtl = TimeSpan.FromHours(2);
    private static readonly object Sync = new();

    public static List<BatteryDevice> Read(StringBuilder? log)
    {
        lock (Sync)
        {
            var result = new List<BatteryDevice>();
            var done = new HashSet<string>();
            foreach (var info in Hid.Enumerate(Vid))
            {
                if (!Pids.ContainsKey(info.Pid) || info.UsagePage != 0xFF00 || info.FeatureLen != 8) continue;
                if (!done.Add(info.DeviceKey)) continue;

                using var h = Hid.OpenNoAccess(info.Path);
                if (h.IsInvalid) continue;

                // 05 80: [2] = 1 — мышь на связи, 0 — спит/выключена.
                var link = Query(h, CmdLink);
                bool awake = link == null || link[2] == 1;

                int? level = null;
                bool wired = Pids[info.Pid];
                if (awake)
                {
                    Thread.Sleep(50);
                    // 05 90: [2] тип связи (0x11 радио, 0x10 кабель), [3] заряд % (0 — ещё не известен).
                    var bat = Query(h, CmdBattery);
                    if (bat != null && (bat[2] == LinkWireless || bat[2] == LinkWired) && bat[3] is > 0 and <= 100)
                    {
                        level = bat[3];
                        wired |= bat[2] == LinkWired;
                    }
                    log?.AppendLine($"  {info.Product} {info.Pid:X4}: связь={(link != null ? Convert.ToHexString(link) : "?")} заряд={(bat != null ? Convert.ToHexString(bat) : "нет ответа")}");
                }
                else log?.AppendLine($"  {info.Product} {info.Pid:X4}: мышь спит");

                string key = info.DeviceKey;
                bool asleep = level == null && !wired;
                if (level != null) LastKnown[key] = (level.Value, DateTime.UtcNow);
                else if (LastKnown.TryGetValue(key, out var last) && DateTime.UtcNow - last.At < LastKnownTtl)
                    level = last.Level; // уснула — последнее известное значение
                if (level == null) continue;

                result.Add(new BatteryDevice(new Guid(MD5.HashData(Encoding.UTF8.GetBytes("sinowealth:" + key))),
                    "Беспроводная мышь (2.4G)", level.Value, DeviceKind.Mouse, asleep));
            }
            return result;
        }
    }

    private static byte[]? Query(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte cmd)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var req = new byte[8];
            req[0] = ReportId;
            req[1] = cmd;
            if (!Hid.SetFeature(h, req)) return null;
            Thread.Sleep(90);
            var resp = new byte[8];
            resp[0] = ReportId;
            if (!Hid.GetFeature(h, resp)) return null;
            if (resp[1] == cmd) return resp;
            Thread.Sleep(200);
        }
        return null;
    }
}
