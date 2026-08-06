using System.IO;
using System.Text.Json;
using UnhingedSync.Services;

namespace UnhingedSync;

/// <summary>
/// Compiles the workspace commit and uploads the result, with no window.
///   UnhingedSync.exe --build
///
/// The same path the button uses, so a build box can be driven from a scheduled task and
/// there is one implementation rather than two that drift.
/// </summary>
public static class BuildCommand
{
    public static async Task<int> RunAsync(string? outputPath)
    {
        outputPath ??= Path.Combine(Path.GetTempPath(), "unhingedsync-build.json");

        var lines = new List<string>();
        var log = new Progress<string>(line =>
        {
            lines.Add(line);
            Console.Error.WriteLine(line);
        });

        object report;
        var ok = false;

        try
        {
            var config = ConfigLoader.Load();
            var engine = EngineLocator.Locate(config);
            using var store = new BuildStore(config);

            if (!store.IsConfigured)
            {
                report = new { ok = false, reason = "No bucket configured. See --storagetest." };
            }
            else
            {
                var builder = new LocalBuilder(config);
                var capability = builder.CanBuild(engine);

                if (!capability.CanBuild)
                {
                    report = new { ok = false, reason = capability.Reason };
                }
                else
                {
                    var commitId = await new DvCli(config.ProjectRoot).GetWorkspaceCommitAsync();
                    var commitShort = commitId.Replace("dv.commit.", "");

                    Console.Error.WriteLine($"Building {commitId} for {config.ProjectName}…");
                    var result = await builder.BuildAndPublishAsync(store, commitShort, log);

                    ok = result.Succeeded;
                    report = new
                    {
                        ok,
                        commitId,
                        result.ZipName,
                        engine = engine.Version,
                        engineBuildId = engine.BuildId,
                        reason = ok ? null : "See the log for the compiler output."
                    };
                }
            }
        }
        catch (Exception e)
        {
            report = new { ok = false, reason = $"{e.GetType().Name}: {e.Message}" };
        }

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(outputPath, json);
        Console.Error.WriteLine(json);

        return ok ? 0 : 1;
    }
}
