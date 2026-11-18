using System.Security.Cryptography;
using System.Text;

namespace BatteryBar;

/// <summary>
/// Мыши HyperX (HP) через донгл: «универсальный» протокол NGENUITY.
/// Описание протокола — проект ngenuity-linux-cli (github.com/PMarinov1994/ngenuity-linux-cli, MIT).
/// Все пакеты — 64 байта, первый байт = report id = команда; ответ приходит с id = команда + 1.
/// </summary>
internal static class HyperXReader
{
    private const ushort Vid = 0x03F0; // HP

    private static readonly Dictionary<ushort, string> Models = new()
    {
        [0x0F98] = "HyperX Pulsefire Haste 2 Wireless",
        [0x0D97] = "HyperX Pulsefire Haste 2 Wireless",
        [0x0AB5] = "HyperX Pulsefire Haste 2 Core Wireless",
    };

    // Сразу после включения мышь несколько секунд сообщает 40% вместо реального заряда.
    private static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(6);
    private static readonly Dictionary<ushort, DateTime> LinkedSince = new();
    private static readonly object Sync = new();

    public static List<BatteryDevice> Read(StringBuilder? log)
    {
        lock (Sync)
        {
            var result = new List<BatteryDevice>();
            var done = new HashSet<ushort>();
            foreach (var info in Hid.Enumerate(Vid))
            {
                if (!Models.TryGetValue(info.Pid, out var name) || done.Contains(info.Pid)) continue;
                if (info.UsagePage < 0xFF00 || info.OutLen < 2 || info.InLen < 8) continue;

                using var ch = HidChannel.Open(info);
                if (ch == null) { log?.AppendLine($"  {name}: не удалось открыть"); continue; }

                // 52 01 -> 53 01: [2] = 1 мышь на связи, 0 — спит/выключена.
                var link = Query(ch, 0x52, 0x01);
                bool linked = link == null || link[2] != 0;
                if (!linked)
                {
                    LinkedSince[info.Pid] = DateTime.MaxValue; // отметка «спала» — после пробуждения подождём
                    log?.AppendLine($"  {name}: мышь не на связи");
                    continue;
                }
                if (!LinkedSince.TryGetValue(info.Pid, out var since))
                    LinkedSince[info.Pid] = DateTime.MinValue;   // уже работала при запуске — доверяем сразу
                else if (since == DateTime.MaxValue)
                    LinkedSince[info.Pid] = DateTime.UtcNow;     // только что проснулась

                Thread.Sleep(10); // NGENUITY выдерживает паузу между пакетами
                // 50 02 -> 51 02: [2] заряд %, [3] 0 не заряжается, 1 заряжается, 2 заряжена.
                var bat = Query(ch, 0x50, 0x02);
                log?.AppendLine($"  {name} pid={info.Pid:X4}: {(bat != null ? $"{bat[2]}% статус={bat[3]}" : "нет ответа")}");
                if (bat == null || bat[2] > 100) continue;
                if (DateTime.UtcNow - LinkedSince[info.Pid] < SettleTime)
                {
                    log?.AppendLine("    только что проснулась — значение пока не достоверно");
                    continue;
                }

                done.Add(info.Pid);
                result.Add(new BatteryDevice(new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"hyperx:{info.Pid:x4}"))),
                    name, bat[2], DeviceKind.Mouse));
            }
            return result;
        }
    }

    private static byte[]? Query(HidChannel ch, byte cmd, byte sub)
    {
        if (!ch.Write(new byte[] { cmd, sub })) return null; // дополняется нулями до 64 байт
        return Hid.ReadMatching(new[] { ch },
            r => r.Length >= 8 && r[0] == cmd + 1 && r[1] == sub, // ACK 0xFF и прочие репорты пропускаем
            1000);
    }
}
