using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using UnhingedSync.Models;
using UnhingedSync.Services;

namespace UnhingedSync;

/// <summary>One row: a published build whose zip is currently on disk.</summary>
public sealed class BinaryRow : INotifyPropertyChanged
{
    public BinaryRow(BuildRecord record, long actualBytes, bool isInstalled, string? claimedBy)
    {
        Record = record;
        Bytes = actualBytes > 0 ? actualBytes : record.ZipBytes;
        IsInstalled = isInstalled;
        ClaimedBy = claimedBy;
    }

    public BuildRecord Record { get; }

    /// <summary>
    /// Measured on disk where possible. A record written with a zero size would otherwise
    /// display "0 MB" and promise to free nothing while deleting several megabytes.
    /// </summary>
    public long Bytes { get; }

    public bool IsInstalled { get; }
    public string? ClaimedBy { get; }

    public string CommitLabel => Record.CommitOrdinal > 0 ? $"#{Record.CommitOrdinal}" : Record.CommitId;
    public string BuiltBy => Record.BuiltBy;
    public string SizeText => Bytes > 0 ? $"{Bytes / 1024.0 / 1024.0:0.#} MB" : "unknown";
    public string BuiltText => Record.BuiltUtc is { } d ? d.ToLocalTime().ToString("MMM d, HH:mm") : "";

    public string StateText => IsInstalled ? "installed" : "available";

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Record.CommitId };
            if (IsInstalled) lines.Add("These are the binaries you currently have installed");
            if (ClaimedBy is not null) lines.Add($"{ClaimedBy} is building this commit right now");
            if (!string.IsNullOrEmpty(Record.EngineVersion))
                lines.Add($"Engine {Record.EngineVersion} (BuildId {Record.EngineBuildId})");
            return string.Join('\n', lines);
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Frees space in the shared publish root: delete specific builds, or keep only the
/// newest few. This is the same job the build script's retention pass does after every
/// publish, made runnable on demand for when nobody has published in a while.
///
/// The one thing this window must get right is who it lets press the button. Retention
/// only ever runs on a publishing machine, which is always send-receive. This window can
/// open anywhere, and on a receive-only folder Syncthing keeps local deletions to itself:
/// nothing would be freed for the team, the folder would sit permanently out of sync, and
/// the Revert button Syncthing then offers would pull every deleted zip straight back.
/// So the delete controls stay disabled there, and the banner says why.
/// </summary>
public partial class ManageBinariesWindow : Window, INotifyPropertyChanged
{
    private readonly AppConfig _config;
    private readonly BuildStore _store;
    private readonly BinaryInstaller _installer;
    private readonly int _retainBuilds;
    private readonly ObservableCollection<BinaryRow> _rows = [];

    private static readonly TimeSpan ClaimMaxAge = TimeSpan.FromMinutes(90);

    private bool _canDelete;

