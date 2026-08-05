using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using UnhingedSync.Models;
using UnhingedSync.Services;

namespace UnhingedSync.ViewModels;

public enum StatusKind { Unknown, Ready, Skew, NeedsBinaries, Warning, Error }

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly TimeSpan ClaimMaxAge = TimeSpan.FromMinutes(90);

    private readonly AppConfig _config;
    private readonly DvCli _dv;
    private readonly BuildStore _store;
    private readonly BinaryInstaller _installer;
    private readonly LocalBuilder _builder;
    private EngineInfo _engine;
    private readonly IProgress<string> _log;
    private readonly StringBuilder _logBuffer = new();

    private string _workspaceCommit = "";
    private string _branch = "";
    private string? _installedCommit;
    private bool _isBusy;
    private string _busyText = "";
    private StatusKind _status = StatusKind.Unknown;
    private string _statusHeadline = "Loading…";
    private string _statusDetail = "";
    private CommitRowViewModel? _selectedRow;

    public MainViewModel() : this(ConfigLoader.Load()) { }

    public MainViewModel(string projectRoot) : this(ConfigLoader.Load(projectRoot)) { }

    private MainViewModel(AppConfig config)
    {
        _config = config;
        _engine = EngineLocator.Locate(_config);
        _dv = new DvCli(_config.ProjectRoot);
        _store = new BuildStore(_config);
        _installer = new BinaryInstaller(_config);
        _builder = new LocalBuilder(_config);

        _log = new Progress<string>(line =>
        {
            _logBuffer.AppendLine(line);
            OnPropertyChanged(nameof(LogText));
        });

        SyncAndEnsureCommand = new RelayCommand(SyncAndEnsureAsync, () => !IsBusy);
        RefreshCommand = new RelayCommand(RefreshAsync, () => !IsBusy);
        FetchSelectedCommand = new RelayCommand(FetchSelectedAsync,
            () => !IsBusy && SelectedRow?.Record?.IsFetchable == true);
        OpenEditorCommand = new RelayCommand(OpenEditorAsync, () => !IsBusy);
        BuildLocallyCommand = new RelayCommand(BuildLocallyAsync, () => !IsBusy);
        OpenLogCommand = new RelayCommand(OpenLogAsync, () => !IsBusy && SelectedRow?.Record?.LogName is not null);

        BuildEngineOptions();
    }

    // ---------------------------------------------------------------- bindable state

    public ObservableCollection<CommitRowViewModel> Rows { get; } = [];

    /// <summary>Exposed so the sharing window can reach the resolved paths.</summary>
    public AppConfig Config => _config;

    public string ProjectName => _config.ProjectName;
    public string EngineText => $"UE {_engine.Version}  ·  BuildId {_engine.BuildId}";
    public string AppVersionText => $"v{EmbeddedScripts.Version}";

    /// <summary>One entry per engine on this machine, plus automatic resolution.</summary>
    public sealed record EngineOption(string Dir, string Label)
    {
        public override string ToString() => Label;
    }

    public ObservableCollection<EngineOption> EngineOptions { get; } = [];

    private EngineOption? _selectedEngine;
    public EngineOption? SelectedEngine
    {
        get => _selectedEngine;
        set
        {
            if (value is null || ReferenceEquals(_selectedEngine, value)) return;
            var previous = _selectedEngine;
            if (!Set(ref _selectedEngine, value)) return;

            // Only persist a deliberate change, not the initial population.
            if (previous is null) return;

            try
            {
                ConfigLoader.SetEngineOverride(_config.ProjectRoot, value.Dir);
                _config.EngineDirOverride = value.Dir;
                _engine = EngineLocator.Locate(_config);

                OnPropertyChanged(nameof(EngineText));
                _log.Report($"Engine set to UE {_engine.Version} (BuildId {_engine.BuildId}) at {_engine.InstallDir}");
                UpdateStatus();
            }
            catch (Exception e)
            {
                _log.Report($"Could not switch engine: {e.Message}");
            }
        }
    }

    private void BuildEngineOptions()
    {
        EngineOptions.Clear();
        EngineOptions.Add(new EngineOption("", "Automatic (from .uproject)"));

        foreach (var engine in EngineLocator.EnumerateInstalled())
        {
            var kind = Path.GetFileName(engine.InstallDir.TrimEnd(Path.DirectorySeparatorChar));
            EngineOptions.Add(new EngineOption(engine.InstallDir, $"UE {engine.Version}  —  {kind}"));
        }

        // Assign the backing field directly: going through the setter here would persist
        // an override nobody asked for.
        _selectedEngine = EngineOptions.FirstOrDefault(o =>
                              o.Dir.Equals(_config.EngineDirOverride, StringComparison.OrdinalIgnoreCase))
                          ?? EngineOptions[0];
        OnPropertyChanged(nameof(SelectedEngine));
    }
    public string PublishRootText => _store.IsReachable
        ? _config.PublishRoot
        : $"{_config.PublishRoot}  (not reachable)";

    public string Branch { get => _branch; private set => Set(ref _branch, value); }
    public string WorkspaceCommit { get => _workspaceCommit; private set => Set(ref _workspaceCommit, value); }
    public string InstalledCommitText => _installedCommit ?? "none";

    public StatusKind Status { get => _status; private set => Set(ref _status, value); }
    public string StatusHeadline { get => _statusHeadline; private set => Set(ref _statusHeadline, value); }
    public string StatusDetail { get => _statusDetail; private set => Set(ref _statusDetail, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            RaiseAllCanExecute();
        }
    }

    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    public string LogText => _logBuffer.ToString();

    public CommitRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set { if (Set(ref _selectedRow, value)) RaiseAllCanExecute(); }
    }

    public RelayCommand SyncAndEnsureCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand FetchSelectedCommand { get; }
    public RelayCommand OpenEditorCommand { get; }
    public RelayCommand BuildLocallyCommand { get; }
    public RelayCommand OpenLogCommand { get; }

    // ---------------------------------------------------------------- operations

    public async Task RefreshAsync()
    {
        await RunAsync("Refreshing…", async () =>
        {
            WorkspaceCommit = await _dv.GetWorkspaceCommitAsync();
            Branch = await _dv.GetBranchAsync();
            var commits = await _dv.GetLogAsync(60);
            var records = _store.ReadAll().ToDictionary(r => r.CommitId);
            _installedCommit = _installer.ReadInstalled()?.CommitId;

            Rows.Clear();
            foreach (var commit in commits)
            {
                records.TryGetValue(commit.CommitId, out var record);
                Rows.Add(new CommitRowViewModel
                {
                    Commit = commit,
                    Record = record,
                    IsWorkspace = commit.CommitId == WorkspaceCommit,
                    IsInstalled = commit.CommitId == _installedCommit,
                    ClaimedBy = _store.ActiveClaimBy(commit.Ordinal.ToString(), ClaimMaxAge)
                });
            }

            OnPropertyChanged(nameof(InstalledCommitText));
            UpdateStatus();
        });
    }

    /// <summary>
    /// The one button most of the team needs: sync to latest, then make sure usable
    /// binaries for that commit are installed -- building and publishing them here if
    /// nobody has yet.
    /// </summary>
    private async Task SyncAndEnsureAsync()
    {
        await RunAsync("Syncing…", async () =>
        {
            _log.Report("── Sync & ensure binaries ──");
            _log.Report("Pulling latest changes from Diversion…");
            await _dv.UpdateAsync(_log);

            WorkspaceCommit = await _dv.GetWorkspaceCommitAsync();
            Branch = await _dv.GetBranchAsync();
            _log.Report($"Workspace is now on {WorkspaceCommit} ({Branch}).");

            var record = FindUsableRecord(WorkspaceCommit);
            if (record is null)
            {
                BusyText = "No binaries yet — building…";
                record = await BuildPublishAndReloadAsync();
                if (record is null) return;
            }
            else
            {
                _log.Report($"Found published binaries for {WorkspaceCommit} (built by {record.BuiltBy}).");
            }

            BusyText = "Installing binaries…";
            var zip = _store.ZipPathFor(record)!;
            await _installer.InstallAsync(record, _engine, zip, _log);

            await ReloadRowsAsync();
            _log.Report("Ready — you can open the editor.");
        });
    }

    /// <summary>Returns the freshly published record, or null if we could not build.</summary>
    private async Task<BuildRecord?> BuildPublishAndReloadAsync()
    {
        var commitShort = WorkspaceCommit.Replace("dv.commit.", "");
        var otherMachine = _store.ActiveClaimBy(commitShort, ClaimMaxAge);
        if (otherMachine is not null)
        {
            _log.Report($"{otherMachine} is already building this commit. Waiting for them is usually faster " +
                        "than building it again — press Refresh in a few minutes.");
            SetStatus(StatusKind.Warning,
                $"{otherMachine} is building {WorkspaceCommit}",
                "Press Refresh once they finish, then fetch the binaries.");
            return null;
        }

        var capability = _builder.CanBuild(_engine);
        if (!capability.CanBuild)
        {
            _log.Report($"Cannot build here: {capability.Reason}");
            SetStatus(StatusKind.NeedsBinaries,
                $"Nobody has built {WorkspaceCommit} yet",
                capability.Reason);
            return null;
        }

        _log.Report("No binaries published for this commit — building locally. This takes a while.");
        var result = await _builder.BuildAndPublishAsync(_log);

        if (!result.Succeeded)
        {
            _log.Report("Build failed. Nothing was published.");
            SetStatus(StatusKind.Error,
                "Local build failed",
                "Nothing was published. See the log below for the compiler output.");
            return null;
        }

        _log.Report($"Build succeeded and published {result.ZipName}.");
        var record = FindUsableRecord(WorkspaceCommit);
        if (record is null)
            _log.Report("Published, but the record is not readable yet. Press Refresh in a moment.");
        return record;
    }

    private async Task FetchSelectedAsync()
    {
        var row = SelectedRow;
        if (row?.Record is null) return;

        await RunAsync("Installing binaries…", async () =>
        {
            if (row.Commit.CommitId != WorkspaceCommit)
            {
                _log.Report($"WARNING: installing binaries for {row.Commit.CommitId} while your workspace " +
                            $"is on {WorkspaceCommit}. Content and code will not match.");
            }

            var zip = _store.ZipPathFor(row.Record)
                ?? throw new InvalidOperationException("That build's payload is no longer available.");
            await _installer.InstallAsync(row.Record, _engine, zip, _log);
            await ReloadRowsAsync();
        });
    }

    /// <summary>
    /// Compiles the current commit here rather than downloading it.
    ///
    /// This is also how a programmer gets debuggable symbols: every build emits PDBs
    /// alongside the DLLs, and those PDBs match because the same linker produced both.
    /// PDBs are never published -- a downloaded build can never come with usable
    /// symbols, because a PDB is bound by GUID to the exact DLL it was linked with.
    /// </summary>
    private async Task BuildLocallyAsync()
    {
        var capability = _builder.CanBuild(_engine);
        if (!capability.CanBuild)
        {
            SetStatus(StatusKind.Warning, "This machine cannot build", capability.Reason);
            _log.Report($"Cannot build here: {capability.Reason}");
            return;
        }

        if (!Confirm(
                $"Compile {WorkspaceCommit} on this machine?\n\n" +
                "This takes several minutes and replaces any downloaded binaries with your " +
                "own build. The binaries are published for the team; the PDBs stay here.\n\n" +
                "This is how you get symbols you can debug with — downloaded builds never " +
                "come with usable PDBs, because a PDB only works with the exact DLL it was " +
                "linked against."))
            return;

        await RunAsync("Building locally…", async () =>
        {
            _log.Report($"── Local build of {WorkspaceCommit} ──");
            var result = await _builder.BuildAndPublishAsync(_log);

            if (!result.Succeeded)
            {
                SetStatus(StatusKind.Error, "Local build failed",
                    "Your existing binaries are untouched. See the log below.");
                return;
            }

            // Install from what we just published so the marker describes reality
            // rather than whatever was downloaded before.
            var mine = _store.ReadAll().FirstOrDefault(
                r => r.CommitId == WorkspaceCommit &&
                     r.BuiltBy.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase));

            if (mine is null)
            {
                _log.Report("Built and published, but the record is not readable yet. Press Refresh.");
                return;
            }

            await _installer.InstallAsync(mine, _engine, _store.ZipPathFor(mine)!, _log);
            await ReloadRowsAsync();
            _log.Report("Done. PDBs for this build are on disk locally and were not published.");
        });
    }

    private static bool Confirm(string message) =>
        MessageBox.Show(message, "Unhinged Sync — symbols",
            MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    private Task OpenEditorAsync()
    {
        if (Status == StatusKind.Skew)
        {
            SetStatus(StatusKind.Skew, StatusHeadline,
                "Fetch matching binaries before opening the editor — mismatched code can corrupt assets on save.");
            return Task.CompletedTask;
        }

        var uproject = Path.Combine(_config.ProjectRoot, _config.ProjectFile);
        Process.Start(new ProcessStartInfo(uproject) { UseShellExecute = true });
        _log.Report($"Launching {_config.ProjectFile}…");
        return Task.CompletedTask;
    }

    private Task OpenLogAsync()
    {
        if (SelectedRow?.Record is null) return Task.CompletedTask;
        var path = _store.LogPathFor(SelectedRow.Record);
        if (path is not null && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            _log.Report("That build log is no longer available.");
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- helpers

    private BuildRecord? FindUsableRecord(string commitId)
    {
        var record = _store.ReadAll().FirstOrDefault(r => r.CommitId == commitId && r.IsFetchable);
        if (record is null) return null;

        if (_config.Engine.EnforceBuildIdMatch &&
            !string.IsNullOrEmpty(record.EngineBuildId) &&
            record.EngineBuildId != _engine.BuildId)
        {
            _log.Report($"A build exists for {commitId} but it targets engine BuildId " +
                        $"{record.EngineBuildId}, and this machine has {_engine.BuildId}.");
            return null;
        }
        return record;
    }

    private async Task ReloadRowsAsync()
    {
        var commits = await _dv.GetLogAsync(60);
        var records = _store.ReadAll().ToDictionary(r => r.CommitId);
        _installedCommit = _installer.ReadInstalled()?.CommitId;

        Rows.Clear();
        foreach (var commit in commits)
        {
            records.TryGetValue(commit.CommitId, out var record);
            Rows.Add(new CommitRowViewModel
            {
                Commit = commit,
                Record = record,
                IsWorkspace = commit.CommitId == WorkspaceCommit,
                IsInstalled = commit.CommitId == _installedCommit,
                ClaimedBy = _store.ActiveClaimBy(commit.Ordinal.ToString(), ClaimMaxAge)
            });
        }

        OnPropertyChanged(nameof(InstalledCommitText));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (!_store.IsReachable)
        {
            SetStatus(StatusKind.Warning, "Binary share not reachable",
                $"Cannot see {_config.PublishRoot}. Check that Syncthing is running.");
            return;
        }

        // The .uproject states which engine version the project targets. Building with a
        // different one is not a warning-and-carry-on situation: assets are versioned to
        // the engine that wrote them.
        var association = EngineLocator.ReadEngineAssociation(_config.ProjectRoot, _config.ProjectFile);
        if (!string.IsNullOrEmpty(association) &&
            !_engine.Version.StartsWith(association, StringComparison.OrdinalIgnoreCase))
        {
            SetStatus(StatusKind.Warning,
                $"Selected engine (UE {_engine.Version}) is not what this project targets (UE {association})",
                "Pick the matching engine above, or set Automatic. Building against a different " +
                "engine version will fail, and opening the project with it can upgrade assets " +
                "irreversibly.");
            return;
        }

        if (_config.Engine.EnforceBuildIdMatch &&
            !string.IsNullOrEmpty(_config.Engine.ExpectedBuildId) &&
            _config.Engine.ExpectedBuildId != _engine.BuildId)
        {
            SetStatus(StatusKind.Warning, "Engine build differs from the team's",
                $"This machine has BuildId {_engine.BuildId}; the project expects " +
                $"{_config.Engine.ExpectedBuildId}. Binaries will not be interchangeable.");
            return;
        }

        if (_installedCommit is null)
        {
            SetStatus(StatusKind.NeedsBinaries, "No binaries installed",
                "Press Sync & Ensure Binaries to get set up.");
            return;
        }

        if (_installedCommit != WorkspaceCommit)
        {
            SetStatus(StatusKind.Skew,
                $"Binaries are from {_installedCommit}, workspace is on {WorkspaceCommit}",
                "Do not open the editor until these match — mismatched C++ and content can " +
                "silently drop data when assets are resaved.");
            return;
        }

        SetStatus(StatusKind.Ready, $"In sync on {WorkspaceCommit}",
            "Binaries match your workspace. Safe to open the editor.");
    }

    private void SetStatus(StatusKind kind, string headline, string detail)
    {
        Status = kind;
        StatusHeadline = headline;
        StatusDetail = detail;
    }

    private async Task RunAsync(string busyText, Func<Task> work)
    {
        IsBusy = true;
        BusyText = busyText;
        try
        {
            await work();
        }
        catch (Exception e)
        {
            _log.Report($"ERROR: {e.Message}");
            if (e is DvException { Detail: { Length: > 0 } detail }) _log.Report(detail);
            SetStatus(StatusKind.Error, "Something went wrong", e.Message);
        }
        finally
        {
            IsBusy = false;
            BusyText = "";
        }
    }

    private void RaiseAllCanExecute()
    {
        SyncAndEnsureCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        FetchSelectedCommand.RaiseCanExecuteChanged();
        OpenEditorCommand.RaiseCanExecuteChanged();
        OpenLogCommand.RaiseCanExecuteChanged();
        BuildLocallyCommand.RaiseCanExecuteChanged();
    }

    // ---------------------------------------------------------------- INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

