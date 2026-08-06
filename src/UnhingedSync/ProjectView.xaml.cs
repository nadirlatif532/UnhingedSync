using System.Windows;
using System.Windows.Controls;
using UnhingedSync.ViewModels;

namespace UnhingedSync;

/// <summary>One project's view. Several of these live side by side as tabs.</summary>
public partial class ProjectView : UserControl
{
    public ProjectView() => InitializeComponent();

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void Sharing_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        new PeersWindow(ViewModel.Config) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async void ManageBinaries_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        new ManageBinariesWindow(ViewModel.Config) { Owner = Window.GetWindow(this) }.ShowDialog();

        // Deletions there can change what this project's own list considers expired.
        await ViewModel.RefreshAsync();
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Keep the newest output in view while a long build streams in.
        if (sender is TextBox box) box.ScrollToEnd();
    }
}
