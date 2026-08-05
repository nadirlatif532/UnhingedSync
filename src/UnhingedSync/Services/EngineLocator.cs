using System.IO;
using System.Text.Json;
using UnhingedSync.Models;
using Microsoft.Win32;

namespace UnhingedSync.Services;

/// <summary>
/// Resolves the launcher-installed engine for the project and reads its BuildId.
/// Mirrors Invoke-UnhingedBuild.ps1 -- the two must agree or the gate is meaningless.
/// </summary>
public static class EngineLocator
{
    public static EngineInfo Locate(AppConfig config)
    {
        // A machine-local override wins: several installs of the same version, or a
        // source build alongside a launcher build, are both legitimate.
        if (!string.IsNullOrWhiteSpace(config.EngineDirOverride) &&
            File.Exists(Path.Combine(config.EngineDirOverride, "Engine", "Build", "Build.version")))
        {
            return ReadVersion(config.EngineDirOverride);
        }
        return LocateFor(config.ProjectRoot, config.ProjectFile);
    }

    /// <summary>The version the .uproject says this project targets, for comparison.</summary>
    public static string ReadEngineAssociation(string projectRoot, string projectFile)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(projectRoot, projectFile)));
            return doc.RootElement.TryGetProperty("EngineAssociation", out var assoc)
                ? assoc.GetString() ?? ""
                : "";
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// Resolve from raw paths, so a project can be inspected before it has a config
    /// (bootstrapping a new project needs the engine's BuildId to write into it).
    /// </summary>
    public static EngineInfo LocateFor(string projectRoot, string projectFile)
    {
        var uprojectPath = Path.Combine(projectRoot, projectFile);
        using var doc = JsonDocument.Parse(File.ReadAllText(uprojectPath));
        var association = doc.RootElement.TryGetProperty("EngineAssociation", out var assoc)
            ? assoc.GetString() ?? ""
            : "";

        var installDir = FromRegistry(association)
            ?? FromLauncherManifest(association)
            ?? throw new InvalidOperationException(
                $"Could not find an installed Unreal Engine for association '{association}'. " +
                "Install UE from the Epic Games Launcher, or set 'engineDir' in " +
                @"%LOCALAPPDATA%\UnhingedSync\config.local.json.");

        return ReadVersion(installDir);
    }

    /// <summary>
    /// Every engine this machine has, launcher-installed or source-built.
    ///
    /// Install locations differ per machine, which is why the choice is stored locally
    /// rather than in the project's committed config. Which engine VERSION a project
    /// targets is a team decision and already lives in the .uproject.
    /// </summary>
    public static List<EngineInfo> EnumerateInstalled()
    {
        var byDirectory = new Dictionary<string, EngineInfo>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || byDirectory.ContainsKey(dir)) return;
            if (!File.Exists(Path.Combine(dir, "Engine", "Build", "Build.version"))) return;
            try { byDirectory[dir] = ReadVersion(dir); }
            catch (Exception e) when (e is IOException or JsonException or FileNotFoundException) { }
        }

        // Launcher installs, keyed by version string.
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\EpicGames\Unreal Engine");
            foreach (var version in key?.GetSubKeyNames() ?? [])
            {
                using var sub = key!.OpenSubKey(version);
                Consider(sub?.GetValue("InstalledDirectory") as string);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }

        // Source builds register themselves here under a GUID.
        try
        {
            using var builds = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Epic Games\Unreal Engine\Builds");
            foreach (var name in builds?.GetValueNames() ?? [])
            {
                Consider(builds!.GetValue(name) as string);
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException) { }

        // And the launcher's own manifest, which also covers engines the registry missed.
        var manifest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\UnrealEngineLauncher\LauncherInstalled.dat");
        if (File.Exists(manifest))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("InstallationList", out var list))
                {
                    foreach (var entry in list.EnumerateArray())
                    {
                        Consider(entry.TryGetProperty("InstallLocation", out var loc) ? loc.GetString() : null);
                    }
                }
            }
            catch (JsonException) { }
        }

        return byDirectory.Values
            .OrderByDescending(e => e.Version)
            .ThenBy(e => e.InstallDir)
            .ToList();
    }

    private static string? FromRegistry(string association)
    {
        if (string.IsNullOrEmpty(association)) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SOFTWARE\EpicGames\Unreal Engine\{association}");
            var dir = key?.GetValue("InstalledDirectory") as string;
            return Directory.Exists(dir) ? dir : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FromLauncherManifest(string association)
    {
        var manifest = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\UnrealEngineLauncher\LauncherInstalled.dat");
        if (!File.Exists(manifest)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            if (!doc.RootElement.TryGetProperty("InstallationList", out var list)) return null;

            string? fallback = null;
            foreach (var entry in list.EnumerateArray())
            {
                var location = entry.TryGetProperty("InstallLocation", out var loc) ? loc.GetString() : null;
                if (!Directory.Exists(location)) continue;

                var appName = entry.TryGetProperty("AppName", out var an) ? an.GetString() : null;
                if (appName == $"UE_{association}") return location;

                // Launcher entries for bundled plugins carry the engine's install
                // location too, so they serve as a fallback when UE_x.y is absent.
                var appVersion = entry.TryGetProperty("AppVersion", out var av) ? av.GetString() : null;
                if (fallback is null && appVersion?.StartsWith(association, StringComparison.Ordinal) == true)
                    fallback = location;
            }
            return fallback;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static EngineInfo ReadVersion(string installDir)
    {
        var versionPath = Path.Combine(installDir, "Engine", "Build", "Build.version");
        if (!File.Exists(versionPath))
            throw new FileNotFoundException($"Engine version file missing: {versionPath}");

        using var doc = JsonDocument.Parse(File.ReadAllText(versionPath));
        var root = doc.RootElement;

        long Get(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt64(out var l) ? l : 0;

        var changelist = Get("Changelist");
        var compatible = Get("CompatibleChangelist");

        // UBT stamps modules with a BuildId taken from CompatibleChangelist when it
        // is set. That is why 5.8.0 and 5.8.1 binaries interoperate: both resolve
        // to 55116800. Comparing version strings instead would reject them wrongly.
        var buildId = compatible != 0 ? compatible : changelist;

        return new EngineInfo
        {
            InstallDir = installDir,
            BuildId = buildId.ToString(),
            Version = $"{Get("MajorVersion")}.{Get("MinorVersion")}.{Get("PatchVersion")}",
            Changelist = changelist,
            CompatibleChangelist = compatible
        };
    }
}
