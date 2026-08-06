using System.IO;
using System.Windows;
using System.Windows.Controls;
using UnhingedSync.Services;
using UnhingedSync.ViewModels;

namespace UnhingedSync;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await LoadKnownProjectsAsync();
    }

    private async Task LoadKnownProjectsAsync()
    {
        foreach (var projectRoot in ConfigLoader.GetKnownProjects())
        {
            await OpenProjectAsync(projectRoot, activate: ProjectTabs.Items.Count == 0);
        }
        UpdateEmptyHint();
    }

    /// <summary>
    /// Each tab owns its own view model, so two projects never share workspace state,
    /// engine resolution or a build store.
    /// </summary>
    private async Task OpenProjectAsync(string projectRoot, bool activate)
    {
        if (FindTab(projectRoot) is { } existing)
        {
            existing.IsSelected = true;
            return;
        }

        MainViewModel viewModel;
        try
        {
            viewModel = new MainViewModel(projectRoot);
        }
        catch (Exception e)
        {
            MessageBox.Show(
                $"Could not open:\n\n{projectRoot}\n\n{e.Message}",
                "Project could not be opened", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var tab = new TabItem
        {
            Header = viewModel.ProjectName,
            Tag = projectRoot,
            ToolTip = projectRoot,
            Content = new ProjectView { DataContext = viewModel }
        };

        ProjectTabs.Items.Add(tab);
        if (activate) tab.IsSelected = true;
        UpdateEmptyHint();

        await viewModel.RefreshAsync();
    }

    private TabItem? FindTab(string projectRoot) =>
        ProjectTabs.Items.OfType<TabItem>().FirstOrDefault(t =>
            t.Tag is string path &&
            path.Equals(projectRoot, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Bypasses both the four-hour throttle and the "don't ask about this version again"
    /// flag, and reports the up-to-date case. Without this, declining an update once meant
    /// never being offered that version again with no way to change your mind.
    /// </summary>
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await UpdateChecker.CheckAsync(this, manual: true);

    private async void AddProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectPicker.Pick(this) is not { } projectRoot) return;

        ConfigLoader.AddKnownProject(projectRoot);
        await OpenProjectAsync(projectRoot, activate: true);
    }

    private void CloseProject_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectTabs.SelectedItem is not TabItem { Tag: string projectRoot } tab) return;

        var confirm = MessageBox.Show(
            $"Remove this project from Unhinged Sync?\n\n{projectRoot}\n\n" +
            "Nothing on disk is touched, so you can add it back at any time.",
            "Close project", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        ConfigLoader.RemoveKnownProject(projectRoot);
        ProjectTabs.Items.Remove(tab);
        UpdateEmptyHint();
    }

    private void UpdateEmptyHint()
    {
        var empty = ProjectTabs.Items.Count == 0;
        EmptyHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ProjectTabs.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }
}

/// <summary>
/// Shared folder-picking for a project, used both on first run and by "Add project…",
/// so the validation and the explanation of a rejection only exist in one place.
/// </summary>
public static class ProjectPicker
{
    public static string? Pick(Window? owner)
    {
        while (true)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select the folder containing the .uproject",
                Multiselect = false
            };
            if (owner is not null ? dialog.ShowDialog(owner) != true : dialog.ShowDialog() != true)
                return null;

            var chosen = Path.GetFullPath(dialog.FolderName);
            if (ConfigLoader.IsProjectRoot(chosen)) return chosen;

            var retry = MessageBox.Show(
                $"That folder isn't a project Unhinged Sync can open:\n\n{chosen}\n\n" +
                ConfigLoader.DescribeWhyNotProjectRoot(chosen) + "\n\nTry again?",
                "Not a project folder", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (retry != MessageBoxResult.OK) return null;
        }
    }
}
