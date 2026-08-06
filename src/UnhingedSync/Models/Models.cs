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
    /// Where published builds live. Committed with the project, which is private, so a
    /// teammate who syncs the project is configured with no further setup.
    /// </summary>
    public StorageConfig Storage { get; set; } = new();

    /// <summary>
    /// Syncthing folder ID, kept only so an older project config still deserialises
    /// without error while the team moves to object storage. Nothing reads it.
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

/// <summary>
/// Credentials and location for the object store holding published builds.
///
/// Both keys live here, in the project's own committed config, which is deliberate: the
/// project repository is private, and one file that just works beats a per-machine setup
/// step twenty five people have to get right. The consequence, stated plainly because it
/// is a real trade: anyone who can open the project can publish and delete builds. The
/// storage layer enforces nothing; the confirmations in the UI are guard rails against
/// mistakes, not against people.
///
/// This must never appear in the Unhinged Sync repository itself, which is public. See
/// CredentialGuard.
/// </summary>
public sealed class StorageConfig
{
    /// <summary>r2, or any S3-compatible service via <see cref="EndpointUrl"/>.</summary>
    public string Provider { get; set; } = "r2";

    /// <summary>Cloudflare account ID, used to derive the R2 endpoint.</summary>
    public string AccountId { get; set; } = "";

    public string Bucket { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";

    /// <summary>Set for a non-R2 S3 service. Otherwise derived from the account ID.</summary>
    public string EndpointUrl { get; set; } = "";

    /// <summary>Optional key prefix, so one bucket can hold several projects.</summary>
    public string Prefix { get; set; } = "";

    [JsonIgnore]
    public string ResolvedEndpoint => !string.IsNullOrWhiteSpace(EndpointUrl)
        ? EndpointUrl
        : $"https://{AccountId}.r2.cloudflarestorage.com";

    [JsonIgnore]
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Bucket) &&
        !string.IsNullOrWhiteSpace(AccessKeyId) &&
        !string.IsNullOrWhiteSpace(SecretAccessKey) &&
        (!string.IsNullOrWhiteSpace(AccountId) || !string.IsNullOrWhiteSpace(EndpointUrl));

    /// <summary>What is missing, for a message worth reading.</summary>
    public string DescribeWhatIsMissing()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(Bucket)) missing.Add("bucket");
        if (string.IsNullOrWhiteSpace(AccessKeyId)) missing.Add("accessKeyId");
        if (string.IsNullOrWhiteSpace(SecretAccessKey)) missing.Add("secretAccessKey");
        if (string.IsNullOrWhiteSpace(AccountId) && string.IsNullOrWhiteSpace(EndpointUrl))
            missing.Add("accountId or endpointUrl");
        return missing.Count == 0 ? "" : string.Join(", ", missing);
    }
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
