using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BatteryBar;

/// <summary>
/// Прозрачное окно поверх панели задач слева от области уведомлений:
/// «[иконка] 30%  [иконка] 93%».
/// </summary>
internal sealed class TaskbarWidget : Form
{
    private const int PollIntervalMs = 30_000;   // опрос заряда
    private const int PlacementIntervalMs = 1_000; // слежение за панелью задач

    private readonly Settings _settings = Settings.Load();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = PollIntervalMs };
    private readonly System.Windows.Forms.Timer _placementTimer = new() { Interval = PlacementIntervalMs };
    private readonly System.Windows.Forms.Timer _debounceTimer = new() { Interval = 2_000 };
    private readonly ToolTip _toolTip = new() { InitialDelay = 400, ShowAlways = true };
    private readonly ContextMenuStrip _menu = new();
    private readonly List<DeviceWatcher> _watchers = new();

    private List<BatteryDevice> _devices = new();
    private bool _refreshing;
    private bool _hover;
    private bool _shown;
    private Size _size = Size.Empty;
    private Point _location;

    private bool _dragging;
    private bool _dragMoved;
    private int _dragStartCursorX, _dragStartLeft;

    private IntPtr _winEventHook;
    private readonly Native.WinEventDelegate _winEventProc; // держим ссылку, чтобы GC не собрал делегат

    private static readonly string IconFontName = FirstInstalled("Segoe Fluent Icons", "Segoe MDL2 Assets");
    private static readonly string TextFontName = FirstInstalled("Segoe UI Variable Text", "Segoe UI");

    public TaskbarWidget()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        Text = "BatteryBar";
        Size = new Size(1, 1);
        Location = new Point(-32000, -32000);

        _winEventProc = (_, _, _, _, _, _, _) => BeginInvoke(() => { UpdatePlacement(); BringToTop(); });

        _pollTimer.Tick += (_, _) => RefreshDevices();
        _placementTimer.Tick += (_, _) => { UpdatePlacement(); BringToTop(); };
        _debounceTimer.Tick += (_, _) => { _debounceTimer.Stop(); RefreshDevices(); };

        BuildMenu();
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        MouseEnter += (_, _) => { _hover = true; Render(); };
        MouseLeave += (_, _) => { _hover = false; Render(); };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        StartWatchers();
        _pollTimer.Start();
        _placementTimer.Start();
        RefreshDevices();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (_winEventHook != IntPtr.Zero) Native.UnhookWinEvent(_winEventHook);
        foreach (var w in _watchers)
        {
            try { w.Stop(); } catch { }
        }
        base.OnFormClosed(e);
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_DEVICECHANGE:
                ScheduleRefresh();
                break;
            case Native.WM_SETTINGCHANGE:
            case Native.WM_DPICHANGED:
                base.WndProc(ref m);
                BeginInvoke(Render);
                return;
        }
        base.WndProc(ref m);
    }

    // ---------------- Данные ----------------

    private void StartWatchers()
    {
        // Подключение/отключение Bluetooth-устройств приходит сюда сразу, без ожидания опроса.
        string[] props = { "System.Devices.Aep.IsConnected" };
        foreach (var selector in new[]
                 {
                     BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                     BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                 })
        {
            try
            {
                var w = DeviceInformation.CreateWatcher(selector, props, DeviceInformationKind.AssociationEndpoint);
                w.Added += (_, _) => { };
                w.Removed += (_, _) => ScheduleRefreshFromAnyThread();
                w.Updated += (_, _) => ScheduleRefreshFromAnyThread();
                w.Start();
                _watchers.Add(w);
            }
            catch { /* не критично — остаётся периодический опрос */ }
        }
    }

    private void ScheduleRefreshFromAnyThread()
    {
        if (IsHandleCreated && !IsDisposed) BeginInvoke(ScheduleRefresh);
    }

    private void ScheduleRefresh()
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private async void RefreshDevices()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            _devices = await BatteryReader.ReadAsync();
        }
        catch
        {
            // оставляем предыдущие значения
        }
        finally
        {
            _refreshing = false;
        }
        Render();
    }

    // ---------------- Отрисовка ----------------

    private void Render()
    {
        if (!IsHandleCreated || IsDisposed) return;
        if (_devices.Count == 0 || !TryGetTaskbar(out var tb, out _))
        {
            HideWidget();
            UpdateTooltip();
            return;
        }

        float scale = Native.GetDpiForWindow(Handle) / 96f;
        if (scale <= 0) scale = 1;
        bool horizontal = tb.Width >= tb.Height;
        int height = horizontal ? tb.Height : (int)(40 * scale);

        bool light = Settings.TaskbarIsLight;
        Color fg = light ? Color.FromArgb(255, 0, 0, 0) : Color.FromArgb(255, 255, 255, 255);
        Color low = light ? Color.FromArgb(255, 196, 43, 28) : Color.FromArgb(255, 255, 153, 164);

        // Целые пиксельные размеры — глифы Segoe Fluent Icons рассчитаны на сетку 16 px.
        int iconPx = (int)Math.Round(16 * scale);
        int textPx = (int)Math.Round(14 * scale);
        int pad = (int)Math.Round(8 * scale);
        int iconGap = (int)Math.Round(6 * scale);
        int itemGap = (int)Math.Round(16 * scale);

        var items = _devices.Select(d => (
            Icon: GlyphRenderer.Render(Glyph(d.Kind), IconFontName, iconPx),
            Text: GlyphRenderer.Render(d.Level + "%", TextFontName, textPx),
            Color: d.Level <= 15 ? low : fg)).ToList();

        static int InkW(GlyphRun r) => r.InkRight - r.InkLeft + 1;
        static int InkH(GlyphRun r) => r.InkBottom - r.InkTop + 1;

        int contentW = items.Sum(i => InkW(i.Icon) + iconGap + InkW(i.Text)) + itemGap * (items.Count - 1);
        int width = contentW + pad * 2;

        // Фон: почти прозрачный (чтобы окно ловило мышь по всей площади) + подсветка при наведении.
        byte[] pixels;
        using (var bg = new Bitmap(width, height, PixelFormat.Format32bppPArgb))
        {
            using (var g = Graphics.FromImage(bg))
            {
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                if (_hover || _dragging)
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    int vm = (int)Math.Round(4 * scale);
                    using var hoverBrush = new SolidBrush(Color.FromArgb(light ? 20 : 26, fg));
                    using var path = RoundedRect(new RectangleF(0, vm, width, height - vm * 2), 5 * scale);
                    g.FillPath(hoverBrush, path);
                }
            }
            var data = bg.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * width * 4, width * 4);
            bg.UnlockBits(data);
        }

        // Глифы: по горизонтали встык по контуру, по вертикали — центр реального контура в центре панели.
        int x = pad;
        foreach (var it in items)
        {
            int iconTop = (height - InkH(it.Icon)) / 2 - it.Icon.InkTop;
            GlyphRenderer.Composite(pixels, width, height, it.Icon, x - it.Icon.InkLeft, iconTop, it.Color);
            x += InkW(it.Icon) + iconGap;

            int textTop = (height - InkH(it.Text)) / 2 - it.Text.InkTop;
            GlyphRenderer.Composite(pixels, width, height, it.Text, x - it.Text.InkLeft, textTop, it.Color);
            x += InkW(it.Text) + itemGap;
        }

        _size = new Size(width, height);
        _location = ComputeLocation(width, height);
        ApplyPixels(pixels, width, height, _location);
        UpdateTooltip();
        UpdatePlacement();
    }

    private static string Glyph(DeviceKind kind) => kind switch
    {
        DeviceKind.Headphones => "",
        DeviceKind.Mouse => "",
        DeviceKind.Keyboard => "",
        DeviceKind.Gamepad => "",
        DeviceKind.Phone => "",
        _ => "", // Bluetooth
    };

    private void ApplyPixels(byte[] pixels, int width, int height, Point location)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = GlyphRenderer.CreateDib(pixels, width, height);
        IntPtr old = Native.SelectObject(memDc, hBitmap);
        try
        {
            var size = new Native.SIZE(width, height);
            var src = new Native.POINT(0, 0);
            var dst = new Native.POINT(location.X, location.Y);
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };
            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            Native.SelectObject(memDc, old);
            Native.DeleteObject(hBitmap);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
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

    private void UpdateTooltip()
    {
        string tip = _devices.Count == 0
            ? "Нет подключённых устройств с данными о заряде"
            : string.Join(Environment.NewLine, _devices.Select(d => $"{d.Name} — {d.Level}%"));
        _toolTip.SetToolTip(this, tip);
    }

    // ---------------- Положение на панели задач ----------------

    private static bool TryGetTaskbar(out Native.RECT taskbar, out Native.RECT? tray)
    {
        tray = null;
        taskbar = default;
        IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero || !Native.GetWindowRect(tb, out taskbar)) return false;

        IntPtr notify = Native.FindWindowEx(tb, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var r) && r.Width > 0 && r.Height > 0)
            tray = r;
        return true;
    }

    private Point ComputeLocation(int width, int height)
    {
        if (!TryGetTaskbar(out var tb, out var tray)) return _location;
        float scale = Native.GetDpiForWindow(Handle) / 96f;

        if (tb.Width < tb.Height)
        {
            // Вертикальная панель (Windows 10): над областью уведомлений.
            int bottom = tray?.Top ?? tb.Bottom - (int)(200 * scale);
            return new Point(tb.Left + (tb.Width - width) / 2, bottom - height - (int)(4 * scale));
        }

        int right;
        if (_settings.RightOffset is int off)
            right = tb.Right - off;
        else if (tray is { } t && t.Left > tb.Left && t.Left <= tb.Right)
            right = t.Left - (int)(4 * scale);
        else
            right = tb.Right - (int)(300 * scale);

        int x = Math.Clamp(right - width, tb.Left, Math.Max(tb.Left, tb.Right - width));
        int y = tb.Top + (tb.Height - height) / 2;
        return new Point(x, y);
    }

    private void UpdatePlacement()
    {
        if (!IsHandleCreated || IsDisposed || _dragging) return;
        if (_devices.Count == 0 || _size.IsEmpty || !ShouldBeVisible())
        {
            HideWidget();
            return;
        }

        var loc = ComputeLocation(_size.Width, _size.Height);
        if (loc != _location || Location != loc)
        {
            _location = loc;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, loc.X, loc.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        ShowWidget();
    }

    private static bool ShouldBeVisible()
    {
        if (!TryGetTaskbar(out var tb, out _)) return false;

        // Полноэкранное приложение / презентация / заблокированный экран — прячемся вместе с панелью задач.
        if (Native.SHQueryUserNotificationState(out int state) == 0 && state is 1 or 2 or 3 or 4)
            return false;

        // Автоскрытие панели задач: видно меньше половины — прячемся.
        IntPtr tbHwnd = Native.FindWindow("Shell_TrayWnd", null);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (Native.GetMonitorInfo(Native.MonitorFromWindow(tbHwnd, 2), ref mi))
        {
            var m = mi.rcMonitor;
            int visW = Math.Min(tb.Right, m.Right) - Math.Max(tb.Left, m.Left);
            int visH = Math.Min(tb.Bottom, m.Bottom) - Math.Max(tb.Top, m.Top);
            if (visW < tb.Width / 2 || visH < tb.Height / 2) return false;

            // Активное окно на весь монитор (видео в браузере, игра в окне без рамки) — панель задач скрыта.
            if (IsForegroundFullscreen(m)) return false;
        }
        return true;
    }

    private static bool IsForegroundFullscreen(Native.RECT monitor)
    {
        IntPtr fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        var cls = new System.Text.StringBuilder(64);
        Native.GetClassName(fg, cls, cls.Capacity);
        string c = cls.ToString();
        if (c is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        if (!Native.GetWindowRect(fg, out var r)) return false;
        return r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
    }

    private void BringToTop()
    {
        if (_shown && !_dragging && !_menu.Visible)
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void ShowWidget()
    {
        if (_shown) return;
        _shown = true;
        Show();
        BringToTop();
    }

    private void HideWidget()
    {
        if (!_shown) return;
        _shown = false;
        Hide();
    }

    // ---------------- Мышь: перетаскивание и меню ----------------

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _dragging = true;
        _dragMoved = false;
        _dragStartCursorX = Cursor.Position.X;
        _dragStartLeft = _location.X;
        Capture = true;
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        int dx = Cursor.Position.X - _dragStartCursorX;
        if (!_dragMoved && Math.Abs(dx) < 4) return;
        _dragMoved = true;
        if (!TryGetTaskbar(out var tb, out _)) return;

        int x = Math.Clamp(_dragStartLeft + dx, tb.Left, tb.Right - _size.Width);
        _location = new Point(x, _location.Y);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, _location.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            ShowMenu();
            return;
        }
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        if (_dragMoved && TryGetTaskbar(out var tb, out _))
        {
            _settings.RightOffset = tb.Right - (_location.X + _size.Width);
            _settings.Save();
        }
        Render();
    }

    private void BuildMenu()
    {
        _menu.ShowImageMargin = false;
        _menu.ShowCheckMargin = true;
        _menu.Opening += (_, _) =>
        {
            _menu.Items.Clear();
            if (_devices.Count == 0)
                _menu.Items.Add(new ToolStripMenuItem("Нет устройств с данными о заряде") { Enabled = false });
            foreach (var d in _devices)
                _menu.Items.Add(new ToolStripMenuItem($"{d.Name} — {d.Level}%") { Enabled = false });
            _menu.Items.Add(new ToolStripSeparator());

            _menu.Items.Add("Обновить", null, (_, _) => RefreshDevices());

            var autostart = new ToolStripMenuItem("Запускать вместе с Windows") { Checked = SafeAutostart() };
            autostart.Click += (_, _) =>
            {
                try { Settings.AutostartEnabled = !autostart.Checked; }
                catch (Exception ex) { MessageBox.Show(ex.Message, "BatteryBar"); }
            };
            _menu.Items.Add(autostart);

            var reset = new ToolStripMenuItem("Вернуть на место по умолчанию") { Enabled = _settings.RightOffset != null };
            reset.Click += (_, _) =>
            {
                _settings.RightOffset = null;
                _settings.Save();
                Render();
            };
            _menu.Items.Add(reset);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Выход", null, (_, _) => Close());
        };
    }

    private static bool SafeAutostart()
    {
        try { return Settings.AutostartEnabled; } catch { return false; }
    }

    private void ShowMenu()
    {
        _menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
    }

    // ---------------- Утилиты ----------------

    private static string FirstInstalled(params string[] names)
    {
        foreach (var n in names)
        {
            try
            {
                using var f = new Font(n, 10f);
                if (string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)) return n;
            }
            catch { }
        }
        return names[^1];
    }
}
