using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar;

/// <summary>
/// Заряд беспроводных устройств Razer (2.4 ГГц-донгл / кабель), которые не видны через Bluetooth.
/// Используется HID feature report Razer (90 байт): команда класса 0x07, id 0x80 — «уровень батареи».
/// </summary>
internal static class RazerReader
{
    private const string RazerVid = "vid_1532";
    private const int ReportLen = 91; // report id + 90 байт
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private static readonly byte[] TransactionIds = { 0x1F, 0x3F, 0xFF };
    private static readonly Dictionary<string, byte> TxCache = new();
    private static readonly object Sync = new();
    private static readonly Dictionary<string, (int Level, DateTime At)> LastKnown = new();
    private static readonly TimeSpan LastKnownTtl = TimeSpan.FromHours(2);

    public static List<BatteryDevice> Read() => Read(null);

    public static List<BatteryDevice> Read(StringBuilder? log)
    {
        lock (Sync)
        {
            var result = new List<BatteryDevice>();
            var paths = GetHidInterfaces().Where(p => p.Contains(RazerVid, StringComparison.OrdinalIgnoreCase)).ToList();

            // Какие PID содержат мышиную коллекцию (usage page 1, usage 2).
            var mousePids = new HashSet<string>();
            var candidates = new List<(string Path, string Pid)>();
            foreach (var path in paths)
            {
                using var h = Open(path);
                if (h.IsInvalid) continue;
                if (!TryGetCaps(h, out ushort usagePage, out ushort usage, out ushort featureLen)) continue;
                string pid = ExtractPid(path);
                if (usagePage == 1 && usage == 2) mousePids.Add(pid);
                if (featureLen == ReportLen) candidates.Add((path, pid));
            }

            var done = new HashSet<string>();
            foreach (var (path, pid) in candidates)
            {
                if (done.Contains(pid)) continue;
                using var h = Open(path);
                if (h.IsInvalid) continue;

                int? level = QueryBattery(h, pid, log);
                log?.AppendLine($"  {pid}: {path} -> {(level?.ToString() ?? "нет ответа")}");
                if (level != null)
                    LastKnown[pid] = (level.Value, DateTime.UtcNow);
                else if (LastKnown.TryGetValue(pid, out var last) && DateTime.UtcNow - last.At < LastKnownTtl)
                    level = last.Level; // мышь уснула — показываем последнее известное значение
                if (level == null) continue;
                done.Add(pid);

                string name = System.Text.RegularExpressions.Regex.Replace(
                    GetProduct(h) ?? "Razer", @"\s+(Dongle|Receiver|Wireless Receiver)$", "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var kind = KindFromName(name) ?? (mousePids.Contains(pid) ? DeviceKind.Mouse : DeviceKind.Generic);
                result.Add(new BatteryDevice(StableGuid("razer:" + pid), name, level.Value, kind));
            }
            return result;
        }
    }

    private static int? QueryBattery(SafeFileHandle h, string pid, StringBuilder? log)
    {
        IEnumerable<byte> order = TxCache.TryGetValue(pid, out var cached)
            ? new[] { cached }.Concat(TransactionIds.Where(t => t != cached))
            : TransactionIds;

        foreach (var tx in order)
        {
            var status = Transact(h, tx, 0x07, 0x80, 0x02, out var resp);
            log?.AppendLine($"  {pid}: tx=0x{tx:X2} status=0x{status:X2} resp={Convert.ToHexString(resp, 0, 12)}");
            if (status == 0x02)
            {
                TxCache[pid] = tx;
                return (int)Math.Round(resp[1 + 9] * 100 / 255.0);
            }
            // 0x05 — команда не поддерживается. 0x04 (timeout) бывает и при неверном transaction id,
            // и когда мышь спит — поэтому перебираем дальше.
            if (status == 0x05) return null;
        }
        return null;
    }

    /// <returns>Статус ответа (0x02 — успех) или 0xFF/0x00 при ошибке.</returns>
    private static byte Transact(SafeFileHandle h, byte tx, byte cmdClass, byte cmdId, byte dataSize, out byte[] resp)
    {
        resp = new byte[ReportLen];
        var req = new byte[ReportLen];
        const int o = 1; // смещение после report id
        req[o + 1] = tx;
        req[o + 5] = dataSize;
        req[o + 6] = cmdClass;
        req[o + 7] = cmdId;
        byte crc = 0;
        for (int i = 2; i < 88; i++) crc ^= req[o + i];
        req[o + 88] = crc;

        // Razer Synapse общается с устройством параллельно, и в ответ может прийти результат его команды.
        // Поэтому повторяем запрос, пока не получим ответ именно на наш (класс/id/transaction id).
        byte lastStatus = 0x00;
        for (int send = 0; send < 6; send++)
        {
            if (!HidD_SetFeature(h, req, ReportLen)) return 0xFF;

            for (int attempt = 0; attempt < 8; attempt++)
            {
                Thread.Sleep(attempt == 0 ? 60 : 30);
                Array.Clear(resp);
                if (!HidD_GetFeature(h, resp, ReportLen)) return 0xFF;
                byte status = resp[o];
                if (status == 0x01) continue; // busy — ждём
                if (resp[o + 6] != cmdClass || resp[o + 7] != cmdId || resp[o + 1] != tx)
                    break; // чужой ответ — отправим запрос заново
                return status;
            }
            Thread.Sleep(30 + send * 20);
        }
        return lastStatus;
    }

    private static DeviceKind? KindFromName(string name)
    {
        string n = name.ToLowerInvariant();
        string[] mice = { "viper", "deathadder", "basilisk", "naga", "mamba", "orochi", "atheris", "lancehead", "pro click", "cobra", "mouse" };
        string[] keyboards = { "blackwidow", "huntsman", "ornata", "deathstalker", "cynosa", "pro type", "keyboard" };
        string[] audio = { "barracuda", "kraken", "blackshark", "nari", "hammerhead", "headset" };
        string[] pads = { "wolverine", "kishi", "raiju", "controller" };
        if (mice.Any(n.Contains)) return DeviceKind.Mouse;
        if (keyboards.Any(n.Contains)) return DeviceKind.Keyboard;
        if (audio.Any(n.Contains)) return DeviceKind.Headphones;
        if (pads.Any(n.Contains)) return DeviceKind.Gamepad;
        return null;
    }

    private static string ExtractPid(string path)
    {
        int i = path.IndexOf("pid_", StringComparison.OrdinalIgnoreCase);
        return i >= 0 && i + 8 <= path.Length ? path.Substring(i, 8).ToLowerInvariant() : path;
    }

    private static Guid StableGuid(string s) => new(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    // ---------------- Win32 / HID ----------------

    private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll")] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, [Out] ushort[] caps);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(SafeFileHandle h, [Out] byte[] buf, int len);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid cls, string? devId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string? devId, [Out] char[] buf, uint len, uint flags);

    // Открываем без прав чтения/записи: мыши и клавиатуры система держит эксклюзивно,
    // но feature-репорты так работают.
    private static SafeFileHandle Open(string path) =>
        CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    private static bool TryGetCaps(SafeFileHandle h, out ushort usagePage, out ushort usage, out ushort featureLen)
    {
        usagePage = usage = featureLen = 0;
        if (!HidD_GetPreparsedData(h, out var pp)) return false;
        try
        {
            var caps = new ushort[32]; // HIDP_CAPS = 64 байта
            if (HidP_GetCaps(pp, caps) != 0x00110000) return false;
            usage = caps[0];
            usagePage = caps[1];
            featureLen = caps[4];
            return true;
        }
        finally { HidD_FreePreparsedData(pp); }
    }

    private static string? GetProduct(SafeFileHandle h)
    {
        var buf = new byte[256];
        if (!HidD_GetProductString(h, buf, buf.Length)) return null;
        var s = Encoding.Unicode.GetString(buf).Split('\0')[0].Trim();
        return s.Length > 0 ? s : null;
    }

    private static IEnumerable<string> GetHidInterfaces()
    {
        var guid = HidInterfaceGuid;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, null, 0) != 0 || len <= 1) break;
            var buf = new char[len];
            int cr = CM_Get_Device_Interface_ListW(ref guid, null, buf, len, 0);
            if (cr == 0x1A) continue;
            if (cr != 0) break;
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        return Array.Empty<string>();
    }
}
