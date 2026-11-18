using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace BatteryBar;

/// <summary>
/// Виджет на панели задач слева от области уведомлений: «[иконка] 30%  [иконка] 93%».
///
/// Окно встроено в саму панель задач (дочернее окно Shell_TrayWnd). Отдельное окно «поверх всех» не может
/// быть выше панели, пока открыты «Пуск» или поиск: Windows переносит её в системный слой. Дочернее окно
/// поднимается вместе с панелью. Если встроиться нельзя — запасной режим: обычное окно поверх всех.
///
/// Окно — NativeWindow, а не Form: WinForms у формы сам меняет стиль и родителя окна и «вынимал» её из панели.
/// </summary>
internal sealed class TaskbarWidget : IDisposable
{
    private const int PollIntervalMs = 15_000;     // опрос заряда (и обнаружение уснувших/проснувшихся мышей)
    private const int PlacementIntervalMs = 1_000; // слежение за панелью задач

    /// <summary>Пользователь выбрал «Выход» (или приложение перезапускается после обновления).</summary>
    public event EventHandler? Closed;

    private readonly Settings _settings = Settings.Load();
    private readonly System.Windows.Forms.Timer _pollTimer = new() { Interval = PollIntervalMs };
    private readonly System.Windows.Forms.Timer _placementTimer = new() { Interval = PlacementIntervalMs };
    private readonly System.Windows.Forms.Timer _debounceTimer = new() { Interval = 2_000 };
    private readonly TipWindow _tip = new();
    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = 500 };
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 100 };
    private readonly System.Windows.Forms.Timer _raiseTimer = new() { Interval = 300 };
    // Первая проверка обновлений через 20 с после запуска, затем каждые 6 часов.
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 20_000 };
    private readonly ContextMenuStrip _menu = new();
    private readonly List<DeviceWatcher> _watchers = new();
    private readonly TrayIcon _tray;
    private readonly BroadcastListener _listener = new();
    private readonly WidgetWindow _win;
    private readonly SynchronizationContext? _ui;

    private bool _updating;
    private Version? _notifiedVersion;
    private bool _tipVisible;

    private List<BatteryDevice> _devices = new();

    /// <summary>Что показывать на панели: уснувшие устройства скрываются, если включена настройка.</summary>
    private List<BatteryDevice> VisibleDevices => _settings.HideInactive ? _devices.Where(d => !d.Asleep).ToList() : _devices;

    private bool _refreshing;
    private bool _hover;
    private bool _shown;
    private bool _closing;
    private bool _disposed;
    private Size _size = Size.Empty;
    private Point _location;

    private bool _dragging;
    private bool _dragMoved;
    private int _dragStartCursorX, _dragStartLeft;

    private IntPtr _taskbar;   // панель задач, в которую встроено окно (IntPtr.Zero — запасной режим)
    private int _lastDpi;
    private DateTime _lastReattach = DateTime.MinValue;

    private IntPtr _winEventHook;
    private readonly Native.WinEventDelegate _winEventProc; // держим ссылку, чтобы GC не собрал делегат

    private static readonly string IconFontName = FirstInstalled("Segoe Fluent Icons", "Segoe MDL2 Assets");
    private static readonly string TextFontName = FirstInstalled("Segoe UI Variable Text", "Segoe UI");

    private IntPtr Handle => _win.Handle;
    private bool IsHandleCreated => _win.Handle != IntPtr.Zero;

    public TaskbarWidget()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _win = new WidgetWindow(this);

        // Колбэк WinEvent (WINEVENT_OUTOFCONTEXT) приходит в потоке интерфейса — вызываем напрямую.
        _winEventProc = (_, _, _, _, _, _, _) =>
        {
            UpdatePlacement();
            BringToTop();
            // Панель задач поднимает себя уже после смены активного окна — повторяем чуть позже.
            _raiseTimer.Stop();
            _raiseTimer.Start();
        };
        _raiseTimer.Tick += (_, _) => { _raiseTimer.Stop(); BringToTop(); };

        _pollTimer.Tick += (_, _) => RefreshDevices();
        _placementTimer.Tick += (_, _) => { UpdatePlacement(); BringToTop(); };
        _hoverTimer.Tick += (_, _) => CheckHover();
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
            _ = CheckForUpdatesAsync(manual: false);
        };
        _tipTimer.Tick += (_, _) => { _tipTimer.Stop(); ShowTip(); };
        _debounceTimer.Tick += (_, _) => { _debounceTimer.Stop(); RefreshDevices(); };

        BuildMenu();
        _tray = new TrayIcon(_menu, RefreshDevices);

        // Широковещательные сообщения получает скрытое окно верхнего уровня (дочерние окна их не получают).
        _listener.DevicesChanged += ScheduleRefresh;
        _listener.SettingsChanged += Render;
        // Проводник перезапущен: старая панель задач (и наше окно внутри неё) уничтожены — встраиваемся заново.
        _listener.TaskbarRecreated += Reattach;
    }

    public void Start()
    {
        CreateWindow();
        _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        StartWatchers();
        _pollTimer.Start();
        _placementTimer.Start();
        _hoverTimer.Start();
        _updateTimer.Start();
        _tray.Update(_devices, VisibleDevices, DeviceLine); // значок появляется сразу, до первого опроса

        if (_settings.IsFirstRun && !BatteryReader.Demo)
        {
            // Первый запуск: по умолчанию включаем автозапуск. Дальше выбор пользователя в меню не трогаем.
            bool enabled = false;
            try
            {
                if (!Settings.AutostartEnabled) Settings.AutostartEnabled = true;
                enabled = true;
            }
            catch { }
            _settings.Save(); // файл настроек появился — больше это не повторится
            if (enabled)
                _tray.Notify("BatteryBar запущен",
                    "Он будет запускаться вместе с Windows. Отключить можно в меню (правый клик по значку).");
        }
        else if (Program.JustUpdated)
        {
            Updater.Log("запущена после обновления");
            _tray.Notify("BatteryBar обновлён", $"Установлена версия {Updater.Current}.");
        }
        RefreshDevices();
    }

    private void Close()
    {
        if (_closing) return;
        _closing = true;
        Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _closing = true;
        foreach (var t in new[] { _pollTimer, _placementTimer, _debounceTimer, _tipTimer, _hoverTimer, _raiseTimer, _updateTimer })
            t.Dispose();
        _tray.Dispose();
        _tip.Dispose();
        _listener.Dispose();
        if (_winEventHook != IntPtr.Zero) Native.UnhookWinEvent(_winEventHook);
        foreach (var w in _watchers)
        {
            try { w.Stop(); } catch { }
        }
        if (IsHandleCreated) _win.DestroyHandle();
    }

    // ---------------- Окно ----------------

    private void CreateWindow()
    {
        var cp = new CreateParams
        {
            Caption = "BatteryBar",
            X = 0, Y = 0, Width = 1, Height = 1,
            ExStyle = Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
        };
        IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
        if (tb != IntPtr.Zero)
        {
            cp.Parent = tb;
            cp.Style = Native.WS_CHILD | Native.WS_CLIPSIBLINGS;
        }
        else
        {
            cp.Style = Native.WS_POPUP;
            cp.ExStyle |= Native.WS_EX_TOPMOST;
        }

        _shown = false;
        _win.CreateHandle(cp);
        _taskbar = tb != IntPtr.Zero && Native.GetParent(Handle) == tb ? tb : IntPtr.Zero;
    }

    /// <summary>Встраивается в (новую) панель задач заново.</summary>
    private void Reattach()
    {
        if (_closing) return;
        IntPtr tb = Native.FindWindow("Shell_TrayWnd", null);
        if (tb == IntPtr.Zero) return; // панель ещё не создана — попробуем на следующем тике
        if (IsHandleCreated && _taskbar == tb && Native.GetParent(Handle) == tb) return;
        // Не чаще раза в 5 с: если встроиться не выходит, не уходим в цикл пересоздания окна.
        if (DateTime.UtcNow - _lastReattach < TimeSpan.FromSeconds(5)) return;
        _lastReattach = DateTime.UtcNow;
        try
        {
            if (IsHandleCreated) _win.DestroyHandle();
            CreateWindow();
            Render();
        }
        catch { /* повторим на следующем тике */ }
    }

    /// <summary>Окно уничтожено извне (Проводник перезапущен) — встроимся заново при следующей проверке.</summary>
    private void OnWindowLost()
    {
        _shown = false;
        HideTip();
    }

    /// <summary>DPI берём у панели задач: у встроенного окна свой DPI не всегда актуален.</summary>
    private float Scale()
    {
        IntPtr src = _taskbar != IntPtr.Zero && Native.IsWindow(_taskbar) ? _taskbar : Handle;
        uint dpi = src != IntPtr.Zero ? Native.GetDpiForWindow(src) : 96;
        return dpi > 0 ? dpi / 96f : 1f;
    }

    /// <summary>Перемещает окно в точку экрана (для встроенного — в координаты панели задач).</summary>
    private void MoveWindowTo(Point screen)
    {
        if (!IsHandleCreated) return;
        if (_taskbar != IntPtr.Zero)
        {
            var p = new Native.POINT(screen.X, screen.Y);
            Native.ScreenToClient(_taskbar, ref p);
            Native.SetWindowPos(Handle, Native.HWND_TOP, p.X, p.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        else
        {
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, screen.X, screen.Y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
    }

    /// <summary>Сообщения окна виджета. true — обработано.</summary>
    private bool HandleMessage(ref Message m)
    {
        switch (m.Msg)
        {
            case Native.WM_MOUSEACTIVATE:
                m.Result = (IntPtr)Native.MA_NOACTIVATE; // щелчок не активирует ни виджет, ни панель задач
                return true;
            case Native.WM_LBUTTONDOWN:
                OnLeftDown();
                return true;
            case Native.WM_MOUSEMOVE:
                OnMouseMove();
                return true;
            case Native.WM_LBUTTONUP:
                OnLeftUp();
                return true;
            case Native.WM_RBUTTONDOWN:
                HideTip();
                return true;
            case Native.WM_RBUTTONUP:
                ShowMenu();
                return true;
            case Native.WM_CAPTURECHANGED:
                if (_dragging && m.LParam != Handle) FinishDrag();
                return false;
        }
        return false;
    }

    private sealed class WidgetWindow : NativeWindow
    {
        private readonly TaskbarWidget _owner;
        public WidgetWindow(TaskbarWidget owner) => _owner = owner;

        protected override void WndProc(ref Message m)
        {
            if (!_owner.HandleMessage(ref m)) base.WndProc(ref m);
        }

        protected override void OnHandleChange()
        {
            base.OnHandleChange();
            if (Handle == IntPtr.Zero) _owner.OnWindowLost();
        }
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
        if (!_disposed) _ui?.Post(_ => ScheduleRefresh(), null);
    }

    private void ScheduleRefresh()
    {
        if (_disposed) return;
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private async void RefreshDevices()
    {
        if (_refreshing || _disposed) return;
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
        if (_disposed) return;
        var shown = VisibleDevices;
        _tray.Update(_devices, shown, DeviceLine);
        if (!IsHandleCreated) return;
        if (shown.Count == 0 || !TryGetTaskbar(out var tb, out _))
        {
            HideWidget();
            UpdateTooltip();
            return;
        }

        float scale = Scale();
        _lastDpi = (int)(scale * 96);
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

        var items = shown.Select(d => (
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
            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA,
            };
            if (_taskbar != IntPtr.Zero)
            {
                // Встроенное окно: картинку и размер задаём здесь, позицию — в координатах панели задач.
                Native.UpdateLayeredWindowNoMove(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
                MoveWindowTo(location);
            }
            else
            {
                var dst = new Native.POINT(location.X, location.Y);
                Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
            }
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

    // ---------------- Подсказка и наведение ----------------

    private string TooltipText() => _devices.Count == 0
        ? "Нет подключённых устройств с данными о заряде"
        : string.Join(Environment.NewLine, _devices.Select(DeviceLine));

    /// <summary>Обновляет текст, если подсказка уже показана (например, пришли новые данные).</summary>
    private void UpdateTooltip()
    {
        if (_tipVisible) ShowTip();
    }

    private bool CursorOverWidget()
    {
        if (!_shown || !IsHandleCreated || !Native.GetWindowRect(Handle, out var r)) return false;
        var c = Cursor.Position;
        return c.X >= r.Left && c.X < r.Right && c.Y >= r.Top && c.Y < r.Bottom;
    }

    private void ShowTip()
    {
        if (!CursorOverWidget() || _dragging || _menu.Visible) { HideTip(); return; }
        if (!Native.GetWindowRect(Handle, out var r)) return;
        // Над виджетом, чтобы не перекрывать панель задач.
        _tip.ShowAbove(TooltipText(), Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
        _tipVisible = true;
    }

    private void HideTip()
    {
        _tipTimer.Stop();
        _tipVisible = false;
        if (!_tip.IsDisposed && _tip.Visible) _tip.Hide();
    }

    /// <summary>
    /// Наведение определяется опросом положения курсора (каждые 100 мс): сообщения о входе/уходе мыши
    /// до такого окна доходят ненадёжно.
    /// </summary>
    private void CheckHover()
    {
        if (_dragging || _disposed) return;
        bool over = CursorOverWidget();
        if (over)
        {
            if (!_hover) { _hover = true; Render(); }
            if (!_tipVisible && !_tipTimer.Enabled && !_menu.Visible) _tipTimer.Start();
        }
        else
        {
            HideTip();
            if (_hover) { _hover = false; Render(); }
        }
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
        float scale = Scale();

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
        if (_disposed || _closing || _dragging) return;

        // Окно уничтожено (перезапуск Проводника) или панель задач сменилась — встраиваемся заново.
        IntPtr tbNow = Native.FindWindow("Shell_TrayWnd", null);
        if (!IsHandleCreated || (tbNow != IntPtr.Zero && _taskbar != tbNow))
        {
            Reattach();
            if (!IsHandleCreated) return;
        }

        if (VisibleDevices.Count == 0 || _size.IsEmpty || !ShouldBeVisible())
        {
            HideWidget();
            return;
        }

        // Сменился масштаб экрана — перерисовываем под новый DPI.
        if ((int)(Scale() * 96) != _lastDpi) { Render(); return; }

        var loc = ComputeLocation(_size.Width, _size.Height);
        bool moved = !Native.GetWindowRect(Handle, out var cur) || cur.Left != loc.X || cur.Top != loc.Y;
        if (loc != _location || moved)
        {
            _location = loc;
            MoveWindowTo(loc);
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
        if (!_shown || _dragging || _menu.Visible || !IsHandleCreated) return;
        // Встроенное окно — наверх среди дочерних окон панели задач; запасной режим — поверх всех окон.
        Native.SetWindowPos(Handle, _taskbar != IntPtr.Zero ? Native.HWND_TOP : Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void ShowWidget()
    {
        if (_shown || !IsHandleCreated) return;
        _shown = true;
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
        BringToTop();
    }

    private void HideWidget()
    {
        HideTip();
        if (!_shown) return;
        _shown = false;
        if (IsHandleCreated) Native.ShowWindow(Handle, Native.SW_HIDE);
    }

    // ---------------- Мышь: перетаскивание и меню ----------------

    private void OnLeftDown()
    {
        HideTip();
        _dragging = true;
        _dragMoved = false;
        _dragStartCursorX = Cursor.Position.X;
        _dragStartLeft = _location.X;
        Native.SetCapture(Handle);
    }

    private void OnMouseMove()
    {
        if (!_dragging) return; // наведение отслеживает CheckHover
        int dx = Cursor.Position.X - _dragStartCursorX;
        if (!_dragMoved && Math.Abs(dx) < 4) return;
        _dragMoved = true;
        if (!TryGetTaskbar(out var tb, out _)) return;

        int x = Math.Clamp(_dragStartLeft + dx, tb.Left, tb.Right - _size.Width);
        _location = new Point(x, _location.Y);
        MoveWindowTo(_location);
    }

    private void OnLeftUp()
    {
        if (!_dragging) return;
        Native.ReleaseCapture(); // FinishDrag вызовется из WM_CAPTURECHANGED
        if (_dragging) FinishDrag();
    }

    private void FinishDrag()
    {
        _dragging = false;
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
        // После закрытия меню (щелчок по панели задач поднимает её над виджетом) — сразу возвращаем виджет наверх.
        _menu.Closed += (_, _) =>
        {
            RemoveMenuMouseHook();
            _ui?.Post(_ => { BringToTop(); CheckHover(); }, null);
        };
        _menu.Opened += (_, _) =>
        {
            EnsureMenuOwned(); // и при открытии из значка в трее
            InstallMenuMouseHook();
        };

        _menu.Opening += (_, e) =>
        {
            // WinForms заранее отменяет открытие пустого меню, а пункты мы добавляем здесь же —
            // без этого первое нажатие правой кнопки меню не открывало.
            e.Cancel = false;
            HideTip();
            _menu.Items.Clear();
            if (_devices.Count == 0)
                _menu.Items.Add(new ToolStripMenuItem("Нет устройств с данными о заряде") { Enabled = false });
            foreach (var d in _devices)
                _menu.Items.Add(new ToolStripMenuItem(DeviceLine(d)) { Enabled = false });
            _menu.Items.Add(new ToolStripSeparator());

            _menu.Items.Add("Обновить", null, (_, _) => RefreshDevices());

            var hide = new ToolStripMenuItem("Скрывать уснувшие устройства") { Checked = _settings.HideInactive };
            hide.Click += (_, _) =>
            {
                _settings.HideInactive = !_settings.HideInactive;
                _settings.Save();
                Render();
            };
            _menu.Items.Add(hide);

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
            _menu.Items.Add(new ToolStripMenuItem($"BatteryBar {Updater.Current}") { Enabled = false });
            _menu.Items.Add("Проверить обновления", null, (_, _) => _ = CheckForUpdatesAsync(manual: true));

            var auto = new ToolStripMenuItem("Обновлять автоматически") { Checked = _settings.AutoUpdate };
            auto.Click += (_, _) =>
            {
                _settings.AutoUpdate = !_settings.AutoUpdate;
                _settings.Save();
            };
            _menu.Items.Add(auto);

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add("Выход", null, (_, _) => Close());
        };
    }

    private void ShowMenu()
    {
        EnsureMenuOwned();
        _menu.Show(Cursor.Position, ToolStripDropDownDirection.AboveLeft);
        EnsureMenuOwned(); // WinForms при показе может сбросить владельца
        // Виджет никогда не активируется, поэтому без этого меню не узнаёт о щелчке в другом месте
        // и не закрывается. Делаем меню активным окном — тогда любой щелчок вне его закрывает меню.
        Native.SetForegroundWindow(_menu.Handle);
    }

    // Виджет встроен в панель задач и делит с Проводником состояние ввода, поэтому щелчок по панели
    // не считается переходом в другое приложение и меню само не закрывается. Пока меню открыто,
    // следим за нажатиями кнопок мыши: нажатие вне меню закрывает его.
    private IntPtr _mouseHook;
    private Native.LowLevelMouseProc? _mouseHookProc; // держим ссылку, чтобы GC не собрал делегат

    private void InstallMenuMouseHook()
    {
        if (_mouseHook != IntPtr.Zero) return;
        _mouseHookProc ??= MenuMouseHook;
        _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseHookProc, Native.GetModuleHandle(null), 0);
    }

    private void RemoveMenuMouseHook()
    {
        if (_mouseHook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_mouseHook);
        _mouseHook = IntPtr.Zero;
    }

    private IntPtr MenuMouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg is Native.WM_LBUTTONDOWN or Native.WM_RBUTTONDOWN or Native.WM_MBUTTONDOWN or Native.WM_XBUTTONDOWN)
            {
                var info = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
                var pt = new Point(info.pt.X, info.pt.Y);
                if (_menu.Visible && !_menu.Bounds.Contains(pt))
                    _ui?.Post(_ => { if (_menu.Visible) _menu.Close(ToolStripDropDownCloseReason.AppClicked); }, null);
            }
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    /// <summary>
    /// У меню без владельца, ставшего активным окном, Windows показывала кнопку на панели задач, будто
    /// открылось приложение. Окно-инструмент со скрытым владельцем кнопку не получает.
    /// </summary>
    private void EnsureMenuOwned()
    {
        IntPtr h = _menu.Handle; // создаёт окно меню, если его ещё нет
        long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & Native.WS_EX_TOOLWINDOW) == 0)
            Native.SetWindowLongPtr(h, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        if (Native.GetWindow(h, Native.GW_OWNER) != _listener.Handle)
            Native.SetWindowLongPtr(h, Native.GWLP_HWNDPARENT, _listener.Handle);
    }

    // ---------------- Обновления ----------------

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updating) return;
        _updating = true;
        try
        {
            var release = await Updater.CheckAsync();
            if (release == null || !Updater.IsNewer(release))
            {
                if (manual) _tray.Notify("BatteryBar", $"Установлена последняя версия {Updater.Current}.");
                return;
            }

            // Автообновление выключено: только сообщаем (один раз на версию).
            if (!manual && !_settings.AutoUpdate)
            {
                if (_notifiedVersion != release.Version)
                {
                    _notifiedVersion = release.Version;
                    _tray.Notify("Доступно обновление BatteryBar",
                        $"Версия {release.Version}. Нажмите, чтобы открыть страницу релиза.",
                        () => Updater.OpenPage(release.PageUrl));
                }
                return;
            }

            if (!Updater.CanWriteToExeFolder())
            {
                Updater.Log("нет прав на запись в папку приложения");
                _tray.Notify("Доступно обновление BatteryBar",
                    $"Версия {release.Version}. Нет прав на запись в папку программы — нажмите, чтобы скачать вручную.",
                    () => Updater.OpenPage(release.PageUrl));
                return;
            }

            if (manual) _tray.Notify("BatteryBar", $"Загружается версия {release.Version}…");
            var file = await Updater.DownloadAsync(release);
            Updater.ApplyAndRestart(file);
            Close(); // новая версия уже запущена и ждёт завершения этого процесса
        }
        catch (Exception ex)
        {
            Updater.Log("ошибка обновления: " + ex.Message);
            if (manual) _tray.Notify("Не удалось обновить BatteryBar", ex.Message);
        }
        finally
        {
            _updating = false;
        }
    }

    // ---------------- Утилиты ----------------

    private static string DeviceLine(BatteryDevice d) =>
        d.Asleep ? $"{d.Name} — {d.Level}% (спит)" : $"{d.Name} — {d.Level}%";

    private static bool SafeAutostart()
    {
        try { return Settings.AutostartEnabled; } catch { return false; }
    }

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
