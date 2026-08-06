using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
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

    /// <summary>The only executable name this will ever install as itself.</summary>
    private const string ExeName = "UnhingedSync.exe";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(4);

    /// <summary>
    /// The running version, always three components. The tag is normalised the same way
    /// before comparison, so a "v1.2.3.0" tag cannot read as newer than a "1.2.3" build.
    /// </summary>
    private static Version CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, v.Build);
    }

    /// <summary>
    /// Call with manual: true from a menu item to bypass the throttle and the
    /// "don't ask about this version again" flag, and to report the no-update case.
    /// Never throws.
    /// </summary>
    public static async Task CheckAsync(Window? owner = null, bool manual = false)
    {
        try
        {
            if (!manual && !ShouldCheckNow()) return;

            // Stamped before the request, not after: an offline machine that throws here
            // would otherwise re-hit the API on every single launch.
            ConfigLoader.SetLastUpdateCheckUtc(DateTimeOffset.UtcNow);

            // Updating in place inside the replicated share would push two ~60 MB writes
            // to every peer and, on a receive-only machine, leave the folder permanently
            // out of sync -- which also breaks the sync-percentage the team relies on.
            // It is redundant there anyway: Syncthing already distributes the new exe.
            if (RunningInsideShare())
            {
                if (manual)
                    Tell(owner, "This copy is running from inside the shared binaries folder, " +
                                "so it updates by replication rather than by downloading.\n\n" +
                                "Whoever publishes the tool should update it there. To self-update, " +
                                "run a copy kept outside the share.",
                        MessageBoxImage.Information);
                return;
            }

            var (latest, zipUrl) = await FetchLatestAsync();
            var current = CurrentVersion();

            if (latest is null || zipUrl is null)
            {
                if (manual)
                    Tell(owner, $"Could not read the latest release. You are on v{current}.",
                        MessageBoxImage.Warning);
                return;
            }

            if (latest <= current)
            {
                if (manual) Tell(owner, $"You are up to date on v{current}.", MessageBoxImage.Information);
                return;
            }

            if (!manual && ConfigLoader.GetDismissedUpdateVersion() == latest.ToString()) return;

            var choice = Ask(owner,
                $"Unhinged Sync v{latest} is available. You have v{current}.\n\n" +
                "Update now? The download runs in the background and the app restarts when " +
                "it is ready.");

            if (choice != MessageBoxResult.Yes)
            {
                ConfigLoader.SetDismissedUpdateVersion(latest.ToString());
                return;
            }

            await DownloadAndRelaunchAsync(zipUrl, owner);
        }
        catch (Exception e)
        {
            // A failed check must never be louder than a missed one -- unless the user
            // asked for it, in which case silence is the wrong answer.
            if (manual) Tell(owner, $"Update failed: {e.Message}", MessageBoxImage.Error);
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

    /// <summary>
    /// Whether this exe sits in the Syncthing-replicated publish root, or its \App
    /// subfolder. Mirrors how ConfigLoader recognises the same layout.
    /// </summary>
    private static bool RunningInsideShare()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (string.IsNullOrEmpty(dir)) return false;

        var configured = ConfigLoader.GetPersistedPublishRoot();

        foreach (var candidate in new[] { dir, Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)) })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            if (Directory.Exists(Path.Combine(candidate, "records"))) return true;
            if (!string.IsNullOrEmpty(configured) &&
                Path.TrimEndingDirectorySeparator(candidate)
                    .Equals(Path.TrimEndingDirectorySeparator(configured), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool ShouldCheckNow()
    {
        var last = ConfigLoader.GetLastUpdateCheckUtc();
        return last is null || DateTimeOffset.UtcNow - last.Value > CheckInterval;
    }

    private static async Task<(Version? Latest, string? ZipUrl)> FetchLatestAsync()
    {
        // Short timeout: this is a small JSON document and we are blocking a startup path.
        using var http = NewClient(TimeSpan.FromSeconds(20));
        using var response = await http.GetAsync(LatestReleaseUrl);
        if (!response.IsSuccessStatusCode) return (null, null);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagNode)) return (null, null);
        var tag = tagNode.GetString();
        if (string.IsNullOrEmpty(tag)) return (null, null);

        // A prerelease suffix would fail to parse and silently disable updates for the
        // whole team, so strip it rather than give up on the release.
        var numeric = tag.TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(numeric, out var parsed)) return (null, null);
        var latest = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));

        if (!root.TryGetProperty("assets", out var assets)) return (null, null);
        var zipUrl = assets.EnumerateArray()
            .Select(a => a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null)
            .FirstOrDefault(url => url?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true);

        return (latest, zipUrl);
    }

    /// <summary>
    /// Swaps the running exe for the downloaded one and relaunches, without elevation
    /// or a helper process. Windows shares FILE_SHARE_DELETE on a running process's own
    /// image, so renaming it while it executes is allowed -- only overwriting it in place
    /// is not. Rename the old one aside, move the new one in, start it, then exit; the
    /// aside copy is swept up on the next launch once its handle has closed.
    ///
    /// Everything expensive happens on a worker thread. It used to run on the dispatcher,
    /// which froze the window for the whole download and inflate -- and a frozen window
    /// is one people kill, which was precisely how the swap got interrupted.
    /// </summary>
    private static async Task DownloadAndRelaunchAsync(string zipUrl, Window? owner)
    {
        var currentExe = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the running executable's path.");

        var error = await Task.Run(() => DownloadExtractAndSwap(zipUrl, currentExe));

        // The download can outlive the window: someone can close the app during the
        // half hour this is allowed to take. Continuing here would post to a dispatcher
        // that has shut down and crash from a thread-pool thread, where the app's own
        // exception handler cannot see it. The swap has already happened either way, so
        // the new exe is in place and the next launch picks it up.
        if (!AppAlive) return;

        if (error is not null)
        {
            Tell(owner, $"The update could not be installed:\n\n{error}", MessageBoxImage.Error);
            return;
        }

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(currentExe)
        {
            WorkingDirectory = Path.GetDirectoryName(currentExe),
            UseShellExecute = true
        });

        Application.Current.Shutdown();
    }

    /// <summary>
    /// Whether there is still a live dispatcher to talk to. False once the last window has
    /// closed, which can happen while a long download is in flight.
    /// </summary>
    private static bool AppAlive =>
        Application.Current is { } app && !app.Dispatcher.HasShutdownStarted;

    /// <summary>Returns null on success, or a human-readable reason it failed.</summary>
    private static string? DownloadExtractAndSwap(string zipUrl, string currentExe)
    {
        var tempZip = Path.Combine(Path.GetTempPath(), $"unhingedsync-update-{Guid.NewGuid():N}.zip");
        var tempExtract = Path.Combine(Path.GetTempPath(), $"unhingedsync-update-{Guid.NewGuid():N}");
        var oldExe = currentExe + ".old";
        var renamed = false;

        try
        {
            // Generous but still bounded. The payload is ~55 MB, and the 20-second ceiling
            // this used to share with the API call made the update impossible on anything
            // slower than about 23 Mbit/s: it failed silently, so it looked like the update
            // simply did nothing. Half an hour tolerates about 30 KB/s, while still meaning
            // a stalled connection eventually gives up instead of pinning a thread forever.
            using (var http = NewClient(TimeSpan.FromMinutes(30)))
            // ResponseHeadersRead so the body streams to disk. The default buffers the
            // whole response before the call even returns, which for a ~55 MB asset means
            // holding it all in memory on the large object heap first.
            using (var response = http
                       .GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead)
                       .GetAwaiter().GetResult())
            {
                response.EnsureSuccessStatusCode();
                using var source = response.Content.ReadAsStream();
                using var file = File.Create(tempZip);
                source.CopyTo(file);
            }

            ZipFile.ExtractToDirectory(tempZip, tempExtract);

            // Matched by name, not "the first exe found": a release that ever contains a
            // second executable must not be able to install the wrong one over this app.
            var newExe = Directory
                .EnumerateFiles(tempExtract, ExeName, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (newExe is null) return $"The downloaded release did not contain {ExeName}.";

            try { if (File.Exists(oldExe)) File.Delete(oldExe); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            File.Move(currentExe, oldExe);
            renamed = true;

            File.Move(newExe, currentExe);
            renamed = false;
            return null;
        }
        catch (Exception e)
        {
            // Without this the app would be left with no executable at all: renamed aside,
            // nothing moved into place, and the failure only noticed the next morning.
            if (renamed)
            {
                // overwrite: true matters. The move that failed was a cross-volume copy
                // from %TEMP%, not an atomic rename, so it can leave a partial file at the
                // destination -- and without overwrite the rollback would then fail too,
                // leaving a corrupt exe in place and the only good copy named ".old".
                try { File.Move(oldExe, currentExe, overwrite: true); }
                catch (Exception rollback) when (rollback is IOException or UnauthorizedAccessException)
                {
                    return $"{e.Message}\n\nWorse, the original could not be put back. " +
                           $"UnhingedSync.exe is now unusable. Delete it and rename this file " +
                           $"back in its place:\n{oldExe}";
                }
            }
            return e.Message;
        }
        finally
        {
            // ~115 MB per attempt, including every failed one, would otherwise accumulate
            // in %TEMP% forever.
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        // GitHub's API rejects requests with no User-Agent outright.
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("UnhingedSync", CurrentVersion().ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>An owner window is only usable while it is actually loaded.</summary>
    private static Window? UsableOwner(Window? owner) =>
        owner is { IsLoaded: true } ? owner : null;

    private static void Tell(Window? owner, string message, MessageBoxImage icon)
    {
        if (!AppAlive) return;
        if (UsableOwner(owner) is { } w) MessageBox.Show(w, message, "Unhinged Sync", MessageBoxButton.OK, icon);
        else MessageBox.Show(message, "Unhinged Sync", MessageBoxButton.OK, icon);
    }

    private static MessageBoxResult Ask(Window? owner, string message) =>
        UsableOwner(owner) is { } w
            ? MessageBox.Show(w, message, "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information)
            : MessageBox.Show(message, "Update available", MessageBoxButton.YesNo, MessageBoxImage.Information);
}
