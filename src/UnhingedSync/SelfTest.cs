using System.IO;
using System.Text.Json;
using UnhingedSync.Services;

namespace UnhingedSync;

/// <summary>
/// Exercises every service against the real machine and writes a JSON report.
/// Run with: UnhingedSync.exe --selftest [outputPath]
/// </summary>
public static class SelfTest
{
    public static async Task<int> RunAsync(string? outputPath)
    {
        outputPath ??= Path.Combine(Path.GetTempPath(), "unhingedsync-selftest.json");
        var results = new List<object>();
        var failures = 0;

        void Record(string name, bool ok, object? detail)
        {
            if (!ok) failures++;
            results.Add(new { check = name, ok, detail });
        }

        // --- config -------------------------------------------------------------
        Models.AppConfig? config = null;
        try
        {
            config = ConfigLoader.Load();
            Record("config.load", true, new
            {
                config.ProjectRoot,
                config.PublishRoot,
                config.EditorTarget,
                expectedBuildId = config.Engine.ExpectedBuildId
            });
        }
        catch (Exception e)
        {
            Record("config.load", false, e.Message);
        }

        // --- known projects -----------------------------------------------------
        try
        {
            var known = ConfigLoader.GetKnownProjects();
            Record("projects.known", known.Count > 0, new
            {
                count = known.Count,
                projects = known,
                localConfig = ConfigLoader.LocalConfigPath
            });
        }
        catch (Exception e)
        {
            Record("projects.known", false, e.Message);
        }

        // --- engine -------------------------------------------------------------
        Models.EngineInfo? engine = null;
        if (config is not null)
        {
            try
            {
                engine = EngineLocator.Locate(config);
                Record("engine.locate", true, new
                {
                    engine.InstallDir, engine.Version, engine.BuildId,
                    engine.Changelist, engine.CompatibleChangelist,
                    matchesConfig = engine.BuildId == config.Engine.ExpectedBuildId
                });
            }
            catch (Exception e)
            {
                Record("engine.locate", false, e.Message);
            }
        }

        // --- engines available for the selector ---------------------------------
        try
        {
            var installed = EngineLocator.EnumerateInstalled();
            Record("engine.installed", installed.Count > 0, new
            {
                count = installed.Count,
                engines = installed.Select(e => new { e.Version, e.BuildId, e.InstallDir }),
                association = config is not null
                    ? EngineLocator.ReadEngineAssociation(config.ProjectRoot, config.ProjectFile)
                    : null
            });
        }
        catch (Exception e)
        {
            Record("engine.installed", false, e.Message);
        }

        // --- embedded scripts ---------------------------------------------------
        try
        {
            var missing = EmbeddedScripts.Missing();
            Record("scripts.embedded", missing.Count == 0, new
            {
                version = EmbeddedScripts.Version,
                extractedTo = EmbeddedScripts.Directory,
                missing
            });
        }
        catch (Exception e)
        {
            Record("scripts.embedded", false, e.Message);
        }

        // --- diversion ----------------------------------------------------------
        if (config is not null)
        {
            try
            {
                var dv = new DvCli(config.ProjectRoot);
                var commit = await dv.GetWorkspaceCommitAsync();
                var branch = await dv.GetBranchAsync();
                var log = await dv.GetLogAsync(10);

                Record("dv.workspaceCommit", true, commit);
                Record("dv.branch", true, branch);
                Record("dv.log", log.Count > 0, new
                {
                    count = log.Count,
                    newest = log.FirstOrDefault() is { } c
                        ? new { c.CommitId, c.Ordinal, c.AuthorEmail, date = c.Date?.ToString("o"), c.Message }
                        : null,
                    allParsedOrdinals = log.All(x => x.Ordinal > 0),
                    allParsedDates = log.All(x => x.Date is not null)
                });
            }
            catch (Exception e)
            {
                Record("dv", false, e.Message);
            }
        }

        // --- build store --------------------------------------------------------
        if (config is not null)
        {
            try
            {
                var store = new BuildStore(config);
                var records = store.ReadAll();
                Record("store.read", true, new
                {
                    store.Root,
                    reachable = store.IsReachable,
                    recordCount = records.Count,
                    fetchable = records.Count(r => r.IsFetchable),
                    // Per-record status exposes the reconcile rules: a record claiming
                    // success whose zip vanished must read as expired, and one whose zip
                    // is the wrong size must read as still syncing.
                    reconciled = records.Select(r => new
                    {
                        r.CommitId, r.Status, r.ZipName, r.ZipBytes, r.IsFetchable
                    }).ToList(),
                    claimOnNewest = records.FirstOrDefault() is { } n
                        ? store.ActiveClaimBy(n.CommitOrdinal.ToString(), TimeSpan.FromMinutes(90))
                        : null,
                    claimOn53 = store.ActiveClaimBy("53", TimeSpan.FromMinutes(90))
                });
            }
            catch (Exception e)
            {
                Record("store.read", false, e.Message);
            }
        }

        // --- installed marker ---------------------------------------------------
        if (config is not null)
        {
            try
            {
                var installed = new BinaryInstaller(config).ReadInstalled();
                Record("installer.readInstalled", true, installed is null
                    ? "no binaries installed by UnhingedSync yet"
                    : new { installed.CommitId, installed.EngineBuildId, fileCount = installed.Files.Count });
            }
            catch (Exception e)
            {
                Record("installer.readInstalled", false, e.Message);
            }
        }

        // --- local build capability --------------------------------------------
        if (config is not null && engine is not null)
        {
            try
            {
                var capability = new LocalBuilder(config).CanBuild(engine);
                Record("builder.canBuild", true, new { capability.CanBuild, capability.Reason });
            }
            catch (Exception e)
            {
                Record("builder.canBuild", false, e.Message);
            }
        }

        var report = new
        {
            generatedUtc = DateTimeOffset.UtcNow.ToString("o"),
            machine = Environment.MachineName,
            failures,
            checks = results
        };

        await File.WriteAllTextAsync(outputPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        return failures == 0 ? 0 : 1;
    }
}
