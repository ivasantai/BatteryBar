using System.Security.Cryptography;
using System.Text;

namespace BatteryBar;

/// <summary>
/// Logitech HID++ 2.0: приёмники Lightspeed / Unifying / Bolt, устройства, подключённые кабелем, и по Bluetooth.
/// Заряд берётся из одной из фич: 0x1004 UNIFIED_BATTERY, 0x1000 BATTERY_STATUS, 0x1001 BATTERY_VOLTAGE.
/// </summary>
internal static class LogitechReader
{
    private const ushort Vid = 0x046D;
    private const byte SoftwareId = 0x0B; // отличает наши ответы от ответов G HUB / Options+
    private const int TimeoutMs = 400;

    private const ushort FeatRoot = 0x0000, FeatDeviceName = 0x0005,
        FeatBatteryStatus = 0x1000, FeatBatteryVoltage = 0x1001, FeatUnifiedBattery = 0x1004;

    // Известные приёмники (у остальных PID считаем, что устройство подключено напрямую).
    private static readonly HashSet<ushort> ReceiverPids = new()
    {
        0xC52B, 0xC532,                         // Unifying
        0xC52F, 0xC526, 0xC52E, 0xC534, 0xC542, // Nano
        0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC54D, // Lightspeed
        0xC548,                                 // Bolt
    };

    private sealed class Cached
    {
        public string? Name;
        public DeviceKind Kind = DeviceKind.Mouse;
        public ushort BatteryFeature;
        public byte BatteryIndex;
    }

    private static readonly Dictionary<string, Cached> Cache = new();
    private static readonly object Sync = new();

    public static List<BatteryDevice> Read(StringBuilder? log)
    {
        lock (Sync)
        {
            var result = new List<BatteryDevice>();
            var groups = Hid.Enumerate(Vid)
                .Where(i => i.UsagePage == 0xFF00 && (i.Usage == 0x01 || i.Usage == 0x02))
                .GroupBy(i => i.DeviceKey);

            foreach (var g in groups)
            {
                var longInfo = g.FirstOrDefault(i => i.Usage == 0x02);
                var shortInfo = g.FirstOrDefault(i => i.Usage == 0x01);
                if (longInfo == null) continue;

                using var longCh = HidChannel.Open(longInfo);
                using var shortCh = shortInfo != null ? HidChannel.Open(shortInfo) : null;
                if (longCh == null) { log?.AppendLine($"  {g.Key}: не удалось открыть"); continue; }

                var channels = shortCh != null ? new[] { longCh, shortCh } : new[] { longCh };
                bool receiver = ReceiverPids.Contains(longInfo.Pid) ||
                                (longInfo.Product?.Contains("receiver", StringComparison.OrdinalIgnoreCase) ?? false);
                byte[] indices = receiver ? new byte[] { 1, 2, 3, 4, 5, 6 } : new byte[] { 0xFF };
                log?.AppendLine($"  {longInfo.Product} pid={longInfo.Pid:X4} {(receiver ? "приёмник" : "напрямую")}");

                foreach (var idx in indices)
                {
                    var dev = QueryDevice(longCh, channels, longInfo, idx, log);
                    if (dev != null) result.Add(dev);
                }
            }

            // Bluetooth (MX Master / Anywhere / Keys, Lift, POP и т.д.): HID++ идёт через коллекцию FF43:0202,
            // только длинные репорты, обращение напрямую к устройству (индекс 0xFF).
            // USB-донглы гарнитур Logitech используют похожую коллекцию — их обрабатывает HeadsetReader,
            // поэтому здесь берём только устройства, подключённые по Bluetooth.
            foreach (var info in Hid.Enumerate(Vid).Where(i => i.UsagePage == 0xFF43 && i.Usage == 0x0202 && IsBluetooth(i.Path)))
            {
                if (info.OutLen < 20 || info.InLen < 20) continue;
                using var ch = HidChannel.Open(info);
                if (ch == null) { log?.AppendLine($"  BT {info.Pid:X4}: не удалось открыть"); continue; }
                log?.AppendLine($"  {info.Product ?? "Logitech"} pid={info.Pid:X4} по Bluetooth");

                // Реальный ContainerId: по нему устройство сопоставляется с Bluetooth-записью Windows
                // (не показывать дважды и не опрашивать отключённое).
                var dev = QueryDevice(ch, new[] { ch }, info, 0xFF, log, Hid.GetContainerId(info.Path));
                if (dev != null) result.Add(dev);
            }
            return result;
        }
    }

