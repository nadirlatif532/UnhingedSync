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

    public string StateText => Record.Status switch
    {
        "syncing" => "syncing",
        _ when IsInstalled => "installed",
        _ => "available"
    };

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Record.CommitId };
            if (IsInstalled) lines.Add("These are the binaries you currently have installed");
            if (Record.Status == "syncing") lines.Add("Still replicating to this machine");
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
    private readonly SyncthingClient _syncthing = new();
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
        Load();

        Loaded += async (_, _) => await ApplySharePolicyAsync();
    }

    /// <summary>
    /// Decides whether deleting here reaches the team, and says so plainly either way.
    /// Until this resolves the delete controls stay disabled, so a fast click cannot beat
    /// the check.
    /// </summary>
    /// <summary>
    /// The resolved verdict, kept so a test can assert that CanDelete is never true without
    /// a confirmed writable share.
    /// </summary>
    internal bool PolicyConfirmedWritable { get; private set; }

    internal async Task ApplySharePolicyAsync()
    {
        var info = await ShareRole.ResolveAsync(_syncthing, _config.SyncthingFolderId);
        PolicyConfirmedWritable = info.WritesReachTeam;

        if (!info.SyncthingReachable || info.FolderType.Length == 0)
        {
            CanDelete = false;
            ScopeBanner.Background = Brush("#352C18");
            ScopeBanner.BorderBrush = Brush("#7A6229");
            ScopeHeadline.Text = "Cannot confirm what this machine is allowed to do";
            ScopeDetail.Text = info.SyncthingReachable
                ? $"Syncthing is running but has no folder called '{_config.SyncthingFolderId}', so " +
                  "there is nothing here it is replicating. Run the Syncthing setup from the Sharing " +
                  "window."
                : "Syncthing could not be reached, so there is no way to know whether deleting here " +
                  "would free space for the team or only on this machine. Deleting stays disabled " +
                  "until it can be confirmed, because guessing wrong deletes your only copy and " +
                  "helps nobody. Start Syncthing, or run its setup from the Sharing window.";
        }
        else if (!info.WritesReachTeam)
        {
            CanDelete = false;
            ScopeBanner.Background = Brush("#1C2735");
            ScopeBanner.BorderBrush = Brush("#35597F");
            ScopeHeadline.Text = "This machine receives builds only, so deleting is disabled";
            ScopeDetail.Text =
                $"The shared folder is set to {info.FolderType} " +
                "here, which is how artist machines are set up. Syncthing does not send local " +
                "deletions from a receive-only folder, so removing a build would free space on " +
                "this machine alone, mark the folder permanently out of sync, and offer a Revert " +
                "button that downloads everything straight back. Ask a programmer or the build " +
                "host to clean up instead.";
        }
        else
        {
            CanDelete = true;
            ScopeBanner.Background = Brush("#352C18");
            ScopeBanner.BorderBrush = Brush("#7A6229");
            ScopeHeadline.Text = "Deleting here removes builds for the whole team";
            ScopeDetail.Text =
                "This folder is replicated by Syncthing, and this machine sends changes, so a " +
                "deletion propagates to everyone. Anyone who still needs one of these commits " +
                "would have to compile it again.";
        }

        UpdateSummary();
    }

    private static System.Windows.Media.Brush Brush(string hex) =>
        (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom(hex)!;

    private void Load()
    {
        var installed = _installer.ReadInstalled()?.CommitId;

        _rows.Clear();

        // Anything with a zip on disk, which is also what retention counts. Filtering to
        // status == success alone would hide a still-replicating build from the list while
        // retention kept counting it, and "keep newest 10" would then delete one build too
        // many.
        foreach (var record in _store.ReadAll()
                     .Where(r => !string.IsNullOrEmpty(r.ZipName))
                     .OrderByDescending(r => r.CommitOrdinal))
        {
            var row = new BinaryRow(
                record,
                _store.ActualZipBytes(record),
                record.CommitId == installed,
                _store.ActiveClaimBy(
                    string.IsNullOrEmpty(record.CommitShort)
                        ? record.CommitOrdinal.ToString()
                        : record.CommitShort,
                    ClaimMaxAge));

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
            ? _store.IsReachable
                ? "No builds on disk yet."
                : $"Cannot reach {_store.Root}."
            : $"{_rows.Count} build(s) on disk, {totalMb:0.#} MB total" +
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
        Load();
        await ApplySharePolicyAsync();
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDelete) return;
        var chosen = _rows.Where(r => r.IsSelected).ToList();
        if (chosen.Count == 0) return;
        DeleteRows(chosen, $"Delete {chosen.Count} build(s)");
    }

    private void KeepLast_Click(object sender, RoutedEventArgs e)
    {
        if (!CanDelete) return;

        // Rows are newest-first by commit ordinal, the same ordering retention uses, so
        // "skip the first N" means the same thing in both places.
        // The button is disabled when there is nothing to trim, so this is only a guard.
        var toDelete = _rows.Skip(_retainBuilds).ToList();
        if (toDelete.Count == 0) return;

        DeleteRows(toDelete, $"Keep the newest {_retainBuilds} and delete {toDelete.Count} older build(s)");
    }

    private void DeleteRows(List<BinaryRow> rows, string action)
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
            $"{action}, freeing {mb:0.#} MB.\n\n" +
            "This is the shared Syncthing folder, so deleting here removes these builds for " +
            "everyone on the team. Anyone who still needs one of these commits would have to " +
            "compile it again.\n\n" +
            (warnings.Count > 0 ? string.Join("\n\n", warnings) + "\n\n" : "") +
            "Continue?",
            "Delete shared builds", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var failures = new List<string>();
        foreach (var row in rows)
        {
            // Never throws, so one zip held open by Syncthing mid-transfer no longer takes
            // the app down partway through, leaving the user guessing which ones went.
            if (_store.DeletePayload(row.Record) is { } error)
                failures.Add($"{row.CommitLabel}: {error}");
            else
                _rows.Remove(row);
        }

        UpdateSummary();

        if (failures.Count > 0)
        {
            MessageBox.Show(this,
                $"{rows.Count - failures.Count} of {rows.Count} deleted. These could not be " +
                $"removed, usually because Syncthing is still sending them:\n\n" +
                string.Join("\n", failures) + "\n\nTry again in a moment.",
                "Some builds could not be deleted", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;
}
