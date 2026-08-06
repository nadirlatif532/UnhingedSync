using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using UnhingedSync.Services;
using Microsoft.Win32;

namespace UnhingedSync;

public partial class App
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    // DllImport rather than LibraryImport: the source generator for the latter emits unsafe
    // code, and enabling AllowUnsafeBlocks across the project to obtain one console handle
    // is a poor trade.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);

    /// <summary>
    /// Borrows the calling terminal's console so the headless modes can actually be read.
    ///
    /// This is a WinExe, which means Windows gives it no console, so every Console.Write in
    /// the diagnostic modes went nowhere and the only way to see a result was to open the
    /// JSON file it left in TEMP. Someone running --storagetest saw a silent exit and no
    /// clue whether it had worked.
    ///
    /// Failure is normal and ignored: launched from Explorer there is no parent console to
    /// attach to, and nothing wants one.
    /// </summary>
    private static void AttachToParentConsole()
    {
        if (!AttachConsole(AttachParentProcess)) return;

        // Rebinding is needed because the streams were already bound to nothing. This also
        // does the right thing when output is redirected to a file or a pipe.
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Any switch means a diagnostic run, which is only useful if its output is visible.
        if (e.Args.Any(a => a.StartsWith("--", StringComparison.Ordinal))) AttachToParentConsole();

        // Headless check of the whole service layer, so the plumbing can be verified
        // without a human driving the window.
        if (e.Args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            var outputPath = e.Args.SkipWhile(a => !a.Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                                   .Skip(1)
                                   .FirstOrDefault();
            Shutdown(await SelfTest.RunAsync(outputPath));
            return;
        }

        // Builds every window so the ControlTemplates are applied, not merely parsed.
        if (e.Args.Any(a => a.Equals("--uitest", StringComparison.OrdinalIgnoreCase)))
        {
            var uiPath = e.Args.SkipWhile(a => !a.Equals("--uitest", StringComparison.OrdinalIgnoreCase))
                               .Skip(1)
                               .FirstOrDefault();
            Shutdown(await UiTest.RunAsync(uiPath));
            return;
        }

        // Checks the bucket and token before anyone relies on them. Every way this can be
        // misconfigured otherwise surfaces as the same "no builds found".
        // Compile and upload with no window, so a build box can run from a scheduled task
        // through the same code path the button uses.
        if (e.Args.Any(a => a.Equals("--build", StringComparison.OrdinalIgnoreCase)))
        {
            var buildPath = e.Args.SkipWhile(a => !a.Equals("--build", StringComparison.OrdinalIgnoreCase))
                                  .Skip(1)
                                  .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            Shutdown(await BuildCommand.RunAsync(buildPath));
            return;
        }

        if (e.Args.Any(a => a.Equals("--storagetest", StringComparison.OrdinalIgnoreCase)))
        {
            var storagePath = e.Args.SkipWhile(a => !a.Equals("--storagetest", StringComparison.OrdinalIgnoreCase))
                                    .Skip(1)
                                    .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            var includeWrite = e.Args.Any(a => a.Equals("--write", StringComparison.OrdinalIgnoreCase));
            Shutdown(await StorageTest.RunAsync(storagePath, includeWrite));
            return;
        }

        if (e.Args.Any(a => a.Equals("--fetch", StringComparison.OrdinalIgnoreCase)))
        {
            var commit = e.Args.SkipWhile(a => !a.Equals("--fetch", StringComparison.OrdinalIgnoreCase))
                               .Skip(1)
                               .FirstOrDefault(a => a.StartsWith("dv.commit.", StringComparison.Ordinal));
            Shutdown(await Fetch.RunAsync(commit));
            return;
        }

        // A dispatcher exception would otherwise kill the process outright, with a Windows
        // crash dialog and no explanation. Nothing here is important enough to lose work
        // over, so report it and carry on.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                $"{args.Exception.Message}\n\n{args.Exception.GetType().Name}\n\n" +
                "The app is still running. Use Copy Diagnostics if you need to report this.",
                "Something went wrong", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        try
        {
            UpdateChecker.CleanUpAfterRelaunch();
            if (!EnsureAtLeastOneProject()) { Shutdown(1); return; }
            if (!EnsureStorageConfigured()) { Shutdown(1); return; }

            var window = new MainWindow();
            window.Show();

            // Both of these prompt, so they run only once there is a window to own the
            // dialogs and to be visibly frozen behind them. Before the window existed, a
            // winget install or a 55 MB download left the user staring at nothing at all.
            await EnsurePowerShellAsync(window);
            _ = UpdateChecker.CheckAsync(window);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"{ex.Message}\n\n{ex.GetType().Name}",
                "Unhinged Sync could not start",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// PowerShell 7 (pwsh) runs every embedded script; Windows PowerShell 5.1 cannot --
    /// it lacks the utf8NoBOM encoding these scripts write with, among other gaps, and
    /// fails a few steps into a build or a Syncthing setup rather than up front. Catching
    /// its absence here, once, with an offer to fix it, beats that confusion.
    ///
    /// Declining doesn't block the app: fetching and installing binaries someone else
    /// already built needs no PowerShell at all. It just won't ask again this machine.
    /// </summary>
    private static async Task EnsurePowerShellAsync(Window owner)
    {
        if (PowerShellLocator.IsAvailable) return;
        if (ConfigLoader.GetDeclinedPowerShellInstall()) return;

        var choice = MessageBox.Show(owner,
            "PowerShell 7 was not found on this machine.\n\n" +
            "It's required to build binaries locally and to run Syncthing setup from this " +
            "app. Fetching and installing binaries someone else already built still works " +
            "fine without it.\n\n" +
            "Install PowerShell 7 now? This can take a few minutes and may ask for " +
            "administrator permission.",
            "PowerShell 7 not found", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (choice != MessageBoxResult.Yes)
        {
            ConfigLoader.SetDeclinedPowerShellInstall(true);
            return;
        }

        Mouse.OverrideCursor = Cursors.Wait;
        bool installed;
        try { installed = await PowerShellLocator.InstallAsync(); }
        finally { Mouse.OverrideCursor = null; }

        MessageBox.Show(owner,
            installed
                ? "PowerShell 7 is installed."
                : "Could not install it automatically. Install it from https://aka.ms/powershell, " +
                  "then restart Unhinged Sync.",
            "PowerShell 7", MessageBoxButton.OK,
            installed ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    /// <summary>
    /// Only asked when this machine knows about no projects at all. After that the
    /// window's own "Add project…" handles it, and the list is remembered, so nobody
    /// should ever see this twice.
    /// </summary>
    private static bool EnsureAtLeastOneProject()
    {
        if (ConfigLoader.GetKnownProjects().Count > 0) return true;

        // Running from inside a project tree is a legitimate way to start; take it
        // rather than asking a question we can already answer.
        if (ConfigLoader.TryResolveProjectRoot() is { } discovered)
        {
            ConfigLoader.AddKnownProject(discovered);
            return true;
        }

        var prompt = MessageBox.Show(
            "Unhinged Sync needs to know where your Unreal project is: the folder " +
            "containing the .uproject file.\n\n" +
            "You can add more projects later; each one opens in its own tab.\n\nPick one now?",
            "Add your first project",
            MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (prompt != MessageBoxResult.OK) return false;

        if (ProjectPicker.Pick(null) is not { } projectRoot) return false;

        ConfigLoader.AddKnownProject(projectRoot);
        return true;
    }

    /// <summary>
    /// Confirms the project names somewhere to read builds from.
    ///
    /// This used to create and remember a local folder, because the share was a directory
    /// this machine had to own. There is no such folder now: the bucket is named in the
    /// project's committed config, so the only thing worth checking is that somebody filled
    /// it in. Failing here is not fatal, because the window explains it better than a dialog
    /// on a blank screen can.
    /// </summary>
    private static bool EnsureStorageConfigured()
    {
        try
        {
            var config = ConfigLoader.Load();
            if (config.Storage.IsConfigured) return true;

            MessageBox.Show(
                $"No object store is configured for {config.ProjectName}.\n\n" +
                $"Missing: {config.Storage.DescribeWhatIsMissing()}\n\n" +
                "Fill in the \"storage\" block in Tools/unhingedsync.json. If your team already " +
                "uses this tool, that file comes from version control, so syncing the project " +
                "should bring it. Then check it with:\n\n    UnhingedSync.exe --storagetest",
                "No bucket configured", MessageBoxButton.OK, MessageBoxImage.Warning);

            // Still opens: the commit list and Build Locally are useful, and the status
            // banner repeats this without blocking the whole app on a config file.
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"Could not read this project's configuration:\n\n{e.Message}",
                "Cannot read the configuration", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}