    /// <summary>HID-устройство подключено по Bluetooth: LE (HOGP, сервис 0x1812) или классический HID (0x1124).</summary>
    private static bool IsBluetooth(string path) =>
        path.Contains("00001812-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase) ||
        path.Contains("00001124-0000-1000-8000-00805f9b34fb", StringComparison.OrdinalIgnoreCase);

    private static BatteryDevice? QueryDevice(HidChannel longCh, HidChannel[] channels, HidInfo info, byte idx,
        StringBuilder? log, Guid? container = null)
    {
        string key = $"{info.DeviceKey}#{idx}";
        if (!Cache.TryGetValue(key, out var c))
        {
            c = new Cached();
            // Ищем фичу батареи в порядке предпочтения.
            foreach (var f in new[] { FeatUnifiedBattery, FeatBatteryStatus, FeatBatteryVoltage })
            {
                var r = Request(longCh, channels, idx, 0x00, 0, (byte)(f >> 8), (byte)f);
                if (r == null) { log?.AppendLine($"    #{idx}: нет ответа (не сопряжено или спит)"); return null; }
                if (r[0] != 0) { c.BatteryFeature = f; c.BatteryIndex = r[0]; break; }
            }
            if (c.BatteryIndex == 0) { log?.AppendLine($"    #{idx}: нет фичи батареи"); return null; }

            (c.Name, c.Kind) = ReadNameAndKind(longCh, channels, idx);
            Cache[key] = c;
        }

        int? level = ReadLevel(longCh, channels, idx, c);
        log?.AppendLine($"    #{idx}: {c.Name} фича=0x{c.BatteryFeature:X4} заряд={level?.ToString() ?? "?"}");
        if (level == null)
        {
            Cache.Remove(key); // при следующем опросе переопределим (могли переподключить другое устройство)
            return null;
        }
        return new BatteryDevice(container ?? StableGuid(key), c.Name ?? "Logitech", level.Value, c.Kind);
    }

    private static int? ReadLevel(HidChannel longCh, HidChannel[] channels, byte idx, Cached c)
    {
        switch (c.BatteryFeature)
        {
            case FeatUnifiedBattery:
            {
                var r = Request(longCh, channels, idx, c.BatteryIndex, 1); // get_status
                if (r == null) return null;
                if (r[0] is > 0 and <= 100) return r[0];
                // Устройство отдаёт только уровень-флаг: 8 полный, 4 хороший, 2 низкий, 1 критический.
                return (r[1] & 0x08) != 0 ? 100 : (r[1] & 0x04) != 0 ? 50 : (r[1] & 0x02) != 0 ? 20 : (r[1] & 0x01) != 0 ? 5 : null;
            }
            case FeatBatteryStatus:
            {
                var r = Request(longCh, channels, idx, c.BatteryIndex, 0); // GetBatteryLevelStatus
                return r != null && r[0] <= 100 ? r[0] : null;
            }
            case FeatBatteryVoltage:
            {
                var r = Request(longCh, channels, idx, c.BatteryIndex, 0); // GetBatteryVoltage
                if (r == null) return null;
                int mv = (r[0] << 8) | r[1];
                return mv > 0 ? VoltageToPercent(mv) : null;
            }
        }
        return null;
    }

    private static (string? Name, DeviceKind Kind) ReadNameAndKind(HidChannel longCh, HidChannel[] channels, byte idx)
    {
        var fi = Request(longCh, channels, idx, 0x00, 0, (byte)(FeatDeviceName >> 8), (byte)FeatDeviceName);
        if (fi == null || fi[0] == 0) return (null, DeviceKind.Mouse);
        byte f = fi[0];

        string? name = null;
        var count = Request(longCh, channels, idx, f, 0);
        if (count != null && count[0] > 0)
        {
            var sb = new StringBuilder();
            for (int pos = 0; pos < count[0];)
            {
                var part = Request(longCh, channels, idx, f, 1, (byte)pos);
                if (part == null) break;
                int take = Math.Min(Math.Min(16, count[0] - pos), part.Length);
                if (take <= 0) break;
                sb.Append(Encoding.ASCII.GetString(part, 0, take));
                pos += take;
            }
            name = sb.ToString().Replace("\0", "").Trim();
        }

        var type = Request(longCh, channels, idx, f, 2);
        var kind = type?[0] switch
        {
            0 or 2 => DeviceKind.Keyboard,
            8 => DeviceKind.Headphones,
            11 or 12 => DeviceKind.Gamepad,
            _ => DeviceKind.Mouse,
        };
        if (!string.IsNullOrEmpty(name) && !name.StartsWith("Logitech", StringComparison.OrdinalIgnoreCase))
            name = "Logitech " + name;
        return (string.IsNullOrEmpty(name) ? null : name, kind);
    }

    /// <summary>HID++ 2.0 запрос. Возвращает параметры ответа или null (ошибка/таймаут).</summary>
    private static byte[]? Request(HidChannel longCh, HidChannel[] channels, byte dev, byte feature, byte function, params byte[] args)
    {
        var req = new byte[20];
        req[0] = 0x11; // long report
        req[1] = dev;
        req[2] = feature;
        req[3] = (byte)((function << 4) | SoftwareId);
        Array.Copy(args, 0, req, 4, Math.Min(args.Length, 16));
        if (!longCh.Write(req)) return null;

        bool ok = false;
        var resp = Hid.ReadMatching(channels, d =>
        {
            if (d.Length < 5 || (d[0] != 0x10 && d[0] != 0x11) || d[1] != dev) return false;
            if (d[2] == feature && d[3] == req[3]) { ok = true; return true; }            // успех
            if ((d[2] == 0xFF || d[2] == 0x8F) && d[3] == feature && d[4] == req[3]) return true; // ошибка HID++ 2.0 / 1.0
            return false;
        }, TimeoutMs);

        return ok && resp != null ? resp[4..] : null;
    }

    /// <summary>Примерная кривая разряда: Li-ion (мыши G-серии) или батарейка AA (G305 и т.п.).</summary>
    private static int VoltageToPercent(int mv)
    {
        int[,] table = mv < 2500
            ? new[,] { { 1500, 100 }, { 1400, 80 }, { 1300, 50 }, { 1200, 25 }, { 1100, 5 }, { 1000, 0 } }
            : new[,]
            {
                { 4186, 100 }, { 4067, 90 }, { 3989, 80 }, { 3922, 70 }, { 3859, 60 }, { 3811, 50 },
                { 3778, 40 }, { 3750, 30 }, { 3717, 20 }, { 3671, 10 }, { 3500, 0 },
            };
        if (mv >= table[0, 0]) return 100;
        for (int i = 1; i < table.GetLength(0); i++)
        {
            if (mv >= table[i, 0])
            {
                double t = (double)(mv - table[i, 0]) / (table[i - 1, 0] - table[i, 0]);
                return (int)Math.Round(table[i, 1] + t * (table[i - 1, 1] - table[i, 1]));
            }
        }
        return 0;
    }

    private static Guid StableGuid(string s) => new(MD5.HashData(Encoding.UTF8.GetBytes("logitech:" + s)));
}
