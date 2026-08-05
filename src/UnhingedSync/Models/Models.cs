using System.Text.Json.Serialization;

namespace UnhingedSync.Models;

/// <summary>Shared settings from Tools/unhingedsync.json, plus per-machine overrides.</summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 1;

    // No defaults for project identity on purpose. A missing key must fail loudly
    // rather than quietly building some other project's editor target.
    public string ProjectName { get; set; } = "";
    public string ProjectFile { get; set; } = "";
    public string EditorTarget { get; set; } = "";
    public string Platform { get; set; } = "Win64";
    public string Configuration { get; set; } = "Development";
    public string Branch { get; set; } = "main";
    public string PublishRootDefault { get; set; } = "";
    public int RetainBuilds { get; set; } = 10;
    public EngineConfig Engine { get; set; } = new();

    /// <summary>
    /// Syncthing folder ID for the share. Must match on every machine; the setup
    /// script uses the same default.
    /// </summary>
    public string SyncthingFolderId { get; set; } = "";

    /// <summary>Resolved at load time; never round-tripped to the shared file.</summary>
    [JsonIgnore] public string ProjectRoot { get; set; } = "";
    [JsonIgnore] public string PublishRoot { get; set; } = "";

    /// <summary>
    /// Machine-local engine choice. Install paths differ per machine, so this never
    /// belongs in the committed config.
    /// </summary>
    [JsonIgnore] public string EngineDirOverride { get; set; } = "";
}

public sealed class EngineConfig
{
    public string ExpectedBuildId { get; set; } = "";
    public bool EnforceBuildIdMatch { get; set; } = true;
}

/// <summary>The build index published alongside the zips. Our metadata server.</summary>
public sealed class BuildIndex
{
    public int SchemaVersion { get; set; } = 1;
    public string Project { get; set; } = "";
    public DateTimeOffset? UpdatedUtc { get; set; }
    public List<BuildRecord> Builds { get; set; } = new();
}

public sealed class BuildRecord
{
    public string CommitId { get; set; } = "";
    public string CommitShort { get; set; } = "";
    public int CommitOrdinal { get; set; }
    public string Branch { get; set; } = "";
    public string CommitMessage { get; set; } = "";
    public string CommitAuthor { get; set; } = "";
    public string CommitDateUtc { get; set; } = "";

    /// <summary>success | failed | expired</summary>
    public string Status { get; set; } = "";

    public string Target { get; set; } = "";
    public string Platform { get; set; } = "";
    public string Configuration { get; set; } = "";

    public string EngineBuildId { get; set; } = "";
    public string EngineVersion { get; set; } = "";
    public long EngineChangelist { get; set; }

    public string? ZipName { get; set; }
    public long ZipBytes { get; set; }
    public string? ZipSha256 { get; set; }

    public int FileCount { get; set; }
    public DateTimeOffset? BuiltUtc { get; set; }
    public string BuiltBy { get; set; } = "";
    public int BuildDurationSeconds { get; set; }
    public string? LogName { get; set; }

    [JsonIgnore] public bool IsFetchable => Status == "success" && !string.IsNullOrEmpty(ZipName);
}

/// <summary>Marker written into Binaries/ recording what the client installed.</summary>
public sealed class InstallRecord
{
    public string CommitId { get; set; } = "";
    public string ZipSha256 { get; set; } = "";
    public string EngineBuildId { get; set; } = "";
    public DateTimeOffset InstalledUtc { get; set; }
    public List<string> Files { get; set; } = new();
}

/// <summary>One commit from 'dv log', joined with whatever the index knows about it.</summary>
public sealed class CommitInfo
{
    public string CommitId { get; set; } = "";
    public int Ordinal { get; set; }
    public string AuthorEmail { get; set; } = "";
    public DateTimeOffset? Date { get; set; }
    public string Message { get; set; } = "";
}

public sealed class EngineInfo
{
    public string InstallDir { get; set; } = "";
    public string BuildId { get; set; } = "";
    public string Version { get; set; } = "";
    public long Changelist { get; set; }
    public long CompatibleChangelist { get; set; }
}
