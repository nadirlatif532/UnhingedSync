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

        // --- path normalisation -------------------------------------------------
        // Syncthing stores folder paths as typed, so "~/UnhingedShare" and forward slashes
        // both turn up. An unrooted result gets resolved against the project, which would
        // create a literal "~" folder inside the Unreal workspace: the one location the
        // whole publish-root resolver exists to avoid, because dv clean empties it.
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var cases = new (string Input, string Expected)[]
            {
                (@"~\UnhingedShare", Path.Combine(home, "UnhingedShare")),
                ("~/UnhingedShare",  Path.Combine(home, "UnhingedShare")),
                ("~",                home),
                (@"C:/Share/sub",    @"C:\Share\sub")
            };

            var bad = cases
                .Select(c => new { c.Input, c.Expected, Actual = ConfigLoader.NormalisePath(c.Input) })
                .Where(r => !r.Actual.Equals(r.Expected, StringComparison.OrdinalIgnoreCase) ||
                            !Path.IsPathRooted(r.Actual))
                .ToList();

            Record("paths.normalise", bad.Count == 0, bad.Count == 0 ? "all rooted and expanded" : bad);
        }
        catch (Exception e)
        {
            Record("paths.normalise", false, e.Message);
        }

        // --- adopting Syncthing's folder ----------------------------------------
        // The app follows Syncthing rather than warning about a mismatch, so this decision
        // runs on every refresh. Too eager and it rewrites config constantly; too shy and it
        // keeps reading an empty folder while Syncthing replicates elsewhere.
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var checks = new (string Name, string? Live, string Current, bool ExpectAdopt)[]
            {
                ("identical",        @"C:\Share",        @"C:\Share",        false),
                ("trailing slash",   @"C:\Share\",       @"C:\Share",        false),
                ("case differs",     @"c:\share",        @"C:\Share",        false),
                ("forward slashes",  "C:/Share",         @"C:\Share",        false),
                ("tilde expands",    @"~\UnhingedShare", Path.Combine(home, "UnhingedShare"), false),
                ("genuinely moved",  @"D:\Elsewhere",    @"C:\Share",        true),
                ("no live folder",   null,               @"C:\Share",        false),
                ("blank live",       "   ",              @"C:\Share",        false)
            };

            var wrong = checks
                .Select(c => new { c.Name, Adopted = ConfigLoader.ResolveAdoption(c.Live, c.Current), c.ExpectAdopt })
                .Where(r => (r.Adopted is not null) != r.ExpectAdopt)
                .ToList();

            Record("paths.adoption", wrong.Count == 0,
                wrong.Count == 0 ? $"{checks.Length} cases correct" : wrong);
        }
        catch (Exception e)
        {
            Record("paths.adoption", false, e.Message);
        }

        // --- the Syncthing client primes itself ---------------------------------
        // Two callers were written against this class without calling GetStatusAsync first,
        // and both failed silently: an unprimed client sends an empty API key to a hardcoded
        // port, and TryGetAsync turns the 403 into a null that reads as a real answer. One
        // reported every share as 0% synced forever, the other decided nobody could delete.
        // ConfigPath is set only by TryLoadCredentials, so it witnesses that priming ran.
        if (config is not null)
        {
            try
            {
                var fresh = new SyncthingClient();
                var before = fresh.ConfigPath;
                await fresh.GetLocalCompletionAsync(config.SyncthingFolderId);

                Record("syncthing.selfPriming", before is null && fresh.ConfigPath is not null,
                    new
                    {
                        configPathBeforeCall = before,
                        configPathAfterCall = fresh.ConfigPath,
                        resolvedEndpoint = fresh.WebUiUri,
                        note = "a plain data call must prime credentials without GetStatusAsync"
                    });
            }
            catch (Exception e)
            {
                Record("syncthing.selfPriming", false, e.Message);
            }
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

        // Every other diagnostic mode prints; this one silently wrote a file and left the
        // caller staring at nothing. A summary rather than the whole report, because this
        // one is long, with the failures spelled out since those are the reason to run it.
        Console.Error.WriteLine($"{results.Count - failures}/{results.Count} checks passed.");
        foreach (var result in results)
        {
            var line = JsonSerializer.SerializeToElement(result);
            if (line.TryGetProperty("ok", out var okValue) && !okValue.GetBoolean())
            {
                var name = line.TryGetProperty("check", out var c) ? c.GetString() : "?";
                var detail = line.TryGetProperty("detail", out var d) ? d.ToString() : "";
                Console.Error.WriteLine($"  FAILED  {name}: {detail}");
            }
        }
        Console.Error.WriteLine($"Full report: {outputPath}");

        return failures == 0 ? 0 : 1;
    }
}
