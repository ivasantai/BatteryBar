using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BatteryBar;

/// <summary>
/// Значок в области уведомлений: всегда доступное меню (даже когда все устройства спят и виджет скрыт).
/// Рисуется батарейка, заполненная по самому низкому заряду среди показываемых устройств.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private IntPtr _hIcon;
    private string? _iconKey;

    public TrayIcon(ContextMenuStrip menu, Action onDoubleClick)
    {
        _icon.ContextMenuStrip = menu;
        _icon.Text = "BatteryBar";
        _icon.DoubleClick += (_, _) => onDoubleClick();
    }

    public void Update(IReadOnlyList<BatteryDevice> all, IReadOnlyList<BatteryDevice> visible, Func<BatteryDevice, string> line)
    {
        int? min = visible.Count > 0 ? visible.Min(d => d.Level) : null;
        bool light = Settings.TaskbarIsLight;
        int size = SystemInformation.SmallIconSize.Width;

        string key = $"{min}|{light}|{size}";
        if (key != _iconKey)
        {
            var old = _hIcon;
            _hIcon = Draw(size, min, light);
            _icon.Icon = Icon.FromHandle(_hIcon);
            if (old != IntPtr.Zero) DestroyIcon(old);
            _iconKey = key;
        }

        string text = all.Count == 0
            ? "BatteryBar — нет устройств"
            : string.Join("\n", all.Select(line));
        _icon.Text = text.Length <= 127 ? text : text[..126] + "…"; // ограничение Windows
        _icon.Visible = true;
    }

    private static IntPtr Draw(int size, int? level, bool light)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = size / 16f;
            Color fg = light ? Color.Black : Color.White;
            Color low = light ? Color.FromArgb(196, 43, 28) : Color.FromArgb(255, 120, 120);

            // Корпус батарейки и «плюсовой» контакт.
            using var pen = new Pen(fg, 1.2f * s);
            var body = new RectangleF(1.5f * s, 4.5f * s, 11.5f * s, 7f * s);
            using (var path = Rounded(body, 1.5f * s)) g.DrawPath(pen, path);
            using (var brush = new SolidBrush(fg))
                g.FillRectangle(brush, 13.4f * s, 6.6f * s, 1.6f * s, 2.8f * s);

            // Заливка по уровню заряда.
            if (level is int l && l > 0)
            {
                float w = 8.5f * s * Math.Clamp(l, 4, 100) / 100f;
                using var fill = new SolidBrush(l <= 15 ? low : fg);
                g.FillRectangle(fill, 3f * s, 6f * s, w, 4f * s);
            }
        }
        return bmp.GetHicon();
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
}
