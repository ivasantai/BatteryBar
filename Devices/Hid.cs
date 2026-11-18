using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace BatteryBar;

/// <summary>Одна HID-коллекция (top-level collection) устройства.</summary>
internal sealed record HidInfo(
    string Path, ushort Vid, ushort Pid, int Interface,
    ushort UsagePage, ushort Usage, int InLen, int OutLen, int FeatureLen, string? Product)
{
    /// <summary>Ключ физического USB-интерфейса: коллекции одного интерфейса отличаются только &amp;colNN.</summary>
    public string DeviceKey
    {
        get
        {
            var p = Path.ToLowerInvariant();
            int start = p.IndexOf("hid#", StringComparison.Ordinal);
            int end = start >= 0 ? p.IndexOf('#', start + 4) : -1;
            if (end < 0) return $"{Vid:x4}:{Pid:x4}:{Interface}";
            // \\?\hid#vid_xxxx&pid_xxxx&mi_xx[&colxx]#<экземпляр>#{guid} — экземпляр у коллекций разный,
            // поэтому ключ — первый сегмент без &colxx.
            return Regex.Replace(p[..end], @"&col[0-9a-f]+", "");
        }
    }
}

internal static class Hid
{
    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    /// <summary>Все HID-коллекции с указанным VID (null — любые).</summary>
    public static List<HidInfo> Enumerate(ushort? vid = null)
    {
        var result = new List<HidInfo>();
        foreach (var path in GetInterfacePaths())
        {
            if (vid != null && !path.Contains($"vid_{vid:x4}", StringComparison.OrdinalIgnoreCase)) continue;
            using var h = OpenNoAccess(path);
            if (h.IsInvalid) continue;

            var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(h, ref attr)) continue;
            if (!HidD_GetPreparsedData(h, out var pp)) continue;
            var caps = new ushort[32];
            int st;
            try { st = HidP_GetCaps(pp, caps); }
            finally { HidD_FreePreparsedData(pp); }
            if (st != 0x00110000) continue;

            var m = Regex.Match(path, @"mi_([0-9a-f]{2})", RegexOptions.IgnoreCase);
            int iface = m.Success ? Convert.ToInt32(m.Groups[1].Value, 16) : -1;

            result.Add(new HidInfo(path, attr.VendorID, attr.ProductID, iface,
                UsagePage: caps[1], Usage: caps[0], InLen: caps[2], OutLen: caps[3], FeatureLen: caps[4],
                Product: GetProduct(h)));
        }
        return result;
    }

    // Открытие без прав чтения/записи: подходит для feature-репортов даже у мышей/клавиатур,
    // которые система держит эксклюзивно.
    public static SafeFileHandle OpenNoAccess(string path) =>
        CreateFileW(path, 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    /// <summary>Выходной репорт через control-канал (HidD_SetOutputReport) — нужен устройствам,
    /// которые не слушают interrupt OUT.</summary>
    public static bool SetOutputReport(string path, byte[] buf)
    {
        using var h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        return !h.IsInvalid && HidD_SetOutputReport(h, buf, buf.Length);
    }

    public static bool SetFeature(SafeFileHandle h, byte[] buf) => HidD_SetFeature(h, buf, buf.Length);
    public static bool GetFeature(SafeFileHandle h, byte[] buf) => HidD_GetFeature(h, buf, buf.Length);

    private static string? GetProduct(SafeFileHandle h)
    {
        var buf = new byte[256];
        if (!HidD_GetProductString(h, buf, buf.Length)) return null;
        var s = Encoding.Unicode.GetString(buf).Split('\0')[0].Trim();
        return s.Length > 0 ? s : null;
    }

    private static IEnumerable<string> GetInterfacePaths()
    {
        var guid = HidInterfaceGuid;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (CM_Get_Device_Interface_List_SizeW(out uint len, ref guid, null, 0) != 0 || len <= 1) break;
            var buf = new char[len];
            int cr = CM_Get_Device_Interface_ListW(ref guid, null, buf, len, 0);
            if (cr == 0x1A) continue; // список изменился — повторяем
            if (cr != 0) break;
            return new string(buf).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        return Array.Empty<string>();
    }

    /// <summary>
    /// Ждёт входной репорт, удовлетворяющий <paramref name="match"/>, с любого из каналов.
    /// Чужие репорты (уведомления, ответы другим программам) пропускаются.
    /// </summary>
    public static byte[]? ReadMatching(IReadOnlyList<HidChannel> channels, Func<byte[], bool> match, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        var alive = channels.ToList();
        while (alive.Count > 0)
        {
            int remaining = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (remaining <= 0) return null;

            foreach (var c in alive.ToList())
                if (!c.BeginRead()) alive.Remove(c);
            if (alive.Count == 0) return null;

            int i = WaitHandle.WaitAny(alive.Select(c => (WaitHandle)c.ReadEvent).ToArray(), remaining);
            if (i == WaitHandle.WaitTimeout) return null;

            var data = alive[i].EndRead();
            if (data == null) { alive.RemoveAt(i); continue; }
            if (match(data)) return data;
        }
        return null;
    }

    // ---------------- Win32 ----------------

    internal const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    internal const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES { public int Size; public ushort VendorID, ProductID, VersionNumber; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(SafeFileHandle h, ref HIDD_ATTRIBUTES attr);
    [DllImport("hid.dll")] private static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_SetOutputReport(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr data);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr data);
    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr data, [Out] ushort[] caps);
    [DllImport("hid.dll")] private static extern bool HidD_GetProductString(SafeFileHandle h, [Out] byte[] buf, int len);
    [DllImport("hid.dll")] internal static extern bool HidD_FlushQueue(SafeFileHandle h);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_List_SizeW(out uint len, ref Guid cls, string? devId, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_Interface_ListW(ref Guid cls, string? devId, [Out] char[] buf, uint len, uint flags);
}

