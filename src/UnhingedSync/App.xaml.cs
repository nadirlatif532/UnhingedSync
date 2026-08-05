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
    /// Asks once where this machine should keep the shared binaries, then remembers it
    /// per machine. There is no sensible default to fall back on -- the tool is portable
    /// and every teammate's drives are laid out differently.
    /// </summary>
    private static bool EnsurePublishRoot()
    {
        var config = ConfigLoader.Load();
        if (!string.IsNullOrWhiteSpace(config.PublishRoot)) return true;

        var prompt = MessageBox.Show(
            "Where should the shared binaries live on this machine?\n\n" +
            "This is the folder Syncthing replicates — builds arrive here and are " +
            "installed into the project from it. Roughly 100 MB for ten builds.\n\n" +
            "Pick a folder now?",
            "Choose a location for the binaries",
            MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (prompt != MessageBoxResult.OK) return false;

        while (true)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Where should the shared binaries be stored?",
                Multiselect = false
            };
            if (dialog.ShowDialog() != true) return false;

            var chosen = Path.GetFullPath(dialog.FolderName);

            // Inside the project it would sit in the Diversion workspace, where a
            // 'dv clean' deletes ignored files -- the share would vanish silently.
            var projectRoot = Path.GetFullPath(config.ProjectRoot)
                .TrimEnd(Path.DirectorySeparatorChar);
            if (chosen.Equals(projectRoot, StringComparison.OrdinalIgnoreCase) ||
                chosen.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var again = MessageBox.Show(
                    $"That folder is inside the project:\n\n{chosen}\n\n" +
                    "Don't put the share there. It would live in the Diversion workspace, " +
                    "and 'dv clean' removes ignored files — it would be deleted without " +
                    "warning, along with every build in it.\n\nPick somewhere else?",
                    "Not inside the project",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (again != MessageBoxResult.OK) return false;
                continue;
            }

            try
            {
                Directory.CreateDirectory(chosen);
                ConfigLoader.PersistPublishRoot(chosen);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                var again = MessageBox.Show(
                    $"Could not use that folder:\n\n{e.Message}\n\nPick another?",
                    "Folder not usable",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (again != MessageBoxResult.OK) return false;
            }
        }
    }
}

