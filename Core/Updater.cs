using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace BatteryBar;

/// <summary>
/// Автообновление из GitHub Releases: проверяет последний релиз, скачивает BatteryBar.exe,
/// проверяет его (размер, SHA-256 из GitHub, версия внутри файла) и подменяет запущенный exe.
/// Запущенный exe в Windows можно переименовать, поэтому замена проходит без внешнего установщика.
/// </summary>
internal static class Updater
{
    private const string Repo = "ivasantai/BatteryBar";
    private const string AssetName = "BatteryBar.exe";

    public sealed record Release(Version Version, string Tag, string DownloadUrl, long Size, string? Sha256, string PageUrl);

    // Порядок важен: статические поля инициализируются по порядку объявления, а CreateClient использует Current.
    public static Version Current { get; } = Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    private static readonly HttpClient Http = CreateClient();

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AssetName);
    private static string NewPath => ExePath + ".new";
    private static string OldPath => ExePath + ".old";

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        // GitHub API требует User-Agent; используем нейтральное имя клиента.
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BatteryBar-Updater", Current.ToString()));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    /// <summary>Последний релиз на GitHub, или null, если его нет / нет нужного файла.</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : $"https://github.com/{Repo}/releases";

        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            string? sha = null;
            if (asset.TryGetProperty("digest", out var dg) && dg.GetString() is string d &&
                d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                sha = d[7..];
            Log($"последний релиз {tag}, размер {asset.GetProperty("size").GetInt64()}, sha256 {(sha ?? "нет")}");
            return new Release(Normalize(version), tag, asset.GetProperty("browser_download_url").GetString()!,
                asset.GetProperty("size").GetInt64(), sha, page);
        }
        Log($"в релизе {tag} нет {AssetName}");
        return null;
    }

    public static bool IsNewer(Release r) => r.Version > Current;

    /// <summary>Скачивает и проверяет новый exe. Возвращает путь к проверенному файлу.</summary>
    public static async Task<string> DownloadAsync(Release r, CancellationToken ct = default)
    {
        string tmp = NewPath + ".part";
        try
        {
            using (var resp = await Http.GetAsync(r.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tmp);
                await src.CopyToAsync(dst, ct);
            }

            long size = new FileInfo(tmp).Length;
            if (size != r.Size) throw new InvalidDataException($"размер {size}, ожидался {r.Size}");

            if (r.Sha256 != null)
            {
                await using var fs = File.OpenRead(tmp);
                string hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
                if (!hash.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 не совпадает");
            }

            // Внутри файла должна быть именно та версия, что в теге релиза.
            var fileVer = FileVersionInfo.GetVersionInfo(tmp);
            if (!Version.TryParse(fileVer.FileVersion, out var v) || Normalize(v) != r.Version)
                throw new InvalidDataException($"версия файла {fileVer.FileVersion}, ожидалась {r.Version}");

            File.Move(tmp, NewPath, overwrite: true);
            Log($"скачано и проверено: {r.Tag}");
            return NewPath;
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>
    /// Подменяет текущий exe новым и запускает его. После успешного вызова текущий процесс должен завершиться.
    /// </summary>
    public static void ApplyAndRestart(string newFile)
    {
        string exe = ExePath;
        TryDelete(OldPath);
        File.Move(exe, OldPath);            // работающий exe переименовать можно
        try
        {
            File.Move(newFile, exe);
        }
        catch
        {
            File.Move(OldPath, exe);        // откат
            throw;
        }

        Log($"exe заменён, перезапуск");
        Process.Start(new ProcessStartInfo(exe, $"--updated --wait-pid {Environment.ProcessId}")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        });
    }

    /// <summary>Удаляет остатки прошлого обновления (вызывается при старте).</summary>
    public static void Cleanup()
    {
        for (int i = 0; i < 10 && File.Exists(OldPath); i++)
        {
            if (TryDelete(OldPath)) break;
            Thread.Sleep(300);
        }
        TryDelete(NewPath + ".part");
    }

    /// <summary>Можно ли писать в папку с exe (иначе — только уведомляем о новой версии).</summary>
    public static bool CanWriteToExeFolder()
    {
        try
        {
            string probe = Path.Combine(Path.GetDirectoryName(ExePath)!, $".bb-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    private static bool TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); return true; }
        catch { return false; }
    }

    public static void Log(string message)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BatteryBar");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "update.log");
            if (File.Exists(file) && new FileInfo(file).Length > 200_000) File.Delete(file);
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Current}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
