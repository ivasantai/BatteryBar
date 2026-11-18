using System.Runtime.InteropServices;

namespace BatteryBar;

/// <summary>
/// Простая подсказка над виджетом. Своё окно вместо стандартного ToolTip: тот у неактивируемого
/// layered-окна иногда не показывался или, наоборот, не прятался.
/// </summary>
internal sealed class TipWindow : Form
{
    private string _text = "";
    private const int PadX = 10, PadY = 7;

    public TipWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        Text = "BatteryBarTip";
        Font = SystemFonts.StatusFont ?? SystemFonts.DefaultFont;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE
                          | 0x20 /* WS_EX_TRANSPARENT: не перехватывает мышь */;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 3; // DWMWCP_ROUNDSMALL (Windows 11; на Windows 10 игнорируется)
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    /// <summary>Показывает подсказку над прямоугольником <paramref name="anchor"/> (экранные координаты).</summary>
    public void ShowAbove(string text, Rectangle anchor)
    {
        _text = text;
        var size = TextRenderer.MeasureText(text, Font);
        int w = size.Width + PadX * 2, h = size.Height + PadY * 2;

        var screen = Screen.FromRectangle(anchor).Bounds;
        int x = Math.Clamp(anchor.Left + (anchor.Width - w) / 2, screen.Left, screen.Right - w);
        int y = anchor.Top - h - 8;
        if (y < screen.Top) y = anchor.Bottom + 8;

        bool light = Settings.TaskbarIsLight;
        BackColor = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(43, 43, 43);
        ForeColor = light ? Color.Black : Color.White;

        Bounds = new Rectangle(x, y, w, h);
        if (!Visible) Show();
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool light = Settings.TaskbarIsLight;
        using var border = new Pen(light ? Color.FromArgb(229, 229, 229) : Color.FromArgb(70, 70, 70));
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(e.Graphics, _text, Font, new Point(PadX, PadY), ForeColor);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
