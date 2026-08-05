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

    /// <summary>Another machine currently building this commit, if any.</summary>
    public string? ActiveClaimBy(string commitShort, TimeSpan maxAge)
    {
        if (!Directory.Exists(ClaimsDir)) return null;
        var mine = Environment.MachineName;

        foreach (var file in Directory.EnumerateFiles(ClaimsDir, $"{commitShort}-*.claim"))
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
