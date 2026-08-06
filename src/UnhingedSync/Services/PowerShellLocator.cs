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
    /// Runs winget to install PowerShell 7 and waits for it to finish. Returns whether
    /// pwsh can actually be found afterwards -- winget can exit 0 while PATH in this
    /// process is still stale, so success is judged by looking on disk, not the exit code.
    /// </summary>
    public static async Task<bool> InstallAsync()
    {
        var psi = new ProcessStartInfo("winget",
            "install --id Microsoft.PowerShell --source winget " +
            "--accept-package-agreements --accept-source-agreements -e")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
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
            await proc.WaitForExitAsync();
        }

        return Find() is not null;
    }
}
