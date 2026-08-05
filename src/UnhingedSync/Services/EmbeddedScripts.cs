using System.IO;
using System.Reflection;

namespace UnhingedSync.Services;

/// <summary>
/// The PowerShell scripts ship inside the executable and are extracted to a per-version
/// cache the first time they are needed.
///
/// This is what makes the app a single portable file: it no longer requires the target
/// project to contain a Tools folder, so it works on any Unreal project. It also makes
/// version skew impossible -- the script that runs is always the one built alongside
/// this exe, never a stale copy left in some project.
/// </summary>
public static class EmbeddedScripts
{
    private const string Prefix = "scripts/";

    private static readonly Lazy<string> CacheDir = new(Extract);

    public static string Version =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public static string BuildScript => Path.Combine(CacheDir.Value, "Invoke-UnhingedBuild.ps1");
    public static string EngineIntegrityScript => Path.Combine(CacheDir.Value, "Test-EngineIntegrity.ps1");
    public static string SetupScript => Path.Combine(CacheDir.Value, "Setup-Syncthing.ps1");
    public static string SetupBat => Path.Combine(CacheDir.Value, "Setup-Syncthing.bat");

    /// <summary>Where the scripts were extracted. Useful in diagnostics.</summary>
    public static string Directory => CacheDir.Value;

    private static string Extract()
    {
        // Keyed by version so an upgraded exe never reuses the previous version's
        // scripts, and two versions can coexist without fighting.
        var target = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UnhingedSync", "scripts", Version);

        System.IO.Directory.CreateDirectory(target);

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(Prefix, StringComparison.Ordinal)) continue;

            var fileName = name[Prefix.Length..];
            var path = Path.Combine(target, fileName);

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;

            // Rewrite every time rather than trusting what is on disk: the cache lives
            // in a user-writable folder, and a truncated or hand-edited script would
            // fail in ways that look like a build problem.
            using var file = File.Create(path);
            stream.CopyTo(file);
        }

        return target;
    }

    /// <summary>
    /// Confirms the exe actually carries its scripts. A packaging mistake would
    /// otherwise only surface when someone pressed Build.
    /// </summary>
    public static IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        foreach (var path in new[] { BuildScript, EngineIntegrityScript, SetupScript, SetupBat })
        {
            if (!File.Exists(path)) missing.Add(Path.GetFileName(path));
        }
        return missing;
    }
}
