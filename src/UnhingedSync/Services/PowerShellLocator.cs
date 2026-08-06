using System.Diagnostics;
using System.IO;

namespace UnhingedSync.Services;

/// <summary>
/// Finds PowerShell 7 (pwsh), and only pwsh -- Windows PowerShell 5.1's Set-Content has
/// no utf8NoBOM enumerator, among other gaps, so a silent fallback to powershell.exe
/// doesn't degrade gracefully, it crashes confusingly a few steps into a build or a
/// Syncthing setup. Better to fail here, once, with a fix on offer.
/// </summary>
public static class PowerShellLocator
{
    public static string? Find()
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                 .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var probe = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(probe)) return probe;
            }
            catch (ArgumentException) { }
        }

        // Covers the two installer shapes winget can produce: the per-user App Execution
        // Alias, and the machine-wide MSI location. Either can be missing from PATH in
        // the current process's environment block even right after a successful install.
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Microsoft", "WindowsApps", "pwsh.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         "PowerShell", "7", "pwsh.exe")
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    public static bool IsAvailable => Find() is not null;

    /// <summary>
    /// Runs winget to install PowerShell 7. Returns whether pwsh can actually be found
    /// afterwards -- winget exits non-zero for "already installed", so success is judged
    /// by looking on disk, not by the exit code.
    /// </summary>
    public static async Task<bool> InstallAsync(CancellationToken ct = default)
    {
        // The output streams are deliberately NOT redirected. Nothing here reads them,
        // and a redirected stream nobody drains deadlocks the child the moment it fills
        // the ~4 KB pipe buffer -- which winget's download progress does immediately.
        // Success is judged by Find() below, so the output has no value to capture.
        //
        // --disable-interactivity matters just as much: this runs with CreateNoWindow, so
        // the child has no console. Any prompt winget decided to show would be waiting on
        // input that cannot arrive, and the app would hang with nothing on screen.
        var psi = new ProcessStartInfo("winget",
            "install --id Microsoft.PowerShell --source winget --exact --silent " +
            "--accept-package-agreements --accept-source-agreements --disable-interactivity")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("winget did not start."); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false; // winget itself is not on this machine.
        }

        using (proc)
        {
            // A bounded wait, so a winget that stalls on a network or an elevation prompt
            // we cannot see becomes a clear failure rather than an app that never opens.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));

            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                // Kill is best effort and genuinely may not work: an MSIX install is carried
                // out by AppXSvc, not by a child of winget, so there is nothing in the tree
                // to stop. Fall through to the Find() check below either way, because a slow
                // install that did finish should not be reported as a failure.
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }

        return Find() is not null;
    }
}
