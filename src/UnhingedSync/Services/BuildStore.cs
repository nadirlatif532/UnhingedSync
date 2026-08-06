using System.IO;
using System.Text.Json;
using UnhingedSync.Models;

namespace UnhingedSync.Services;

/// <summary>
/// Reads the published build records. The store is append-only -- one JSON file per
/// (commit, machine) -- because several people can publish into the same
/// Syncthing-replicated folder and a single mutable index would conflict.
/// </summary>
public sealed class BuildStore(AppConfig config)
{
    private readonly string _root = config.PublishRoot;

    public string Root => _root;
    public string RecordsDir => Path.Combine(_root, "records");
    public string ClaimsDir => Path.Combine(_root, "claims");
    public string LogsDir => Path.Combine(_root, "logs");

    public bool IsReachable => Directory.Exists(_root);

    /// <summary>
    /// Makes a commit stem safe to use as a search pattern.
    ///
    /// These values come out of JSON records that other machines write, so they are input,
    /// not constants. A stem containing ':' makes EnumerateFiles throw ArgumentException
    /// (which is not an IOException, so it escapes the usual handling); '..\' makes the glob
    /// escape into the publish root; and '*' would match every log in the share. Commit
    /// stems are ordinals or short hashes, so anything outside that is dropped rather than
    /// escaped.
    /// </summary>
    private static string SafeStem(BuildRecord record)
    {
        var raw = string.IsNullOrEmpty(record.CommitShort)
            ? record.CommitOrdinal.ToString()
            : record.CommitShort;

        var clean = new string(raw.Where(char.IsLetterOrDigit).ToArray());
        return clean.Length == 0 ? "" : clean;
    }

    /// <summary>
    /// One entry per commit, newest first. Where several machines published the same
    /// commit, a usable build wins over a failed one.
    /// </summary>
    public List<BuildRecord> ReadAll()
    {
        if (!Directory.Exists(RecordsDir)) return [];

        var records = new List<BuildRecord>();
        foreach (var file in Directory.EnumerateFiles(RecordsDir, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<BuildRecord>(File.ReadAllText(file), Json.Options);
                if (record is not null && !string.IsNullOrEmpty(record.CommitId))
                    records.Add(Reconcile(record));
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                // A record still replicating in is simply not visible yet.
            }
        }

        return records
            .GroupBy(r => r.CommitId)
            .Select(g => g.OrderByDescending(r => r.IsFetchable)
                          .ThenByDescending(r => r.BuiltUtc ?? DateTimeOffset.MinValue)
                          .First())
            .OrderByDescending(r => r.CommitOrdinal)
            .ToList();
    }

    /// <summary>
    /// Retention deletes zips but only the publishing machine rewrites its own record,
    /// so any reader must treat "claims success but the zip is gone" as expired. A zip
    /// whose size does not match the record is still replicating, not yet usable.
    /// </summary>
    private BuildRecord Reconcile(BuildRecord record)
    {
        if (record.Status != "success" || string.IsNullOrEmpty(record.ZipName)) return record;

        var zipPath = Path.Combine(_root, record.ZipName);
        if (!File.Exists(zipPath))
        {
            record.Status = "expired";
            record.ZipName = null;
            return record;
        }

        if (record.ZipBytes > 0 && new FileInfo(zipPath).Length != record.ZipBytes)
            record.Status = "syncing";

        return record;
    }

    public string? ZipPathFor(BuildRecord record) =>
        string.IsNullOrEmpty(record.ZipName) ? null : Path.Combine(_root, record.ZipName);

    public string? LogPathFor(BuildRecord record) =>
        string.IsNullOrEmpty(record.LogName) ? null : Path.Combine(_root, record.LogName);

    /// <summary>
    /// Deletes a build's zip and logs from the publish root. On a send-receive folder this
    /// frees space for the whole team, the same as the build script's retention pass; on a
    /// receive-only folder Syncthing will not propagate it, which is why callers must check
    /// the folder type before offering this.
    ///
    /// The record file is deliberately left alone: every reader already treats "success
    /// record, zip missing" as expired, and rewriting another machine's record is exactly
    /// what would produce sync-conflict copies.
    ///
    /// Returns null on success, or a reason. It never throws -- a zip held open by
    /// Syncthing mid-transfer is ordinary, and it used to take the whole app down with it
    /// partway through a multi-build delete.
    /// </summary>
    public string? DeletePayload(BuildRecord record)
    {
        var failures = new List<string>();

        void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{Path.GetFileName(path)}: {e.Message}");
            }
        }

        if (ZipPathFor(record) is { } zip) TryDelete(zip);

        // Logs are named per machine, so a commit two people built has two of them. The
        // record we hold is only one of those, and retention sweeps them all -- matching
        // that here stops the other machine's log being orphaned forever, since no later
        // retention pass revisits a commit whose zip is already gone.
        var stem = SafeStem(record);

        if (stem.Length > 0 && Directory.Exists(LogsDir))
        {
            try
            {
                foreach (var log in Directory.EnumerateFiles(LogsDir, $"{stem}-*.log")) TryDelete(log);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                failures.Add($"logs: {e.Message}");
            }
        }
        if (LogPathFor(record) is { } exact) TryDelete(exact);

        return failures.Count == 0 ? null : string.Join("; ", failures);
    }

    /// <summary>Actual size on disk, for when a record's recorded size is missing or stale.</summary>
    public long ActualZipBytes(BuildRecord record)
    {
        try
        {
            return ZipPathFor(record) is { } zip && File.Exists(zip) ? new FileInfo(zip).Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Another machine currently building this commit, if any.</summary>
    public string? ActiveClaimBy(string commitShort, TimeSpan maxAge)
    {
        if (!Directory.Exists(ClaimsDir)) return null;

        // Same reasoning as SafeStem: this is called with values derived from published
        // records, and it now runs while a window is being constructed, so a malformed one
        // must not be able to throw out of a constructor.
        var stem = new string((commitShort ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (stem.Length == 0) return null;

        var mine = Environment.MachineName;

        IEnumerable<string> claims;
        try { claims = Directory.EnumerateFiles(ClaimsDir, $"{stem}-*.claim").ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }

        foreach (var file in claims)
        {
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > maxAge) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var machine = doc.RootElement.TryGetProperty("machine", out var m) ? m.GetString() : null;
                if (!string.IsNullOrEmpty(machine) &&
                    !machine.Equals(mine, StringComparison.OrdinalIgnoreCase))
                    return machine;
            }
            catch (Exception e) when (e is JsonException or IOException) { }
        }
        return null;
    }
}
