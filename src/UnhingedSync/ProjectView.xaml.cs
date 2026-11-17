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

        // DataContextChanged rather than Loaded: Loaded fires again every time the tab is
        // re-shown, which would subscribe a second, third... handler each time.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old)
            {
                old.LogAppended -= OnLogAppended;
                old.OperationStarted -= OnOperationStarted;
            }
            LogBox.Clear();
            _logHasText = false;
            if (e.NewValue is MainViewModel vm)
            {
                AppendLog(vm.LogText);
                vm.LogAppended += OnLogAppended;
                vm.OperationStarted += OnOperationStarted;
            }
        };
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private bool _logHasText;

    private void OnOperationStarted(object? sender, EventArgs e) => LogSection.BringIntoView();

    private void OnLogAppended(object? sender, string chunk) => AppendLog(chunk);

    /// <summary>
    /// Appends lines to the log box and trims the oldest beyond MaxLogLines. Both are
    /// edits at the ends of the text, which cost the same however long the log is;
    /// replacing the whole Text instead costs time proportional to its length and froze
    /// the window during long builds.
    /// </summary>
    internal void AppendLog(string chunk)
    {
        if (chunk.Length == 0) return;

        // Follow new output only if the reader was already at the bottom, so scrolling
        // up to read an error is not yanked away by the next line.
        var follow = LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4;

        // A flag, not LogBox.Text.Length: reading Text copies the whole log into a string.
        LogBox.AppendText(_logHasText ? "\n" + chunk : chunk);
        _logHasText = true;

        // Trim in blocks so the cut happens once per few hundred lines, not every flush.
        var excess = LogBox.LineCount - MainViewModel.MaxLogLines;
        if (excess > 0)
        {
            var cutLine = Math.Min(LogBox.LineCount - 1, excess + MainViewModel.MaxLogLines / 4);
            var cut = LogBox.GetCharacterIndexFromLineIndex(cutLine);
            if (cut > 0)
            {
                LogBox.Select(0, cut);
                LogBox.SelectedText = "";
            }
        }

        if (follow) LogBox.ScrollToEnd();
    }

    private async void ManageBinaries_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        new ManageBinariesWindow(ViewModel.Config) { Owner = Window.GetWindow(this) }.ShowDialog();

        // Deletions there can change what this project's own list considers expired.
        await ViewModel.RefreshAsync();
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
