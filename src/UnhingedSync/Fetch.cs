using System.IO;
using System.Text.Json;
using UnhingedSync.Services;

namespace UnhingedSync;

/// <summary>
/// Headless install of published binaries, for scripting and for troubleshooting a
/// machine without driving the window.
///   UnhingedSync.exe --fetch [dv.commit.NN]
/// Defaults to the workspace's current commit. Never syncs and never builds.
/// </summary>
public static class Fetch
{
    public static async Task<int> RunAsync(string? commitId)
    {
        var log = new Progress<string>(line => Console.Error.WriteLine(line));

        try
        {
            var config = ConfigLoader.Load();
            var engine = EngineLocator.Locate(config);
            var store = new BuildStore(config);

            if (!store.IsReachable)
            {
                Report(false, $"Publish root is not reachable: {store.Root}");
                return 1;
            }

            commitId ??= await new DvCli(config.ProjectRoot).GetWorkspaceCommitAsync();

            var record = store.ReadAll().FirstOrDefault(r => r.CommitId == commitId);
            if (record is null)
            {
                Report(false, $"No build record published for {commitId}.");
                return 1;
            }
            if (!record.IsFetchable)
            {
                Report(false, $"Build for {commitId} is not fetchable (status: {record.Status}).");
                return 1;
            }

            var zip = store.ZipPathFor(record)!;
            await new BinaryInstaller(config).InstallAsync(record, engine, zip, log);

            Report(true, $"Installed {record.FileCount} files for {commitId}.", new
            {
                commitId,
                record.ZipName,
                record.FileCount,
                record.EngineBuildId,
                builtBy = record.BuiltBy
            });
            return 0;
        }
        catch (Exception e)
        {
            Report(false, $"{e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static void Report(bool ok, string message, object? detail = null)
    {
        var payload = JsonSerializer.Serialize(
            new { ok, message, detail },
            new JsonSerializerOptions { WriteIndented = true });

        // A WinExe has no console attached when double-clicked, so also leave the
        // result somewhere a script can read it deterministically.
        Console.Error.WriteLine(payload);
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "unhingedsync-fetch.json"), payload);
        }
        catch (IOException) { }
    }
}
