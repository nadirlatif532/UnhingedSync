using System.IO;
using System.Windows;
using UnhingedSync.Services;
using Microsoft.Win32;

namespace UnhingedSync;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        if (e.Args.Any(a => a.Equals("--syncthing", StringComparison.OrdinalIgnoreCase)))
        {
            var path = e.Args.SkipWhile(a => !a.Equals("--syncthing", StringComparison.OrdinalIgnoreCase))
                             .Skip(1)
                             .FirstOrDefault();
            Shutdown(await Diagnose.RunSyncthingAsync(path));
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

        try
        {
            if (!EnsureAtLeastOneProject()) { Shutdown(1); return; }
            if (!EnsurePublishRoot()) { Shutdown(1); return; }
            new MainWindow().Show();
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
            "Unhinged Sync needs to know where your Unreal project is — the folder " +
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
    /// Settles where this machine keeps the shared binaries, without asking.
    ///
    /// This used to be the second question of a first run, which is a poor question to put
    /// to someone who has just unzipped the tool: they have no basis to answer, and the one
    /// genuinely harmful answer -- inside the project, where 'dv clean' deletes ignored
    /// files -- is exactly the one they might pick by accident. A default is chosen and
    /// persisted instead, which also means the Syncthing setup script finds a location in
    /// config.local.json rather than failing for want of one. Override by editing that file
    /// or setting UNHINGEDSYNC_PUBLISH_ROOT.
    /// </summary>
    private static bool EnsurePublishRoot()
    {
        try
        {
            var config = ConfigLoader.Load();
            Directory.CreateDirectory(config.PublishRoot);
            ConfigLoader.PersistPublishRoot(config.PublishRoot);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                $"Could not create the folder for shared binaries:\n\n{e.Message}\n\n" +
                "Set UNHINGEDSYNC_PUBLISH_ROOT to a writable folder and try again.",
                "Cannot reach the binaries folder", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }
}

