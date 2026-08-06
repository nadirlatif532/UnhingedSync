using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using UnhingedSync.Models;

namespace UnhingedSync.Services;

public sealed record BuildCapability(bool CanBuild, string Reason);

public sealed record LocalBuildResult(bool Succeeded, string? ZipName, string? CommitId, string RawOutput);

/// <summary>
/// Drives Invoke-UnhingedBuild.ps1 so anyone with a working toolchain can produce and
/// publish binaries for a commit nobody has built yet.
/// </summary>
public sealed class LocalBuilder(AppConfig config)
{
    private const string ResultPrefix = "UNHINGEDSYNC_RESULT ";

    // Extracted from this exe rather than read out of the project, so the app works on a
    // project that has never seen these tools, and the script always matches the binary.
    private static string ScriptPath => EmbeddedScripts.BuildScript;

    /// <summary>
    /// Cheap pre-flight so an artist without a compiler gets a plain explanation
    /// instead of a wall of UnrealBuildTool output. UBT remains the real authority.
    /// </summary>
    public BuildCapability CanBuild(EngineInfo engine)
    {
        if (EmbeddedScripts.Missing() is { Count: > 0 } missing)
            return new(false, $"This build of the app is missing its own scripts ({string.Join(", ", missing)}). Reinstall it.");

        if (PowerShellLocator.Find() is null)
            return new(false, "PowerShell 7 (pwsh) was not found on this machine.");

        var buildBat = Path.Combine(engine.InstallDir, "Engine", "Build", "BatchFiles", "Build.bat");
        if (!File.Exists(buildBat))
            return new(false, $"The engine at {engine.InstallDir} has no Build.bat -- it may be a partial install.");

        if (!HasVisualCppToolchain())
            return new(false,
                "No Visual Studio C++ toolchain was found. Building needs Visual Studio with the " +
                "\"Game development with C++\" workload. Someone else on the team can build this " +
                "commit instead -- you only need to sync.");

        return new(true, "Ready to build.");
    }

    private static bool HasVisualCppToolchain()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
        {
            var vs = Path.Combine(root, "Microsoft Visual Studio");
            if (!Directory.Exists(vs)) continue;
            try
            {
                foreach (var edition in Directory.EnumerateDirectories(vs))
                {
                    var msvc = Path.Combine(edition, "VC", "Tools", "MSVC");
                    if (Directory.Exists(msvc) && Directory.EnumerateDirectories(msvc).Any()) return true;

                    foreach (var sub in Directory.EnumerateDirectories(edition))
                    {
                        var nested = Path.Combine(sub, "VC", "Tools", "MSVC");
                        if (Directory.Exists(nested) && Directory.EnumerateDirectories(nested).Any()) return true;
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return false;
    }

    /// <summary>
    /// Compiles and publishes the binaries. PDBs are produced by the compile as a
    /// matter of course and stay on this machine -- they are never published, so a
    /// local build is also how a programmer gets symbols they can actually debug with.
    /// </summary>
    public Task<LocalBuildResult> BuildAndPublishAsync(
        IProgress<string> log,
        CancellationToken ct = default)
        // Both paths are essential, not decorative: the script runs from an extracted
        // cache folder, so it can infer neither the project nor the share on its own.
        => RunScriptAsync(
            ["-NoSync", "-Publish", "-ProjectRoot", config.ProjectRoot, "-PublishRoot", config.PublishRoot],
            log, ct);

    private async Task<LocalBuildResult> RunScriptAsync(
        string[] scriptArgs,
        IProgress<string> log,
        CancellationToken ct = default)
    {
        var shell = PowerShellLocator.Find()
            ?? throw new InvalidOperationException("PowerShell 7 (pwsh) was not found on this machine.");

        // The app has already synced, so the script must not touch the workspace.
        var psi = new ProcessStartInfo
        {
            FileName = shell,
            WorkingDirectory = config.ProjectRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var arg in new[]
                     { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", ScriptPath }
                 .Concat(scriptArgs))
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };
        var all = new StringBuilder();
        string? resultLine = null;

        void Handle(string? line)
        {
            if (line is null) return;
            all.AppendLine(line);
            if (line.StartsWith(ResultPrefix, StringComparison.Ordinal))
            {
                resultLine = line[ResultPrefix.Length..];
                return; // machine-readable, not worth showing
            }
            log.Report(line);
        }

        proc.OutputDataReceived += (_, e) => Handle(e.Data);
        proc.ErrorDataReceived += (_, e) => Handle(e.Data);

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (proc.ExitCode != 0 || resultLine is null)
            return new(false, null, null, all.ToString());

        try
        {
            using var doc = JsonDocument.Parse(resultLine);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            return new(
                status == "success",
                root.TryGetProperty("zipName", out var z) ? z.GetString() : null,
                root.TryGetProperty("commitId", out var c) ? c.GetString() : null,
                all.ToString());
        }
        catch (JsonException)
        {
            return new(false, null, null, all.ToString());
        }
    }
}
