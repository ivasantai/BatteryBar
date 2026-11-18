using System.Security.Cryptography;
using System.Text;

namespace BatteryBar;

/// <summary>
/// Беспроводные гарнитуры с USB-донглами: SteelSeries Arctis, HyperX Cloud, Logitech G, Corsair.
/// Команды и разбор ответов — по описанию протоколов в проекте HeadsetControl (github.com/Sapd/HeadsetControl).
/// Все запросы только читают состояние.
/// </summary>
internal static class HeadsetReader
{
    private const ushort SteelSeries = 0x1038, Hp = 0x03F0, Kingston = 0x0951, Logitech = 0x046D, Corsair = 0x1B1C;

    /// <summary>Выбор HID-коллекции: по usage page/usage (если заданы), иначе по номеру интерфейса.</summary>
    private sealed record Family(
        string Name, ushort Vid, ushort[] Pids, ushort UsagePage, ushort Usage, int Interface,
        Func<HidChannel, ushort, int?> Read);

    private static readonly Family[] Families =
    {
        // ---------------- SteelSeries ----------------
        new("SteelSeries Arctis 7", SteelSeries, new ushort[] { 0x1260, 0x12AD, 0x1252, 0x1280 }, 0xFF43, 0x0202, 5,
            (ch, _) => Pct(Exchange(ch, new byte[] { 0x06, 0x18 }, p => p.Length >= 3), 2)),
        new("SteelSeries Arctis 1/7X/7P", SteelSeries, new ushort[] { 0x12B3, 0x12B6, 0x12D7, 0x12D5 }, 0xFF43, 0x0202, 3,
            (ch, _) =>
            {
                var p = Exchange(ch, new byte[] { 0x06, 0x12 }, p => p.Length >= 4);
                return p == null || p[2] == 0x01 ? null : Pct(p, 3); // [2] = 1 — гарнитура выключена
            }),
        new("SteelSeries Arctis 9", SteelSeries, new ushort[] { 0x12C2, 0x12C4 }, 0, 0, 0,
            (ch, _) =>
            {
                var p = Exchange(ch, new byte[] { 0x00, 0x20 }, p => p.Length >= 5);
                return p == null || p[4] == 0xFF ? null : Map(p[3], 0x64, 0x9A);
            }),
        new("SteelSeries Arctis Pro Wireless", SteelSeries, new ushort[] { 0x1290 }, 0, 0, 0,
            (ch, _) =>
            {
                var st = Exchange(ch, new byte[] { 0x41, 0xAA }, p => p.Length >= 1);
                if (st == null || st[0] == 0x02) return null; // не на связи
                var p = Exchange(ch, new byte[] { 0x40, 0xAA }, p => p.Length >= 1);
                return p == null ? null : Map(p[0], 0, 4);
            }),
        new("SteelSeries Arctis Nova 7", SteelSeries, new ushort[]
            { 0x2202, 0x22A1, 0x227E, 0x2206, 0x2258, 0x229E, 0x22AD, 0x223A, 0x22A9, 0x227A, 0x22A4, 0x22A5, 0x220A, 0x22A7, 0x2298 },
            0xFFC0, 0x0001, 3,
            (ch, pid) =>
            {
                var p = Exchange(ch, new byte[] { 0x00, 0xB0 }, p => p.Length >= 4);
                if (p == null || p[3] == 0x00) return null; // [3] = 0 — гарнитура выключена
                // У части ревизий заряд в шагах 0..4, у остальных — сразу в процентах.
                bool discrete = pid is 0x2202 or 0x2206 or 0x220A or 0x223A or 0x227A or 0x22A4;
                return discrete ? Map(p[2], 0, 4) : Pct(p, 2);
            }),
        new("SteelSeries Arctis 7+", SteelSeries, new ushort[] { 0x220E, 0x2212, 0x2216, 0x2236 }, 0xFFC0, 0x0001, 3,
            (ch, _) =>
            {
                var p = Exchange(ch, new byte[] { 0x00, 0xB0 }, p => p.Length >= 4);
                return p == null || p[1] == 0x01 ? null : Map(p[2], 0, 4);
            }),
        new("SteelSeries Arctis Nova Pro Wireless", SteelSeries, new ushort[] { 0x12E0, 0x12E5 }, 0, 0, 4,
            (ch, _) =>
            {
                var p = Exchange(ch, new byte[] { 0x06, 0xB0 }, p => p.Length >= 16);
                return p == null || p[15] == 0x01 ? null : Map(p[6], 0, 8);
            }),

        // ---------------- HyperX ----------------
        new("HyperX Cloud II Wireless", Hp, new ushort[] { 0x0696, 0x018B }, 0xFF90, 0x0303, 0,
            (ch, _) =>
            {
                var p = Exchange(ch, new byte[] { 0x06, 0xFF, 0xBB, 0x02, 0x00 },
                    p => p.Length >= 8 && p[0] == 0x06 && p[1] == 0xFF && p[2] == 0xBB && p[3] == 0x02);
                return Pct(p, 7);
            }),
        new("HyperX Cloud II Wireless", Kingston, new ushort[] { 0x1718 }, 0, 0, 0,
            (ch, _) =>
            {
                var req = new byte[] { 0x06, 0x00, 0x02, 0x00, 0x9A, 0x00, 0x00, 0x68, 0x4A, 0x8E, 0x0A, 0x00, 0x00, 0x00, 0xBB, 0x02 };
                var p = Exchange(ch, req, p => p.Length >= 8 && p[0] == 0x0B && p[2] == 0xBB && p[3] == 0x02);
                return Pct(p, 7);
            }),
        new("HyperX Cloud Alpha Wireless", Hp, new ushort[] { 0x098D }, 0, 0, 0,
            (ch, _) =>
            {
                var st = Exchange(ch, new byte[] { 0x21, 0xBB, 0x03 }, p => p.Length >= 4 && p[0] == 0x21 && p[2] == 0x03);
                if (st == null || st[3] == 0x01) return null;
                var p = Exchange(ch, new byte[] { 0x21, 0xBB, 0x0B }, p => p.Length >= 4 && p[0] == 0x21 && p[2] == 0x0B);
                return Pct(p, 3);
            }),
        new("HyperX Cloud Flight", Kingston, new ushort[] { 0x16C4, 0x1723 }, 0, 0, 0,
            (ch, _) =>
            {
                var p = Exchange(ch, new byte[] { 0x21, 0xFF, 0x05 }, p => p.Length >= 5);
                if (p == null) return null;
                int mv = (p[3] << 8) | p[4];
                if (mv > 0x100B) return null; // на зарядке напряжение не отражает уровень
                return CloudFlightPercent(mv);
            }),

        // ---------------- Logitech (HID++ через донгл гарнитуры) ----------------
        LogitechHeadset("Logitech G533", new ushort[] { 0x0A66 }, 0xFF43, 0x0202, 3, 0x07, 0x01,
            new[] { (100, 4200), (50, 3850), (30, 3790), (20, 3750), (5, 3680), (0, 3330) }),
        LogitechHeadset("Logitech G535", new ushort[] { 0x0AC4 }, 0x000C, 0x0001, 3, 0x05, 0x0D,
            new[] { (100, 4175), (50, 3817), (30, 3766), (20, 3730), (5, 3664), (0, 3310) }),
        LogitechHeadset("Logitech G633/G635/G733/G933/G935", new ushort[] { 0x0A5C, 0x0A89, 0x0A5B, 0x0A87, 0x0AB5, 0x0AFE, 0x0B1F },
            0xFF43, 0x0202, 0, 0x08, 0x0A,
            new[] { (100, 4100), (80, 3950), (60, 3850), (40, 3750), (20, 3650), (10, 3500), (5, 3300), (0, 3150) }),
        LogitechHeadset("Logitech G PRO Wireless", new ushort[] { 0x0AA7, 0x0AAA, 0x0ABA, 0x0AFB, 0x0AFC }, 0xFF43, 0x0202, 0, 0x06, 0x0D,
            new[] { (100, 4150), (50, 3830), (30, 3780), (20, 3740), (5, 3670), (0, 3320) }),

        // ---------------- Corsair ----------------
        new("Corsair Void", Corsair, new ushort[]
            { 0x1B1C, 0x1B27, 0x0A14, 0x0A16, 0x0A17, 0x0A1D, 0x0A1A, 0x1B2A, 0x1B23, 0x1B29, 0x0A55, 0x0A51, 0x0A52, 0x0A38, 0x0A4F, 0x0A2B, 0x0A75, 0x0A56 },
            0xFFC5, 0x0001, 3,
            (ch, _) =>
            {
                var p = Exchange(ch, new byte[] { 0xC9, 0x64 }, p => p.Length >= 5);
                if (p == null || p[4] == 0) return null; // [4] = 0 — гарнитура не на связи
                int level = p[2] & 0x7F; // старший бит — положение микрофона
                return level <= 100 ? level : null;
            }),
        new("Corsair Wireless V2", Corsair, new ushort[] { 0x2A08, 0x2A02, 0x0A97 }, 0, 0, 4,
            (ch, _) =>
            {
                // Heartbeat гарнитуры: нет ответа — гарнитура выключена.
                if (Exchange(ch, new byte[] { 0x00, 0x02, 0x09, 0x02, 0x12 }, p => p.Length >= 2) == null) return null;
                for (int i = 0; i < 3; i++)
                {
                    var p = Exchange(ch, new byte[] { 0x00, 0x02, 0x09, 0x02, 0x0F }, p => p.Length >= 6);
                    if (p == null) return null;
                    int v = p[4] | (p[5] << 8); // 1..1000
                    if (v is > 0 and <= 1000) return (int)Math.Round(v / 10.0);
                }
                return null;
            }),
    };