    /// <summary>Bound by the checkbox column, so rows go read-only on a receive-only share.</summary>
    public bool CanDelete
    {
        get => _canDelete;
        private set
        {
            if (_canDelete == value) return;
            _canDelete = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanDelete)));
        }
    }

    public ManageBinariesWindow(AppConfig config)
    {
        InitializeComponent();
        DataContext = this;

        _config = config;
        _store = new BuildStore(config);
        _installer = new BinaryInstaller(config);
        _retainBuilds = Math.Max(1, config.RetainBuilds);
        KeepLastButton.Content = $"Clean Up: Keep Newest {_retainBuilds}";

        Rows.ItemsSource = _rows;

        // Both are network calls now, so they belong in Loaded rather than the constructor.
        Loaded += async (_, _) =>
        {
            await LoadAsync();
            await ApplySharePolicyAsync();
        };
    }

    /// <summary>
    /// Kept so a test can assert the delete controls are never live without a store that can
    /// actually be written to.
    /// </summary>
    internal bool PolicyConfirmedWritable { get; private set; }

    /// <summary>
    /// Decides whether deleting is offered at all, and says plainly what it does.
    ///
    /// This used to consult Syncthing's folder type, because on a receive-only folder a
    /// deletion reached nobody and stranded the machine. With a single bucket and one
    /// credential there is no such distinction to make: a delete is a delete, for everyone.
    /// The confirmation is therefore a guard against mistakes, not against people, and the
    /// wording says so rather than implying a permission that does not exist.
    /// </summary>
    internal async Task ApplySharePolicyAsync()
    {
        // Not actually async any more, but kept awaitable: callers already await it and the
        // signature is what the UI test drives.
        await Task.CompletedTask;

        PolicyConfirmedWritable = _store.IsConfigured;

        if (!_store.IsConfigured)
        {
            CanDelete = false;
            ScopeBanner.Background = Brush("#352C18");
            ScopeBanner.BorderBrush = Brush("#7A6229");
            ScopeHeadline.Text = "No bucket is configured for this project";
            ScopeDetail.Text =
                "Fill in the \"storage\" block in Tools/unhingedsync.json, then check it with " +
                "UnhingedSync.exe --storagetest. There is nothing to manage until then.";
        }
        else
        {
            CanDelete = true;
            ScopeBanner.Background = Brush("#352C18");
            ScopeBanner.BorderBrush = Brush("#7A6229");
            ScopeHeadline.Text = "Deleting here removes builds for the whole team";
            ScopeDetail.Text =
                $"These live in {_store.Description}, so a deletion is immediate and affects " +
                "everyone. Anyone who still needs one of these commits would have to compile it " +
                "again.";
        }

        UpdateSummary();
    }

    private static System.Windows.Media.Brush Brush(string hex) =>
        (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom(hex)!;

    private async Task LoadAsync()
    {
        var installed = _installer.ReadInstalled()?.CommitId;

        List<Models.BuildRecord> records;
        try
        {
            records = await _store.ReadAllAsync();
        }
        catch (Exception e)
        {
            _rows.Clear();
            SummaryText.Text = $"Could not read {_store.Description}: {e.Message}";
            return;
        }

        _rows.Clear();

        // Anything still holding a payload. A record whose zip has been deleted reconciles
        // to expired with no ZipName, so this is exactly the set retention counts.
        foreach (var record in records
                     .Where(r => !string.IsNullOrEmpty(r.ZipName))
                     .OrderByDescending(r => r.CommitOrdinal))
        {
            var claim = await _store.ActiveClaimByAsync(
                string.IsNullOrEmpty(record.CommitShort)
                    ? record.CommitOrdinal.ToString()
                    : record.CommitShort,
                ClaimMaxAge);

            var row = new BinaryRow(record, record.ZipBytes, record.CommitId == installed, claim);
            row.PropertyChanged += (_, _) => UpdateSummary();
            _rows.Add(row);
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var totalMb = _rows.Sum(r => r.Bytes) / 1024.0 / 1024.0;
        var selected = _rows.Where(r => r.IsSelected).ToList();
        var selectedMb = selected.Sum(r => r.Bytes) / 1024.0 / 1024.0;

        SummaryText.Text = _rows.Count == 0
            ? _store.IsConfigured
                ? "Nothing published yet."
                : "No bucket is configured for this project."
            : $"{_rows.Count} build(s) published, {totalMb:0.#} MB total" +
              (selected.Count > 0 ? $". {selected.Count} selected, {selectedMb:0.#} MB" : "");

        DeleteButton.IsEnabled = CanDelete && selected.Count > 0;
        KeepLastButton.IsEnabled = CanDelete && _rows.Count > _retainBuilds;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDelete) return;
        foreach (var row in _rows) row.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = false;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadAsync();
        await ApplySharePolicyAsync();
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDelete) return;
        var chosen = _rows.Where(r => r.IsSelected).ToList();
        if (chosen.Count == 0) return;
        await DeleteRowsAsync(chosen, $"Delete {chosen.Count} build(s)");
    }

    private async void KeepLast_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDelete) return;

        // Rows are newest-first by commit ordinal, the same ordering retention uses, so
        // "skip the first N" means the same thing in both places.
        // The button is disabled when there is nothing to trim, so this is only a guard.
        var toDelete = _rows.Skip(_retainBuilds).ToList();
        if (toDelete.Count == 0) return;

        await DeleteRowsAsync(toDelete, $"Keep the newest {_retainBuilds} and delete {toDelete.Count} older build(s)");
    }

    private async Task DeleteRowsAsync(List<BinaryRow> rows, string action)
    {
        var mb = rows.Sum(r => r.Bytes) / 1024.0 / 1024.0;

        // Two cases worth naming before the fact rather than explaining afterwards.
        var warnings = new List<string>();
        if (rows.FirstOrDefault(r => r.IsInstalled) is { } installed)
            warnings.Add($"{installed.CommitLabel} is the build you currently have installed. " +
                         "Your editor keeps working, but you will not be able to reinstall it.");
        if (rows.Where(r => r.ClaimedBy is not null).ToList() is { Count: > 0 } claimed)
            warnings.Add($"Someone is building {string.Join(", ", claimed.Select(c => c.CommitLabel))} " +
                         "right now, and deleting could collide with their publish.");

        var confirm = MessageBox.Show(this,
            $"{action}, freeing {mb:0.#} MB in {_store.Description}.\n\n" +
            "This deletes them from the shared bucket, so they are gone for everyone on the " +
            "team. Anyone who still needs one of these commits would have to compile it again.\n\n" +
            (warnings.Count > 0 ? string.Join("\n\n", warnings) + "\n\n" : "") +
            "Continue?",
            "Delete shared builds", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var failures = new List<string>();
        foreach (var row in rows)
        {
            // Never throws, so one failed delete cannot abandon a multi-build clean up
            // halfway and leave the user guessing which ones went.
            if (await _store.DeletePayloadAsync(row.Record) is { } error)
                failures.Add($"{row.CommitLabel}: {error}");
            else
                _rows.Remove(row);
        }

        UpdateSummary();

        if (failures.Count > 0)
        {
            MessageBox.Show(this,
                $"{rows.Count - failures.Count} of {rows.Count} deleted. These could not be " +
                "removed:\n\n" + string.Join("\n", failures) + "\n\nTry again in a moment.",
                "Some builds could not be deleted", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;
}
