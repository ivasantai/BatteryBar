namespace BatteryBar;

internal static class Program
{
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

        using var mutex = new Mutex(true, @"Local\BatteryBar.SingleInstance", out bool isNew);
        if (!isNew) return;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TaskbarWidget());
    }
}
