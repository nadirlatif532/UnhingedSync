using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UnhingedSync.ViewModels;

namespace UnhingedSync;

/// <summary>One project's view. Several of these live side by side as tabs.</summary>
public partial class ProjectView : UserControl
{
    public ProjectView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (ViewModel is { } vm) vm.OperationStarted += (_, _) => LogSection.BringIntoView();
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

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

    /// <summary>
    /// The DataGrid and the log TextBox each carry their own internal ScrollViewer, and a
    /// nested ScrollViewer always claims the mouse wheel for itself -- even when it has
    /// nothing left to scroll -- so the page-level ScrollViewer around this whole view
    /// never saw a wheel event while the cursor was over either control. That read as
    /// "the scroll wheel does not work" for most of the window, since the grid and the log
    /// together cover most of it.
    ///
    /// Forwarded only when the control the cursor is over has no room left to scroll in
    /// that direction, so scrolling within a long log or a long commit list still works;
    /// only once it is exhausted does the wheel fall through to the page.
    /// </summary>
    private void ForwardMouseWheelToParent(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not FrameworkElement element) return;

        if (FindDescendantScrollViewer(element) is { } inner)
        {
            var scrollingUp = e.Delta > 0;
            var atTop = inner.VerticalOffset <= 0;
            var atBottom = inner.VerticalOffset >= inner.ScrollableHeight;
            if ((scrollingUp && !atTop) || (!scrollingUp && !atBottom)) return;
        }

        e.Handled = true;
        element.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
        {
            RoutedEvent = UIElement.MouseWheelEvent,
            Source = element
        });
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;

            var nested = FindDescendantScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }
}
