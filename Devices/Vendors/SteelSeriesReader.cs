using System.Security.Cryptography;
using System.Text;

namespace BatteryBar;

/// <summary>
/// Беспроводные мыши SteelSeries (донгл 2.4 ГГц или кабель).
/// Команды и формулы — по открытому проекту rivalcfg (github.com/flozz/rivalcfg).
/// </summary>
internal static class SteelSeriesReader
{
    private const ushort Vid = 0x1038;

    private enum Family
    {
        /// <summary>Aerox 3/5/9, Prime Wireless: команда 0x92 (| 0x40 через донгл), ответ 2 байта.</summary>
        Aerox,
        /// <summary>Rival 3 Wireless, Rival 650: команда AA 01, ответ 3 байта.</summary>
        Rival,
    }

    private sealed record Model(string Name, int Interface, Family Family, bool ViaDongle);

    private static readonly Dictionary<ushort, Model> Models = new()
    {
        [0x1838] = new("SteelSeries Aerox 3 Wireless", 3, Family.Aerox, true),
        [0x183A] = new("SteelSeries Aerox 3 Wireless", 3, Family.Aerox, false),
        [0x1878] = new("SteelSeries Aerox 3 Wireless", 3, Family.Aerox, true),
        [0x187A] = new("SteelSeries Aerox 3 Wireless", 3, Family.Aerox, false),
        [0x1852] = new("SteelSeries Aerox 5 Wireless", 3, Family.Aerox, true),
        [0x1854] = new("SteelSeries Aerox 5 Wireless", 3, Family.Aerox, false),
        [0x185C] = new("SteelSeries Aerox 5 Wireless", 3, Family.Aerox, true),
        [0x185E] = new("SteelSeries Aerox 5 Wireless", 3, Family.Aerox, false),
        [0x1860] = new("SteelSeries Aerox 5 Wireless", 3, Family.Aerox, true),
        [0x1862] = new("SteelSeries Aerox 5 Wireless", 3, Family.Aerox, false),
        [0x1858] = new("SteelSeries Aerox 9 Wireless", 3, Family.Aerox, true),
        [0x185A] = new("SteelSeries Aerox 9 Wireless", 3, Family.Aerox, false),
        [0x1874] = new("SteelSeries Aerox 9 Wireless", 3, Family.Aerox, true),
        [0x1876] = new("SteelSeries Aerox 9 Wireless", 3, Family.Aerox, false),
        [0x1840] = new("SteelSeries Prime Wireless", 3, Family.Aerox, true),
        [0x1842] = new("SteelSeries Prime Wireless", 3, Family.Aerox, false),
        [0x1848] = new("SteelSeries Prime Mini Wireless", 3, Family.Aerox, true),
        [0x184A] = new("SteelSeries Prime Mini Wireless", 3, Family.Aerox, false),
        [0x1830] = new("SteelSeries Rival 3 Wireless", 3, Family.Rival, true),
        [0x1872] = new("SteelSeries Rival 3 Wireless Gen 2", 3, Family.Rival, true),
        [0x1726] = new("SteelSeries Rival 650 Wireless", 0, Family.Rival, true),
        [0x172B] = new("SteelSeries Rival 650 Wireless", 0, Family.Rival, false),
    };

    public static List<BatteryDevice> Read(StringBuilder? log)
    {
        var result = new List<BatteryDevice>();
        var done = new HashSet<ushort>();
        foreach (var info in Hid.Enumerate(Vid))
        {
            if (done.Contains(info.Pid) || !Models.TryGetValue(info.Pid, out var model)) continue;
            if (info.Interface != model.Interface || info.OutLen <= 1 || info.InLen <= 1) continue;

            using var ch = HidChannel.Open(info);
            if (ch == null) { log?.AppendLine($"  {model.Name}: не удалось открыть"); continue; }

            int? level = model.Family == Family.Aerox ? ReadAerox(ch, model.ViaDongle) : ReadRival(ch);
            log?.AppendLine($"  {model.Name} pid={info.Pid:X4}: {level?.ToString() ?? "нет ответа"}");
            if (level == null) continue;

            done.Add(info.Pid);
            result.Add(new BatteryDevice(new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"steelseries:{info.Pid:x4}"))),
                model.Name, level.Value, DeviceKind.Mouse));
        }
        return result;
    }

    private static int? ReadAerox(HidChannel ch, bool viaDongle)
    {
        byte cmd = (byte)(viaDongle ? 0x92 | 0x40 : 0x92);
        if (!ch.Write(new byte[] { 0x00, cmd })) return null;
        int? level = null;
        Hid.ReadMatching(new[] { ch }, d =>
        {
            var p = Payload(d);
            if (p.Length < 2) return false;
            int raw = p[1] & 0x7F; // старший бит — «заряжается»
            if (raw is < 1 or > 21) return false;
            level = (raw - 1) * 5;
            return true;
        }, 300);
        return level;
    }

    private static int? ReadRival(HidChannel ch)
    {
        if (!ch.Write(new byte[] { 0x00, 0xAA, 0x01 })) return null;
        int? level = null;
        Hid.ReadMatching(new[] { ch }, d =>
        {
            var p = Payload(d);
            if (p.Length < 3 || p[0] > 100) return false;
            level = p[0];
            return true;
        }, 300);
        return level;
    }

    /// <summary>Убирает нулевой report id в начале входного репорта.</summary>
    private static byte[] Payload(byte[] d) => d.Length > 0 && d[0] == 0 ? d[1..] : d;
}
