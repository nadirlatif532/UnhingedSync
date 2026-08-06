using System.IO;
using System.Text.Json;
using UnhingedSync.Services;

namespace UnhingedSync;

/// <summary>
/// Confirms the object store is reachable and the token works, before anybody depends on it.
///   UnhingedSync.exe --storagetest [outputPath]
///   UnhingedSync.exe --storagetest --write     also proves publishing works
///
/// Exists because every way this can be misconfigured looks the same from the app: a wrong
/// bucket name, a token scoped to a different bucket, a read-only token on a machine that
/// needs to publish, and a typo in the account ID all end up as "no builds found".
/// </summary>
public static class StorageTest
{
    public static async Task<int> RunAsync(string? outputPath, bool includeWrite)
    {
        outputPath ??= Path.Combine(Path.GetTempPath(), "unhingedsync-storagetest.json");

        object report;
        var ok = false;

        try
        {
            var config = ConfigLoader.Load();
            var storage = config.Storage;

            if (!storage.IsConfigured)
            {
                report = new
                {
                    ok = false,
                    reason = "Storage is not configured yet.",
                    missing = storage.DescribeWhatIsMissing(),
                    fillThisIn = ConfigLoader.FindSharedConfig(config.ProjectRoot),
                    hint = "Add accountId, bucket, accessKeyId and secretAccessKey under " +
                           "\"storage\" in the project's Tools/unhingedsync.json. Create the " +
                           "token at Cloudflare: R2, then Manage API Tokens, Object Read and " +
                           "Write, scoped to just this bucket."
                };
            }
            else
            {
                using var store = new ObjectStore(storage);
                var failure = await store.CheckAsync(includeWrite);

                if (failure is not null)
                {
                    report = new
                    {
                        ok = false,
                        endpoint = storage.ResolvedEndpoint,
                        bucket = storage.Bucket,
                        writeChecked = includeWrite,
                        reason = failure
                    };
                }
                else
                {
                    var records = await store.ListAsync("records/");
                    var zips = await store.ListAsync("");

                    ok = true;
                    report = new
                    {
                        ok,
                        endpoint = storage.ResolvedEndpoint,
                        bucket = storage.Bucket,
                        prefix = string.IsNullOrEmpty(storage.Prefix) ? "(none)" : storage.Prefix,
                        writeChecked = includeWrite,
                        recordCount = records.Count,
                        objectCount = zips.Count,
                        totalMegabytes = Math.Round(zips.Sum(o => o.Size) / 1024.0 / 1024.0, 1),
                        newestRecords = records
                            .OrderByDescending(r => r.LastModified ?? DateTimeOffset.MinValue)
                            .Take(5)
                            .Select(r => new { r.Key, r.Size, modified = r.LastModified?.ToString("o") })
                    };
                }
            }
        }
        catch (Exception e)
        {
            report = new { ok = false, error = $"{e.GetType().Name}: {e.Message}" };
        }

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json);
        Console.Error.WriteLine(json);

        return ok ? 0 : 1;
    }
}
