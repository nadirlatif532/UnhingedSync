using System.IO;
using System.Net.Http;
using System.Text.Json;
using UnhingedSync.Models;

namespace UnhingedSync.Services;

/// <summary>
/// The published builds, held in an object store.
///
/// The layout is unchanged from when this was a replicated folder, because it was already
/// append-only with uniquely named files and that maps onto object keys directly:
///
///     &lt;project&gt;-&lt;target&gt;-&lt;platform&gt;-&lt;config&gt;-&lt;commit&gt;.zip
///     records/&lt;commit&gt;-&lt;MACHINE&gt;.json
///     claims/&lt;commit&gt;-&lt;MACHINE&gt;.claim
///     logs/&lt;commit&gt;-&lt;MACHINE&gt;.log
///
/// What changed is that presence is now a fact rather than an inference. Over a replicated
/// folder the only available signal was the local file's size, so a build still arriving and
/// a build deleted by retention were genuinely hard to tell apart, and the app showed the
/// former as "expired" because Syncthing downloads under a temporary name. A listing answers
/// the question exactly, so the "syncing" state is gone entirely.
/// </summary>
public sealed class BuildStore : IDisposable
{
    private readonly AppConfig _config;
    private readonly ObjectStore? _remote;
    private readonly string _cacheDir;

    public BuildStore(AppConfig config)
    {
        _config = config;
        if (config.Storage.IsConfigured) _remote = new ObjectStore(config.Storage);

        // Keyed by bucket and prefix so two projects sharing one machine cannot collide,
        // which the old single shared publish root allowed.
        var slug = new string((config.Storage.Bucket + "-" + config.Storage.Prefix)
            .Where(c => char.IsLetterOrDigit(c) || c is '-').ToArray()).Trim('-');
        if (slug.Length == 0) slug = "default";

        _cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnhingedSync", "cache", slug);
    }

    public string CacheDir => _cacheDir;

    public bool IsConfigured => _remote is not null;

    /// <summary>Where builds live, for messages. Not a path any more.</summary>
    public string Description => _remote is null
        ? "no bucket configured"
        : string.IsNullOrEmpty(_config.Storage.Prefix)
            ? $"{_config.Storage.Bucket} ({_config.Storage.ResolvedEndpoint})"
            : $"{_config.Storage.Bucket}/{_config.Storage.Prefix}";

    /// <summary>
    /// Whether the store answered last time we asked. Not a live check: callers that need
    /// certainty should handle the exception from an actual operation.
    /// </summary>
    public bool LastKnownReachable { get; private set; } = true;

    private ObjectStore Remote => _remote ?? throw new InvalidOperationException(
        "No object store is configured for this project. Fill in the \"storage\" block in " +
        "Tools/unhingedsync.json, then run: UnhingedSync.exe --storagetest");

    // ---------------------------------------------------------------- reading

