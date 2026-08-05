using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using UnhingedSync.Models;

namespace UnhingedSync.Services;

/// <summary>Installs a published payload into the local project tree.</summary>
public sealed class BinaryInstaller(AppConfig config)
{
    private const string MarkerRelative = @"Binaries\.unhingedsync-install.json";

    private string MarkerPath => Path.Combine(config.ProjectRoot, MarkerRelative);

    public InstallRecord? ReadInstalled()
    {
        if (!File.Exists(MarkerPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(MarkerPath), Json.Options);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    public async Task InstallAsync(
        BuildRecord record,
        EngineInfo engine,
        string zipPath,
        IProgress<string> log,
        CancellationToken ct = default)
    {
        if (config.Engine.EnforceBuildIdMatch &&
            !string.IsNullOrEmpty(record.EngineBuildId) &&
            record.EngineBuildId != engine.BuildId)
        {
            throw new InvalidOperationException(
                $"These binaries were built against engine BuildId {record.EngineBuildId} " +
                $"(UE {record.EngineVersion}) but this machine has {engine.BuildId} " +
                $"(UE {engine.Version}). They would not load.\n\n" +
                "Install the matching engine version from the Epic Games Launcher.");
        }

        if (!File.Exists(zipPath))
            throw new FileNotFoundException($"Payload not found: {zipPath}");

        // Verifying the hash also proves the file finished replicating -- a partially
        // synced zip is the most likely failure here, not corruption.
        log.Report($"Verifying {Path.GetFileName(zipPath)} ...");
        var actual = await ComputeSha256Async(zipPath, ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(record.ZipSha256) &&
            !actual.Equals(record.ZipSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Payload checksum does not match the published record. It is most likely " +
                "still syncing -- wait a moment and try again.");
        }

        var previous = ReadInstalled();
        var installed = new List<string>();

        log.Report($"Extracting binaries for {record.CommitId} ...");
        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(entry.Name)) continue; // directory entry

                var destination = ResolveSafeDestination(entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                installed.Add(entry.FullName);
            }
        }
        log.Report($"Extracted {installed.Count} files.");

        RemoveOrphans(previous, installed, log);

        var marker = new InstallRecord
        {
            CommitId = record.CommitId,
            ZipSha256 = actual,
            EngineBuildId = engine.BuildId,
            InstalledUtc = DateTimeOffset.UtcNow,
            Files = installed
        };
        Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
        await File.WriteAllTextAsync(
            MarkerPath, JsonSerializer.Serialize(marker, Json.Options), ct).ConfigureAwait(false);

        log.Report($"Installed binaries for {record.CommitId}.");
    }

    /// <summary>
    /// Zip entries are untrusted input even from our own publisher: reject anything
    /// that would escape the project root.
    /// </summary>
    private string ResolveSafeDestination(string entryPath)
    {
        var root = Path.GetFullPath(config.ProjectRoot);
        var combined = Path.GetFullPath(Path.Combine(root, entryPath.Replace('/', Path.DirectorySeparatorChar)));

        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Refusing to extract outside the project: '{entryPath}'");

        return combined;
    }

    /// <summary>
    /// A DLL left behind from a previous commit still gets loaded by the editor, so
    /// stale files from the last install must go.
    /// </summary>
    private void RemoveOrphans(InstallRecord? previous, List<string> current, IProgress<string> log)
    {
        if (previous is null) return;

        var keep = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = 0;

        foreach (var relative in previous.Files.Where(f => !keep.Contains(f)))
        {
            try
            {
                var path = ResolveSafeDestination(relative);
                if (File.Exists(path)) { File.Delete(path); removed++; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                log.Report($"Could not remove stale file '{relative}': {e.Message}");
            }
        }

        if (removed > 0) log.Report($"Removed {removed} stale file(s) from the previous install.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
