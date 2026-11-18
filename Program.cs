namespace BatteryBar;

internal static class Program
{
    /// <summary>Процесс запущен сразу после автообновления.</summary>
    public static bool JustUpdated { get; private set; }

    [STAThread]
    private static void Main(string[] args)
    {
        // Режим диагностики: BatteryBar.exe --list — выводит найденные устройства в файл рядом с exe.
        if (args.Contains("--list"))
        {
            var text = BatteryReader.Diagnose().GetAwaiter().GetResult();
            var path = Path.Combine(AppContext.BaseDirectory, "devices.txt");
            File.WriteAllText(path, text, new System.Text.UTF8Encoding(true));
            return;
        }

        BatteryReader.Demo = args.Contains("--demo");
        JustUpdated = args.Contains("--updated");

        // После автообновления: дождаться выхода старой версии, затем убрать её файл.
        int waitIdx = Array.IndexOf(args, "--wait-pid");
        if (waitIdx >= 0 && waitIdx + 1 < args.Length && int.TryParse(args[waitIdx + 1], out int oldPid))
        {
            try { System.Diagnostics.Process.GetProcessById(oldPid).WaitForExit(15_000); } catch { }
        }
        Updater.Cleanup();

        using var mutex = new Mutex(true, @"Local\BatteryBar.SingleInstance", out bool isNew);
        if (!isNew) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TaskbarWidget());
    }
}
