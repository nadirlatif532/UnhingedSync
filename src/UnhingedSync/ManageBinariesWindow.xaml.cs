using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using UnhingedSync.Models;
using UnhingedSync.Services;

namespace UnhingedSync;

/// <summary>One row: a published build that currently has a zip on disk.</summary>
public sealed class BinaryRow(BuildRecord record) : INotifyPropertyChanged
{
    public BuildRecord Record { get; } = record;

    public string CommitLabel => Record.CommitOrdinal > 0 ? $"#{Record.CommitOrdinal}" : Record.CommitId;
    public string BuiltBy => Record.BuiltBy;
    public string SizeText => $"{Record.ZipBytes / 1024.0 / 1024.0:0.#} MB";
    public string BuiltText => Record.BuiltUtc is { } d ? d.ToLocalTime().ToString("MMM d, HH:mm") : "";

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
/// Lets anyone free up space in the shared publish root -- delete specific builds, or
/// one click to keep only the newest few and drop the rest. This is exactly what the
/// build script's own retention already does after a publish; this window just makes
/// it runnable on demand, for when nobody has published in a while and old zips have
/// piled up regardless.
/// </summary>
public partial class ManageBinariesWindow : Window
{
    private readonly BuildStore _store;
    private readonly int _retainBuilds;
    private readonly ObservableCollection<BinaryRow> _rows = [];

    public ManageBinariesWindow(AppConfig config)
    {
        InitializeComponent();
        _store = new BuildStore(config);
        _retainBuilds = Math.Max(1, config.RetainBuilds);
        KeepLastButton.Content = $"Clean Up — Keep Newest {_retainBuilds}";

        Rows.ItemsSource = _rows;
        Load();
    }

    private void Load()
    {
        _rows.Clear();
        foreach (var record in _store.ReadAll()
                     .Where(r => r.Status == "success")
                     .OrderByDescending(r => r.CommitOrdinal))
        {
            var row = new BinaryRow(record);
            row.PropertyChanged += (_, _) => UpdateSummary();
            _rows.Add(row);
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var totalMb = _rows.Sum(r => r.Record.ZipBytes) / 1024.0 / 1024.0;
        var selected = _rows.Where(r => r.IsSelected).ToList();
        var selectedMb = selected.Sum(r => r.Record.ZipBytes) / 1024.0 / 1024.0;

        SummaryText.Text = _rows.Count == 0
            ? "Nothing published, or the share is not reachable."
            : $"{_rows.Count} build(s) on disk, {totalMb:0.#} MB total" +
              (selected.Count > 0 ? $" — {selected.Count} selected, {selectedMb:0.#} MB" : "");

        DeleteButton.IsEnabled = selected.Count > 0;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = true;
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _rows) row.IsSelected = false;
    }

    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows.Where(r => r.IsSelected).ToList();
        if (chosen.Count == 0) return;
        DeleteRows(chosen, $"Delete {chosen.Count} build(s)");
    }

    private void KeepLast_Click(object sender, RoutedEventArgs e)
    {
        // Rows are already newest-first, same ordering the build script's own
        // retention uses -- so "skip the first N" means the same thing here as there.
        var toDelete = _rows.Skip(_retainBuilds).ToList();
        if (toDelete.Count == 0)
        {
            MessageBox.Show(this, $"Already at or under {_retainBuilds} builds — nothing to clean up.",
                "Nothing to do", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DeleteRows(toDelete, $"Keep the newest {_retainBuilds} and delete {toDelete.Count} older build(s)");
    }

    private void DeleteRows(List<BinaryRow> rows, string action)
    {
        var mb = rows.Sum(r => r.Record.ZipBytes) / 1024.0 / 1024.0;
        var confirm = MessageBox.Show(this,
            $"{action}, freeing {mb:0.#} MB.\n\n" +
            "This is the shared Syncthing folder — deleting here removes these builds for " +
            "everyone on the team, not just this machine. Anyone who still needs one of " +
            "these commits would have to build it again.\n\n" +
            "Continue?",
            "Delete shared builds", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        foreach (var row in rows)
        {
            _store.DeletePayload(row.Record);
            _rows.Remove(row);
        }
        UpdateSummary();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