    public static List<BatteryDevice> Read(StringBuilder? log)
    {
        var result = new List<BatteryDevice>();
        var vids = Families.Select(f => f.Vid).Distinct().ToArray();
        var all = vids.SelectMany(v => Hid.Enumerate(v)).ToList();

        foreach (var fam in Families)
        foreach (var pid in fam.Pids)
        {
            var cols = all.Where(i => i.Vid == fam.Vid && i.Pid == pid && i.OutLen > 0 && i.InLen > 0).ToList();
            if (cols.Count == 0) continue;

            var info = fam.UsagePage != 0
                ? cols.FirstOrDefault(i => i.UsagePage == fam.UsagePage && i.Usage == fam.Usage)
                  ?? cols.FirstOrDefault(i => i.Interface == fam.Interface)
                : cols.FirstOrDefault(i => i.Interface == fam.Interface);
            if (info == null) { log?.AppendLine($"  {fam.Name} {pid:X4}: нужная HID-коллекция не найдена"); continue; }

            using var ch = HidChannel.Open(info);
            if (ch == null) { log?.AppendLine($"  {fam.Name} {pid:X4}: не удалось открыть"); continue; }

            int? level;
            try { level = fam.Read(ch, pid); }
            catch (Exception ex) { log?.AppendLine($"  {fam.Name}: ошибка {ex.Message}"); continue; }
            log?.AppendLine($"  {fam.Name} {pid:X4}: {level?.ToString() ?? "нет данных (выключена?)"}");
            if (level is not int l || l < 0 || l > 100) continue;

            string name = fam.Name.Contains('/') ? info.Product ?? fam.Name : fam.Name;
            result.Add(new BatteryDevice(new Guid(MD5.HashData(Encoding.UTF8.GetBytes($"headset:{fam.Vid:x4}:{pid:x4}"))),
                name, l, DeviceKind.Headphones));
        }
        return result;
    }