    /// <summary>
    /// One entry per commit, newest first. Where several machines published the same commit,
    /// a usable build wins over a failed one.
    /// </summary>
    public async Task<List<BuildRecord>> ReadAllAsync(CancellationToken ct = default)
    {
        if (_remote is null) return [];

        List<RemoteObject> all;
        try
        {
            all = await Remote.ListAsync("", ct);
            LastKnownReachable = true;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                     or Amazon.S3.AmazonS3Exception)
        {
            LastKnownReachable = false;
            throw;
        }

        // One listing answers both questions: which records exist, and which zips are
        // actually still there. No per-object HEAD, and no guessing.
        var present = all.Select(o => o.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var recordObjects = all
            .Where(o => o.Key.StartsWith("records/", StringComparison.OrdinalIgnoreCase) &&
                        o.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var records = new List<BuildRecord>();
        foreach (var chunk in recordObjects.Chunk(8))
        {
            var fetched = await Task.WhenAll(chunk.Select(o => ReadRecordCachedAsync(o, ct)));
            records.AddRange(fetched.Where(r => r is not null)!);
        }

        return records
            .Select(r => Reconcile(r, present))
            .GroupBy(r => r.CommitId)
            .Select(g => g.OrderByDescending(r => r.IsFetchable)
                          .ThenByDescending(r => r.BuiltUtc ?? DateTimeOffset.MinValue)
                          .First())
            .OrderByDescending(r => r.CommitOrdinal)
            .ToList();
    }

    /// <summary>
    /// Reads one record, reusing a cached copy when the object has not changed.
    ///
    /// Records are rewritten only when retention expires one, so re-downloading every record
    /// on every refresh would be almost entirely wasted. The cached file's timestamp is set
    /// to the object's, which makes the comparison a single stat rather than a manifest to
    /// keep in step.
    /// </summary>
    private async Task<BuildRecord?> ReadRecordCachedAsync(RemoteObject remote, CancellationToken ct)
    {
        var localPath = Path.Combine(_cacheDir, remote.Key.Replace('/', Path.DirectorySeparatorChar));
        var stamp = remote.LastModified?.UtcDateTime;

        try
        {
            if (stamp is not null && File.Exists(localPath) &&
                Math.Abs((File.GetLastWriteTimeUtc(localPath) - stamp.Value).TotalSeconds) < 1.5)
            {
                return Deserialise(await File.ReadAllTextAsync(localPath, ct));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        var json = await Remote.GetTextAsync(remote.Key, ct);
        if (json is null) return null;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
            await File.WriteAllTextAsync(localPath, json, ct);
            if (stamp is not null) File.SetLastWriteTimeUtc(localPath, stamp.Value);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be written costs a little bandwidth, nothing more.
        }

        return Deserialise(json);
    }

    private static BuildRecord? Deserialise(string json)
    {
        try
        {
            var record = JsonSerializer.Deserialize<BuildRecord>(json, Json.Options);
            return record is null || string.IsNullOrEmpty(record.CommitId) ? null : record;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Retention deletes zips but only the publishing machine rewrites its own record, so a
    /// record can claim success long after its payload is gone. Presence in the listing
    /// settles it. There is deliberately no "still arriving" state: a download either
    /// completed and was verified, or it is not in the cache at all.
    /// </summary>
    private static BuildRecord Reconcile(BuildRecord record, HashSet<string> present)
    {
        if (record.Status != "success" || string.IsNullOrEmpty(record.ZipName)) return record;

        if (!present.Contains(record.ZipName))
        {
            record.Status = "expired";
            record.ZipName = null;
        }
        return record;
    }

    /// <summary>
    /// Makes the build's zip available locally, downloading and verifying it if needed, and
    /// returns the path. Reuses a previous download only when its checksum still matches.
    /// </summary>
    public async Task<string> EnsureLocalZipAsync(
        BuildRecord record, IProgress<string>? log = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(record.ZipName))
            throw new InvalidOperationException(
                $"No payload is published for {record.CommitId} any more.");

        var localPath = Path.Combine(_cacheDir, record.ZipName);

        if (File.Exists(localPath) && !string.IsNullOrWhiteSpace(record.ZipSha256))
        {
            var existing = await ObjectStore.Sha256Async(localPath, ct);
            if (existing.Equals(record.ZipSha256, StringComparison.OrdinalIgnoreCase))
            {
                log?.Report($"Using the copy of {record.ZipName} already downloaded.");
                return localPath;
            }
            log?.Report($"The cached {record.ZipName} does not match its checksum; downloading again.");
        }
        else if (File.Exists(localPath))
        {
            return localPath;
        }

        await Remote.DownloadAsync(record.ZipName, localPath, record.ZipSha256, log, ct);
        return localPath;
    }

    /// <summary>The build log, downloaded on demand. Null when it was never published.</summary>
    public async Task<string?> EnsureLocalLogAsync(BuildRecord record, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(record.LogName)) return null;

        var localPath = Path.Combine(_cacheDir, record.LogName.Replace('/', Path.DirectorySeparatorChar));
        var text = await Remote.GetTextAsync(record.LogName, ct);
        if (text is null) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        await File.WriteAllTextAsync(localPath, text, ct);
        return localPath;
    }

    // ---------------------------------------------------------------- claims

    /// <summary>
    /// Another machine currently building this commit, if any. The store's own timestamps
    /// are used for age, which is more trustworthy than the file times this relied on when
    /// the folder was replicated.
    /// </summary>
    public async Task<string?> ActiveClaimByAsync(
        string commitShort, TimeSpan maxAge, CancellationToken ct = default)
    {
        if (_remote is null) return null;

        // Sanitised because this reaches a key prefix and the value comes from published
        // records, not from us.
        var stem = new string((commitShort ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (stem.Length == 0) return null;

        List<RemoteObject> claims;
        try { claims = await Remote.ListAsync($"claims/{stem}-", ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                     or Amazon.S3.AmazonS3Exception)
        {
            return null;
        }

        var mine = Environment.MachineName;
        foreach (var claim in claims)
        {
            if (claim.LastModified is { } when && DateTimeOffset.UtcNow - when > maxAge) continue;

            // claims/<commit>-<MACHINE>.claim
            var name = Path.GetFileNameWithoutExtension(claim.Key);
            var dash = name.IndexOf('-');
            if (dash < 0 || dash + 1 >= name.Length) continue;

            var machine = name[(dash + 1)..];
            if (!machine.Equals(mine, StringComparison.OrdinalIgnoreCase)) return machine;
        }
        return null;
    }

    /// <summary>
    /// Every commit somebody else is building right now, keyed by commit stem.
    ///
    /// One listing for the whole table. The per-commit lookup was being called once per row,
    /// so painting sixty commits meant sixty listings, which was tolerable against a local
    /// folder and is not against a bucket.
    /// </summary>
    public async Task<Dictionary<string, string>> ActiveClaimsAsync(
        TimeSpan maxAge, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_remote is null) return result;

        List<RemoteObject> claims;
        try { claims = await Remote.ListAsync("claims/", ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                     or Amazon.S3.AmazonS3Exception)
        {
            return result;
        }

        var mine = Environment.MachineName;
        foreach (var claim in claims)
        {
            if (claim.LastModified is { } when && DateTimeOffset.UtcNow - when > maxAge) continue;

            var name = Path.GetFileNameWithoutExtension(claim.Key);
            var dash = name.IndexOf('-');
            if (dash <= 0 || dash + 1 >= name.Length) continue;

            var stem = name[..dash];
            var machine = name[(dash + 1)..];
            if (machine.Equals(mine, StringComparison.OrdinalIgnoreCase)) continue;

            result[stem] = machine;
        }
        return result;
    }

    public Task WriteClaimAsync(string commitShort, CancellationToken ct = default) =>
        Remote.PutTextAsync($"claims/{commitShort}-{Environment.MachineName}.claim",
            $"{{\"machine\":\"{Environment.MachineName}\",\"utc\":\"{DateTimeOffset.UtcNow:o}\"}}", ct);

    public Task RemoveClaimAsync(string commitShort, CancellationToken ct = default) =>
        Remote.DeleteAsync($"claims/{commitShort}-{Environment.MachineName}.claim", ct);

    // ---------------------------------------------------------------- publishing

    public Task PutZipAsync(
        string zipName, string localPath, IProgress<string>? log = null, CancellationToken ct = default) =>
        Remote.PutFileAsync(zipName, localPath, log, ct);

    public Task PutTextAsync(string key, string content, CancellationToken ct = default) =>
        Remote.PutTextAsync(key, content, ct);

    // ---------------------------------------------------------------- deleting

    /// <summary>
    /// Deletes a build's payload and logs. Returns null on success, or a reason. Never
    /// throws, because one failure must not abandon a multi-build clean up halfway.
    /// </summary>
    public async Task<string?> DeletePayloadAsync(BuildRecord record, CancellationToken ct = default)
    {
        var failures = new List<string>();

        async Task TryDelete(string key)
        {
            try { await Remote.DeleteAsync(key, ct); }
            catch (Exception e) when (e is Amazon.S3.AmazonS3Exception or HttpRequestException
                                         or TaskCanceledException)
            {
                failures.Add($"{key}: {e.Message}");
            }
        }

        if (!string.IsNullOrEmpty(record.ZipName)) await TryDelete(record.ZipName);

        // Logs are per machine, so a commit two people built has two of them, and the record
        // in hand names only one. Retention sweeps them all and this matches.
        var stem = new string((string.IsNullOrEmpty(record.CommitShort)
            ? record.CommitOrdinal.ToString()
            : record.CommitShort).Where(char.IsLetterOrDigit).ToArray());

        if (stem.Length > 0)
        {
            try
            {
                foreach (var log in await Remote.ListAsync($"logs/{stem}-", ct))
                    await TryDelete(log.Key);
            }
            catch (Exception e) when (e is Amazon.S3.AmazonS3Exception or HttpRequestException
                                         or TaskCanceledException)
            {
                failures.Add($"logs: {e.Message}");
            }
        }

        // The local copy too, or a deleted build lingers in this machine's cache.
        if (!string.IsNullOrEmpty(record.ZipName))
        {
            try
            {
                var cached = Path.Combine(_cacheDir, record.ZipName);
                if (File.Exists(cached)) File.Delete(cached);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    /// <summary>Total bytes this machine is holding in its download cache.</summary>
    public long CacheBytes()
    {
        try
        {
            return Directory.Exists(_cacheDir)
                ? new DirectoryInfo(_cacheDir).EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length)
                : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void Dispose() => _remote?.Dispose();
}
