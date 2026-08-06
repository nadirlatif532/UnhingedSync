using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnosticsAsync, () => true);

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
            EngineOptions.Add(new EngineOption(engine.InstallDir, $"UE {engine.Version}  ({kind})"));
        }

        // Assign the backing field directly: going through the setter here would persist
        // an override nobody asked for.
        _selectedEngine = EngineOptions.FirstOrDefault(o =>
                              o.Dir.Equals(_config.EngineDirOverride, StringComparison.OrdinalIgnoreCase))
                          ?? EngineOptions[0];
        OnPropertyChanged(nameof(SelectedEngine));
    }
    public string PublishRootText => _store.LastKnownReachable
        ? _store.Description
        : $"{_store.Description}  (not reachable)";

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
    public RelayCommand CopyDiagnosticsCommand { get; }

    // ---------------------------------------------------------------- operations

    // Where builds live used to be a per-machine question with five fallbacks and a
    // reconciliation pass, because Syncthing decided it independently and the two could
    // disagree silently. The bucket is named in the committed project config, so there is
    // nothing to resolve, adopt or keep in step.

    public async Task RefreshAsync()
    {
        await RunAsync("Refreshing…", async () =>
        {
            WorkspaceCommit = await _dv.GetWorkspaceCommitAsync();
            Branch = await _dv.GetBranchAsync();

            // Was a near-copy of ReloadRowsAsync, which is how the two drifted: only one of
            // them ever got the single-listing claim lookup.
            await ReloadRowsAsync();
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

            var record = await FindUsableRecordAsync(WorkspaceCommit);
            if (record is null)
            {
                // Deliberately does NOT build. Quietly compiling on a miss made this button
                // succeed whatever the state of the share, which hid the failure that
                // actually matters: replication not working, or nobody having published. A
                // 30 minute compile that ends in a working editor looks like success, so the
                // broken sync goes unnoticed until it bites somebody who cannot compile.
                await ExplainMissingBinariesAsync();
                return;
            }

            _log.Report($"Found published binaries for {WorkspaceCommit} (built by {record.BuiltBy}).");

            BusyText = "Downloading binaries…";
            var zip = await _store.EnsureLocalZipAsync(record, _log);

            BusyText = "Installing binaries…";
            await _installer.InstallAsync(record, _engine, zip, _log);

            await ReloadRowsAsync();
            _log.Report("Ready. You can open the editor.");
        });
    }

    /// <summary>
    /// Says why there are no binaries, and points at Build Locally rather than compiling.
    ///
    /// The distinction that matters: "nobody has built this" and "your share is not syncing"
    /// look identical from here, and only one of them is your problem to solve by compiling.
    /// So this reports the sync percentage, because a share sitting below 100% is the far
    /// more likely explanation on a machine that has just been set up, and compiling would
    /// paper straight over it.
    /// </summary>
    private async Task ExplainMissingBinariesAsync()
    {
        var commitShort = WorkspaceCommit.Replace("dv.commit.", "");
        var claims = await _store.ActiveClaimsAsync(ClaimMaxAge);

        if (claims.TryGetValue(commitShort, out var claim))
        {
            // The age is the whole point. A claim minutes old means wait; one over three
            // quarters of an hour usually means that machine died mid-build, and the user
            // needs to know they are allowed to ignore it rather than sit there.
            var stale = claim.LooksStale
                ? $"\n\nThat started {claim.Describe}, which is long enough that their build may " +
                  "have died. Nothing is stopping you: Build Locally ignores claims entirely, and " +
                  "a claim this old is discarded automatically after 90 minutes."
                : "";

            _log.Report($"{claim.Machine} started building this {claim.Describe}. " +
                        "Waiting is usually faster than building it again.");

            SetStatus(StatusKind.Warning,
                $"{claim.Machine} is building {WorkspaceCommit} (started {claim.Describe})",
                "Press Refresh once they finish. If their build died, press Build Locally, which " +
                "ignores claims.");

            MessageBox.Show(
                $"{claim.Machine} started building {WorkspaceCommit} {claim.Describe}.\n\n" +
                "Waiting for them is usually faster than compiling it again." + stale,
                "Someone is already building this", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Says which of the two situations this is, rather than leaving the user to guess.
        // Against a bucket the distinction is exact: either the store answered and this
        // commit is genuinely unbuilt, or the store could not be reached at all. There is no
        // longer a partially-arrived middle case to reason about, which is what the sync
        // percentage existed to describe.
        string reason;
        List<BuildRecord> published;
        try
        {
            published = await _store.ReadAllAsync();
            reason = published.Count == 0
                ? $"Nothing at all has been published to {_store.Description} yet."
                : $"{_store.Description} holds {published.Count} build(s), but none for this commit.";
        }
        catch (Exception e)
        {
            published = [];
            reason = $"Could not reach {_store.Description}, so it is not known whether this " +
                     $"commit has been built: {e.Message}";
        }

        _log.Report($"No binaries published for {WorkspaceCommit}. {reason}");

        var capability = _builder.CanBuild(_engine);

        SetStatus(StatusKind.NeedsBinaries,
            $"No binaries for {WorkspaceCommit}",
            reason + (capability.CanBuild
                ? " Press Build Locally to compile and publish it for the team."
                : $" This machine cannot build: {capability.Reason}"));

        MessageBox.Show(
            $"No binaries have been published for {WorkspaceCommit}.\n\n" +
            reason + "\n\n" +
            (capability.CanBuild
                ? "This is not done for you automatically, because compiling on every miss hides " +
                  "a store that is unreachable or a commit nobody has built.\n\nPress Build " +
                  "Locally to compile and publish it for the team."
                : $"This machine cannot compile it either:\n{capability.Reason}\n\n" +
                  "Ask someone with a C++ toolchain to build this commit."),
            "No binaries for this commit", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task FetchSelectedAsync()
    {
        var row = SelectedRow;
        if (row?.Record is null) return;

        if (row.Commit.CommitId != WorkspaceCommit)
        {
            var target = row.Commit.Ordinal > 0 ? $"#{row.Commit.Ordinal}" : row.Commit.CommitId;
            if (!Confirm(
                    $"These binaries are for {target}, but your workspace is on {WorkspaceCommit}.\n\n" +
                    $"Run \"dv update\" to {target} in Diversion first if you want matching code and " +
                    "content. Installing anyway leaves the two mismatched, so don't open the editor " +
                    "until you've synced, or a resave can silently drop data.\n\n" +
                    "Install anyway?",
                    "Unhinged Sync: commit mismatch"))
                return;
        }

        await RunAsync("Installing binaries…", async () =>
        {
            if (row.Commit.CommitId != WorkspaceCommit)
            {
                _log.Report($"WARNING: installed binaries for {row.Commit.CommitId} while your workspace " +
                            $"is on {WorkspaceCommit}. Sync in Diversion before opening the editor.");
            }

            var zip = await _store.EnsureLocalZipAsync(row.Record, _log);
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
                "This is how you get symbols you can debug with. Downloaded builds never " +
                "come with usable PDBs, because a PDB only works with the exact DLL it was " +
                "linked against.",
                "Unhinged Sync: symbols"))
            return;

        await RunAsync("Building locally…", async () =>
        {
            _log.Report($"── Local build of {WorkspaceCommit} ──");
            var result = await _builder.BuildAndPublishAsync(
                _store, WorkspaceCommit.Replace("dv.commit.", ""), _log);

            if (!result.Succeeded)
            {
                SetStatus(StatusKind.Error, "Local build failed",
                    "Your existing binaries are untouched. See the log below.");
                return;
            }

            // Install from what we just published so the marker describes reality
            // rather than whatever was downloaded before.
            var mine = (await _store.ReadAllAsync()).FirstOrDefault(
                r => r.CommitId == WorkspaceCommit &&
                     r.BuiltBy.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase));

            if (mine is null)
            {
                _log.Report("Built and published, but the record is not readable yet. Press Refresh.");
                return;
            }

            await _installer.InstallAsync(mine, _engine, await _store.EnsureLocalZipAsync(mine, _log), _log);
            await ReloadRowsAsync();
            _log.Report("Done. PDBs for this build are on disk locally and were not published.");
        });
    }

    private static bool Confirm(string message, string title) =>
        MessageBox.Show(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    /// <summary>
    /// True when the installed binaries are not the ones this workspace's content expects.
    ///
    /// Computed here rather than read off Status, which is a single value produced by a chain
    /// of early returns. Any earlier warning, including the engine mismatch raised during a
    /// team engine upgrade, would set Status to Warning and mean this never read as Skew, so
    /// the guard below silently stopped guarding at exactly the moment it was needed: the
    /// user cannot install matching binaries during an upgrade, but could still open the
    /// editor. Mismatched C++ and content is the one failure here that destroys work.
    /// </summary>
    private bool HasBinarySkew =>
        _installedCommit is null || _installedCommit != WorkspaceCommit;

    private Task OpenEditorAsync()
    {
        if (HasBinarySkew)
        {
            var installed = _installedCommit ?? "none";
            if (!Confirm(
                    $"Your installed binaries are from {installed}, but your workspace is on " +
                    $"{WorkspaceCommit}.\n\n" +
                    "Opening the editor like this can permanently damage assets: if a UPROPERTY " +
                    "changed between those commits, content saved against mismatched code can " +
                    "silently drop data.\n\n" +
                    "Press Sync & Ensure Binaries first. Open anyway?",
                    "Binaries do not match your workspace"))
            {
                _log.Report($"Not opening the editor: binaries are from {installed}, workspace is " +
                            $"on {WorkspaceCommit}.");
                return Task.CompletedTask;
            }

            _log.Report($"WARNING: opening the editor with binaries from {installed} while the " +
                        $"workspace is on {WorkspaceCommit}. Do not save assets.");
        }

        var uproject = Path.Combine(_config.ProjectRoot, _config.ProjectFile);
        Process.Start(new ProcessStartInfo(uproject) { UseShellExecute = true });
        _log.Report($"Launching {_config.ProjectFile}…");
        return Task.CompletedTask;
    }

    private async Task OpenLogAsync()
    {
        if (SelectedRow?.Record is null) return;

        // Downloaded on demand rather than held by everyone. Most people never open a log.
        var path = await _store.EnsureLocalLogAsync(SelectedRow.Record);
        if (path is not null && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else
            _log.Report("That build log is no longer available.");
    }

    /// <summary>
    /// One click, everything worth pasting into a bug report: version, engine, project,
    /// publish-root reachability, whether PowerShell is even findable, current status,
    /// and the recent log. Faster than a screenshot of a stack trace, and doesn't miss
    /// details a screenshot would crop out.
    /// </summary>
    private Task CopyDiagnosticsAsync()
    {
        var text = string.Join(Environment.NewLine, new[]
        {
            $"Unhinged Sync v{EmbeddedScripts.Version}",
            $"Machine: {Environment.MachineName} ({Environment.OSVersion.VersionString})",
            $"Project: {_config.ProjectName} ({_config.ProjectRoot})",
            $"Branch: {Branch}   Workspace: {WorkspaceCommit}   Installed: {InstalledCommitText}",
            $"Engine: {EngineText}   Dir: {_engine.InstallDir}",
            $"Store: {_store.Description} (reachable: {_store.LastKnownReachable})",
            $"Download cache: {_store.CacheDir} ({_store.CacheBytes() / 1024.0 / 1024.0:0.#} MB)",
            $"PowerShell: {PowerShellLocator.Find() ?? "NOT FOUND"}",
            $"Status: {StatusHeadline}",
            $"Detail: {StatusDetail}",
            "",
            "-- recent log --",
            LogText
        });

        try
        {
            Clipboard.SetText(text);
            _log.Report("Diagnostics copied to clipboard.");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _log.Report("Could not reach the clipboard. Try again.");
        }

        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- helpers

    private async Task<BuildRecord?> FindUsableRecordAsync(string commitId)
    {
        var record = (await _store.ReadAllAsync())
            .FirstOrDefault(r => r.CommitId == commitId && r.IsFetchable);
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

    /// <summary>
    /// Rebuilds the commit table. Two store calls total, whatever the row count: the records
    /// and the in-flight claims are each fetched once and joined in memory.
    /// </summary>
    private async Task ReloadRowsAsync()
    {
        var commits = await _dv.GetLogAsync(60);

        Dictionary<string, BuildRecord> records;
        Dictionary<string, ClaimInfo> claims;
        try
        {
            records = (await _store.ReadAllAsync()).ToDictionary(r => r.CommitId);
            claims = await _store.ActiveClaimsAsync(ClaimMaxAge);
        }
        catch (Exception e)
        {
            _log.Report($"Could not read {_store.Description}: {e.Message}");
            records = [];
            claims = [];
        }

        _installedCommit = _installer.ReadInstalled()?.CommitId;

        Rows.Clear();
        foreach (var commit in commits)
        {
            records.TryGetValue(commit.CommitId, out var record);
            claims.TryGetValue(commit.Ordinal.ToString(), out var claim);

            Rows.Add(new CommitRowViewModel
            {
                Commit = commit,
                Record = record,
                IsWorkspace = commit.CommitId == WorkspaceCommit,
                IsInstalled = commit.CommitId == _installedCommit,
                ClaimedBy = claim?.Machine,
                ClaimAge = claim?.Describe
            });
        }

        OnPropertyChanged(nameof(InstalledCommitText));
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        // Checked before anything else, because everything else is downstream of it. This is
        // named specifically rather than falling through to "no bucket configured", because
        // the cause and the fix are different: a generated config almost always means the
        // project's Tools folder has not arrived from version control yet, and the answer is
        // to sync rather than to start filling in credentials.
        if (ConfigLoader.WasConfigGeneratedFor(_config.ProjectRoot))
        {
            SetStatus(StatusKind.Error,
                "This project had no Unhinged Sync config, so a blank one was generated",
                "If your team already uses this tool, Tools/unhingedsync.json comes from " +
                "Diversion and has the bucket details in it, so this most likely means the " +
                "project has not finished syncing.\n\n" +
                "Sync the project, delete the generated file, and reopen. Do not commit the " +
                "generated one over the team's.");
            return;
        }

        if (!_store.IsConfigured)
        {
            SetStatus(StatusKind.Error, "No bucket configured for this project",
                "Fill in the \"storage\" block in Tools/unhingedsync.json, then check it with " +
                "UnhingedSync.exe --storagetest.");
            return;
        }

        if (!_store.LastKnownReachable)
        {
            SetStatus(StatusKind.Warning, "Could not reach the build store",
                $"{_store.Description} did not answer. Check your connection, then press Refresh.");
            return;
        }

        // The .uproject states which engine version the project targets. Building with a
        // different one is not a warning-and-carry-on situation: assets are versioned to
        // the engine that wrote them.
        //
        // Checked before the share-location warning below on purpose. Both are warnings, but
        // only one of them can silently upgrade assets past the point of return, so it must
        // not be possible for a misconfigured folder path to hide it.
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

        // No "the two disagree" warning any more. AdoptSyncthingFolderAsync follows
        // Syncthing on every refresh, so a disagreement resolves itself instead of asking
        // someone to go and set an environment variable.

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
                "Do not open the editor until these match. Mismatched C++ and content can " +
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