    // ---------------- Logitech ----------------

    private static Family LogitechHeadset(string name, ushort[] pids, ushort page, ushort usage, int iface,
        byte b1, byte b2, (int Pct, int Mv)[] curve) =>
        new(name, Logitech, pids, page, usage, iface, (ch, _) =>
        {
            var p = Exchange(ch, new byte[] { 0x11, 0xFF, b1, b2 },
                p => p.Length >= 7 && (p[0] == 0x11 || p[0] == 0x10) && p[1] == 0xFF && (p[2] == 0xFF || (p[2] == b1 && p[3] == b2)));
            if (p == null || p[2] == 0xFF) return null; // гарнитура выключена
            int mv = (p[4] << 8) | p[5];
            if (mv < curve[^1].Mv) return null;
            return Interpolate(mv, curve);
        });

    // ---------------- Утилиты ----------------

    /// <summary>Отправляет запрос и ждёт подходящий ответ. Возвращает полезную нагрузку (без нулевого report id).</summary>
    private static byte[]? Exchange(HidChannel ch, byte[] request, Func<byte[], bool> accept, int timeoutMs = 1000)
    {
        if (!ch.Write(request)) return null;
        var r = Hid.ReadMatching(new[] { ch }, d => accept(Payload(d)), timeoutMs);
        return r == null ? null : Payload(r);
    }

    private static byte[] Payload(byte[] d) => d.Length > 0 && d[0] == 0 ? d[1..] : d;

    private static int? Pct(byte[]? p, int index) => p != null && p.Length > index && p[index] <= 100 ? p[index] : null;

    private static int Map(int raw, int min, int max)
    {
        raw = Math.Clamp(raw, min, max);
        return (raw - min) * 100 / (max - min);
    }

    private static int Interpolate(int mv, (int Pct, int Mv)[] curve)
    {
        if (mv >= curve[0].Mv) return 100;
        for (int i = 1; i < curve.Length; i++)
            if (mv >= curve[i].Mv)
            {
                double t = (double)(mv - curve[i].Mv) / (curve[i - 1].Mv - curve[i].Mv);
                return (int)Math.Round(curve[i].Pct + t * (curve[i - 1].Pct - curve[i].Pct));
            }
        return 0;
    }

    /// <summary>Оценка заряда HyperX Cloud Flight по напряжению (полином из HeadsetControl).</summary>
    private static int CloudFlightPercent(int mv)
    {
        if (mv <= 3648) return (int)Math.Round(0.00125 * mv);
        if (mv > 3975) return 100;
        double v = mv;
        double pct = 0.00000002547505 * Math.Pow(v, 4) - 0.0003900299 * Math.Pow(v, 3)
                     + 2.238321 * Math.Pow(v, 2) - 5706.256 * v + 5452299.0;
        return (int)Math.Clamp(Math.Round(pct), 0, 100);
    }
}
