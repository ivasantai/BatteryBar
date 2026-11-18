using System.Runtime.InteropServices;

namespace BatteryBar;

/// <summary>Отрисованная строка: маска покрытия (0..255) и метрики.</summary>
internal sealed class GlyphRun
{
    public int Width, Height;     // размер маски
    public int OriginX;           // где в маске начинается перо (отступ слева)
    public int Advance;           // ширина строки для раскладки
    public int InkTop, InkBottom; // вертикальные границы реально закрашенных пикселей
    public int InkLeft, InkRight; // горизонтальные границы
    public byte[] Coverage = Array.Empty<byte>();
}

/// <summary>
/// Рисует текст и иконки через GDI с ClearType-хинтингом — так же чётко, как системный текст.
/// Белый текст рисуется на чёрном фоне, яркость пикселя становится альфой, после чего маска
/// накладывается нужным цветом на прозрачный буфер для UpdateLayeredWindow.
/// </summary>
internal static class GlyphRenderer
{
    private const int CLEARTYPE_QUALITY = 5;
    private const int OUT_TT_PRECIS = 4;
    private const int DEFAULT_CHARSET = 1;
    private const int TRANSPARENT = 1;

    public static GlyphRun Render(string text, string face, int pixelHeight, int weight = 400)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr font = CreateFontW(-pixelHeight, 0, 0, 0, weight, 0, 0, 0, DEFAULT_CHARSET, OUT_TT_PRECIS, 0,
            CLEARTYPE_QUALITY, 0, face);
        IntPtr oldFont = SelectObject(dc, font);
        IntPtr dib = IntPtr.Zero, oldBmp = IntPtr.Zero;
        try
        {
            GetTextExtentPoint32W(dc, text, text.Length, out var ext);
            int pad = Math.Max(2, pixelHeight / 3); // запас под выносные элементы глифов
            int w = ext.cx + pad * 2, h = ext.cy + pad * 2;

            var bmi = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -h, // top-down
                biPlanes = 1,
                biBitCount = 32,
            };
            dib = CreateDIBSection(dc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
            oldBmp = SelectObject(dc, dib);

            SetBkMode(dc, TRANSPARENT);
            SetTextColor(dc, 0x00FFFFFF);
            TextOutW(dc, pad, pad, text, text.Length);
            GdiFlush();

            var raw = new byte[w * h * 4];
            Marshal.Copy(bits, raw, 0, raw.Length);

            var run = new GlyphRun
            {
                Width = w, Height = h, OriginX = pad, Advance = ext.cx,
                Coverage = new byte[w * h],
                InkTop = h, InkBottom = -1, InkLeft = w, InkRight = -1,
            };
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                // ClearType даёт покрытие по субпикселям — берём среднее (B, G, R).
                int c = (raw[i] + raw[i + 1] + raw[i + 2] + 1) / 3;
                if (c == 0) continue;
                run.Coverage[y * w + x] = (byte)c;
                if (y < run.InkTop) run.InkTop = y;
                if (y > run.InkBottom) run.InkBottom = y;
                if (x < run.InkLeft) run.InkLeft = x;
                if (x > run.InkRight) run.InkRight = x;
            }
            if (run.InkBottom < 0) { run.InkTop = run.InkBottom = h / 2; run.InkLeft = run.InkRight = pad; }
            return run;
        }
        finally
        {
            if (oldBmp != IntPtr.Zero) SelectObject(dc, oldBmp);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            SelectObject(dc, oldFont);
            DeleteObject(font);
            DeleteDC(dc);
        }
    }

    /// <summary>Накладывает маску цветом <paramref name="color"/> на premultiplied BGRA-буфер.</summary>
    public static void Composite(byte[] dst, int dstW, int dstH, GlyphRun run, int left, int top, Color color)
    {
        for (int y = 0; y < run.Height; y++)
        {
            int dy = top + y;
            if (dy < 0 || dy >= dstH) continue;
            for (int x = 0; x < run.Width; x++)
            {
                int cov = run.Coverage[y * run.Width + x];
                if (cov == 0) continue;
                int dx = left + x;
                if (dx < 0 || dx >= dstW) continue;

                int a = cov * color.A / 255;
                int i = (dy * dstW + dx) * 4;
                int inv = 255 - a;
                dst[i + 0] = (byte)(color.B * a / 255 + dst[i + 0] * inv / 255);
                dst[i + 1] = (byte)(color.G * a / 255 + dst[i + 1] * inv / 255);
                dst[i + 2] = (byte)(color.R * a / 255 + dst[i + 2] * inv / 255);
                dst[i + 3] = (byte)(a + dst[i + 3] * inv / 255);
            }
        }
    }

    /// <summary>Создаёт 32-битный DIB с premultiplied-пикселями (для UpdateLayeredWindow).</summary>
    public static IntPtr CreateDib(byte[] pixels, int w, int h)
    {
        var bmi = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dib = CreateDIBSection(IntPtr.Zero, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (dib != IntPtr.Zero) Marshal.Copy(pixels, 0, bits, pixels.Length);
        return dib;
    }

    // ---------------- GDI ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int h, int w, int esc, int orient, int weight, uint italic, uint underline,
        uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool TextOutW(IntPtr dc, int x, int y, string text, int len);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr dc, string text, int len, out SIZE size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits,
        IntPtr section, uint offset);
}
