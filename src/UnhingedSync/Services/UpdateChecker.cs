using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;

namespace UnhingedSync.Services;

/// <summary>
/// Checks GitHub Releases for a newer build of the tool itself and, on approval,
/// downloads and swaps it in.
///
/// The repo is public specifically so this needs no token: every teammate's machine
/// can hit the API cold, with no secret to distribute or rotate.
/// </summary>
public static class UpdateChecker
{
    private const string LatestReleaseUrl =
        "https://api.github.com/repos/nadirlatif532/UnhingedSync/releases/latest";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    /// <summary>
    /// Fire-and-forget from OnStartup. Never throws: a network hiccup should not be
    /// louder than a missed update check.
    /// </summary>
    public static async Task CheckAsync()
    {
        try
        {
            if (!ShouldCheckNow()) return;

            var (latest, zipUrl) = await FetchLatestAsync();
            ConfigLoader.SetLastUpdateCheckUtc(DateTimeOffset.UtcNow);
            if (latest is null || zipUrl is null) return;

            var current = new Version(EmbeddedScripts.Version);
            if (latest <= current) return;

            if (ConfigLoader.GetDismissedUpdateVersion() == latest.ToString()) return;

            var choice = MessageBox.Show(
                $"Unhinged Sync v{latest} is available — you have v{current}.\n\n" +
                "Update now? The app will restart.",
                "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);

            if (choice != MessageBoxResult.Yes)
            {
                ConfigLoader.SetDismissedUpdateVersion(latest.ToString());
                return;
            }

            await DownloadAndRelaunchAsync(zipUrl);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Update check failed: {e.Message}");
        }
    }

    /// <summary>Deletes the previous exe's leftover, if its handle has closed by now.</summary>
    public static void CleanUpAfterRelaunch()
    {
        try
        {
            var old = (Environment.ProcessPath ?? "") + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Still held open by the process that was just replaced; it'll go on the
            // next launch instead. Harmless either way.
        }
    }

    private static bool ShouldCheckNow()
    {
        var last = ConfigLoader.GetLastUpdateCheckUtc();
        return last is null || DateTimeOffset.UtcNow - last.Value > CheckInterval;
    }

    private static async Task<(Version? Latest, string? ZipUrl)> FetchLatestAsync()
    {
        using var http = NewClient();
        using var response = await http.GetAsync(LatestReleaseUrl);
        if (!response.IsSuccessStatusCode) return (null, null);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var tag = doc.RootElement.GetProperty("tag_name").GetString();
        if (string.IsNullOrEmpty(tag) || !Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            return (null, null);

        var zipUrl = doc.RootElement.GetProperty("assets").EnumerateArray()
            .Select(a => a.GetProperty("browser_download_url").GetString())
            .FirstOrDefault(url => url?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);

        return (latest, zipUrl);
    }

    /// <summary>
    /// Swaps the running exe for the downloaded one and relaunches, without elevation
    /// or a helper process. Windows shares FILE_SHARE_DELETE on a running process's own
    /// image, so renaming (and even deleting) it while it executes is allowed -- only
    /// overwriting it in place is not. Rename the old one aside, move the new one in,
    /// start it, then exit; the aside copy is swept up on the next launch once its
    /// handle has actually closed.
    /// </summary>
    private static async Task DownloadAndRelaunchAsync(string zipUrl)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the running executable's path.");

        var tempZip = Path.Combine(Path.GetTempPath(), $"unhingedsync-update-{Guid.NewGuid():N}.zip");
        var tempExtract = Path.Combine(Path.GetTempPath(), $"unhingedsync-update-{Guid.NewGuid():N}");

        using (var http = NewClient())
        {
            var bytes = await http.GetByteArrayAsync(zipUrl);
            await File.WriteAllBytesAsync(tempZip, bytes);
        }

        ZipFile.ExtractToDirectory(tempZip, tempExtract);
        var newExe = Directory.EnumerateFiles(tempExtract, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new InvalidOperationException("Downloaded release did not contain an .exe.");

        var oldExe = currentExe + ".old";
        try { if (File.Exists(oldExe)) File.Delete(oldExe); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A previous update's leftover, still held open somehow. The rename below
            // still works -- it just leaves two ".old" generations instead of one.
        }

        File.Move(currentExe, oldExe);
        File.Move(newExe, currentExe);

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(currentExe)
        {
            WorkingDirectory = Path.GetDirectoryName(currentExe),
            UseShellExecute = true
        });

        Application.Current.Shutdown();
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        // GitHub's API rejects requests with no User-Agent outright.
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("UnhingedSync", EmbeddedScripts.Version));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }
}