/// <summary>
/// Канал обмена входными/выходными репортами с HID-коллекцией (overlapped I/O с таймаутами).
/// </summary>
internal sealed class HidChannel : IDisposable
{
    public HidInfo Info { get; }
    public ManualResetEvent ReadEvent { get; } = new(false);

    private readonly SafeFileHandle _h;
    private readonly ManualResetEvent _writeEvent = new(false);
    private readonly IntPtr _readOvl, _writeOvl, _readBuf;
    private bool _readPending;

    private HidChannel(HidInfo info, SafeFileHandle h)
    {
        Info = info;
        _h = h;
        _readOvl = Marshal.AllocHGlobal(OverlappedSize);
        _writeOvl = Marshal.AllocHGlobal(OverlappedSize);
        _readBuf = Marshal.AllocHGlobal(Math.Max(1, info.InLen));
    }

    public static HidChannel? Open(HidInfo info)
    {
        if (info.InLen <= 0 && info.OutLen <= 0) return null;
        var h = Hid.CreateFileW(info.Path, Hid.GENERIC_READ | Hid.GENERIC_WRITE,
            Hid.FILE_SHARE_READ | Hid.FILE_SHARE_WRITE, IntPtr.Zero, Hid.OPEN_EXISTING, Hid.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        if (h.IsInvalid) { h.Dispose(); return null; }
        var c = new HidChannel(info, h);
        Hid.HidD_FlushQueue(h); // выбрасываем накопившиеся старые репорты
        return c;
    }

    /// <summary>Отправляет выходной репорт (дополняется нулями до длины репорта).</summary>
    public bool Write(byte[] report, int timeoutMs = 500)
    {
        if (Info.OutLen <= 0) return false;
        var buf = new byte[Info.OutLen];
        Array.Copy(report, buf, Math.Min(report.Length, buf.Length));

        var pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            PrepareOverlapped(_writeOvl, _writeEvent);
            if (!WriteFile(_h, pin.AddrOfPinnedObject(), buf.Length, IntPtr.Zero, _writeOvl) &&
                Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
                return false;
            if (!_writeEvent.WaitOne(timeoutMs))
            {
                CancelIoEx(_h, _writeOvl);
                GetOverlappedResult(_h, _writeOvl, out _, true);
                return false;
            }
            return GetOverlappedResult(_h, _writeOvl, out _, false);
        }
        finally { pin.Free(); }
    }

    /// <summary>Запускает асинхронное чтение, если оно ещё не идёт.</summary>
    public bool BeginRead()
    {
        if (_readPending) return true;
        if (Info.InLen <= 0) return false;
        PrepareOverlapped(_readOvl, ReadEvent);
        if (!ReadFile(_h, _readBuf, Info.InLen, IntPtr.Zero, _readOvl) &&
            Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
            return false;
        _readPending = true;
        return true;
    }

    /// <summary>Забирает результат завершившегося чтения (первый байт — report id).</summary>
    public byte[]? EndRead()
    {
        _readPending = false;
        if (!GetOverlappedResult(_h, _readOvl, out int n, false) || n <= 0) return null;
        var data = new byte[n];
        Marshal.Copy(_readBuf, data, 0, n);
        return data;
    }

    public void Dispose()
    {
        if (_readPending)
        {
            CancelIoEx(_h, _readOvl);
            GetOverlappedResult(_h, _readOvl, out _, true);
            _readPending = false;
        }
        _h.Dispose();
        Marshal.FreeHGlobal(_readOvl);
        Marshal.FreeHGlobal(_writeOvl);
        Marshal.FreeHGlobal(_readBuf);
        ReadEvent.Dispose();
        _writeEvent.Dispose();
    }

    // OVERLAPPED на x64: Internal(8) InternalHigh(8) Offset(4) OffsetHigh(4) hEvent(8)
    private const int OverlappedSize = 32;
    private const int ERROR_IO_PENDING = 997;

    private static void PrepareOverlapped(IntPtr ovl, ManualResetEvent evt)
    {
        for (int i = 0; i < OverlappedSize; i += 8) Marshal.WriteInt64(ovl, i, 0);
        evt.Reset();
        Marshal.WriteIntPtr(ovl, 24, evt.SafeWaitHandle.DangerousGetHandle());
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle h, IntPtr buf, int len, IntPtr read, IntPtr ovl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(SafeFileHandle h, IntPtr buf, int len, IntPtr written, IntPtr ovl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle h, IntPtr ovl, out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle h, IntPtr ovl);
}
