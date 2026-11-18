using System.Runtime.InteropServices;

namespace BatteryBar;

/// <summary>
/// Скрытое окно верхнего уровня для системных широковещательных сообщений.
/// Виджет встроен в панель задач как дочернее окно, а такие сообщения получают только окна верхнего уровня:
/// подключение устройств, смена темы/параметров экрана и «TaskbarCreated» (Проводник перезапущен).
/// </summary>
internal sealed class BroadcastListener : NativeWindow, IDisposable
{
    public event Action? DevicesChanged;
    public event Action? SettingsChanged;
    public event Action? TaskbarRecreated;

    private static readonly int WM_TASKBARCREATED = RegisterWindowMessage("TaskbarCreated");
    private const int WM_DISPLAYCHANGE = 0x007E;

    public BroadcastListener()
    {
        CreateHandle(new CreateParams { Caption = "BatteryBarListener" });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_DEVICECHANGE) DevicesChanged?.Invoke();
        else if (m.Msg is Native.WM_SETTINGCHANGE or WM_DISPLAYCHANGE) SettingsChanged?.Invoke();
        else if (m.Msg == WM_TASKBARCREATED) TaskbarRecreated?.Invoke();
        base.WndProc(ref m);
    }

    public void Dispose() => DestroyHandle();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string name);
}
