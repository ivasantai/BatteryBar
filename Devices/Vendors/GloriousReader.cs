using System.Security.Cryptography;
using System.Text;

namespace BatteryBar;

/// <summary>
/// Беспроводные мыши Glorious (платформа Sinowealth, VID 0x258A).
/// Запрос — feature report 65 байт; описание — проект glorious-ctl (github.com/louis4craft/glorious-ctl, MIT).
/// VID 0x258A используют многие бренды (в том числе клавиатуры), поэтому опрашиваются только известные PID.
/// </summary>
internal static class GloriousReader
{
    private const ushort Vid = 0x258A;
    private const int ReportLen = 65;

    private static readonly Dictionary<ushort, (string Name, bool Cable)> Models = new()
    {
        [0x2022] = ("Glorious Model O Wireless", false),
        [0x2024] = ("Glorious Model O- Wireless", false),
        [0x2023] = ("Glorious Model D Wireless", false),
        [0x2025] = ("Glorious Model D- Wireless", false),
        [0x2033] = ("Glorious Model O 2 Wireless", false),
        [0x2034] = ("Glorious Model D 2 PRO", false),
        [0x2036] = ("Glorious Model D 2 PRO", false),
        [0x2011] = ("Glorious Wireless (кабель)", true),
        [0x2012] = ("Glorious Wireless (кабель)", true),
        [0x2013] = ("Glorious Wireless (кабель)", true),
        [0x2014] = ("Glorious Wireless (кабель)", true),
        [0x201A] = ("Glorious Model D 2 PRO", true),
    };

    public static List<BatteryDevice> Read(StringBuilder? log)
    {
        var result = new List<BatteryDevice>();
        var done = new HashSet<ushort>();
        foreach (var info in Hid.Enumerate(Vid))
        {
            if (!Models.TryGetValue(info.Pid, out var model) || done.Contains(info.Pid)) continue;
            if (info.FeatureLen != ReportLen) continue;

            using var h = Hid.OpenNoAccess(info.Path);
            if (h.IsInvalid) continue;

            int? level = null;
            for (int attempt = 0; attempt < 2 && level == null; attempt++)
            {
                var req = new byte[ReportLen];
                req[3] = 0x02;
                req[4] = 0x02;
                req[6] = 0x83;
                if (!Hid.SetFeature(h, req)) break;
                Thread.Sleep(50);

                var resp = new byte[ReportLen];
                if (!Hid.GetFeature(h, resp)) break;
                // [1] статус: A1 на связи, A0 просыпается, A4 спит; [6] = 0x83; [7] заряжается; [8] заряд %.
                if (resp[6] != 0x83 || (resp[1] & 0xF0) != 0xA0) break;
                log?.AppendLine($"  {model.Name} pid={info.Pid:X4}: статус={resp[1]:X2} заряд={resp[8]}");

                if (resp[1] == 0xA1 && resp[8] <= 100) level = Math.Max(1, (int)resp[8]);
                else if (resp[1] == 0xA0) Thread.Sleep(400); // просыпается — повторим
                else break; // спит
            }
            if (level == null) continue;

            done.Add(info.Pid);
            result.Add(new BatteryDevice(new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"glorious:{info.Pid:x4}"))),
                model.Name, level.Value, DeviceKind.Mouse));
        }
        return result;
    }
}
