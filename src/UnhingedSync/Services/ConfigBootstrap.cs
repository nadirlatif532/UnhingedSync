using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace UnhingedSync.Services;

public sealed record BootstrapResult(string ConfigPath, bool Created, string ProjectName);

/// <summary>
/// Creates the shared config for a project that doesn't have one yet, so the app works
/// on any Unreal project rather than only on one it was built for.
///
/// Everything is derived from the .uproject, and the file is written into the project's
/// Tools folder so it can be committed and shared: the whole team then agrees on the
/// editor target, the retention count and, critically, the Syncthing folder ID.
/// </summary>
public static class ConfigBootstrap
{
    public const string ConfigFileName = "unhingedsync.json";

    public static string? FindUproject(string projectRoot)
    {
        try
        {
            return Directory.EnumerateFiles(projectRoot, "*.uproject").OrderBy(p => p).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// A folder ID every machine on this project derives identically, and that will not
    /// collide with another team's project. Syncthing requires the ID to match exactly
    /// on both sides, so it cannot be random per machine, and a bare project name would
    /// eventually clash between studios sharing a network.
    /// </summary>
    public static string DeriveFolderId(string projectName)
    {
        var slug = new string(projectName.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length > 24) slug = slug[..24].Trim('-');
        if (slug.Length == 0) slug = "project";

        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(projectName.ToLowerInvariant())))[..8]
            .ToLowerInvariant();

        return $"unhinged-{slug}-{hash}";
    }

    /// <summary>
    /// Best guess at the editor target. UBT's convention is "&lt;Project&gt;Editor", but the
    /// project may say otherwise, so prefer a real *Editor.Target.cs if one exists.
    /// </summary>
    private static string DeriveEditorTarget(string projectRoot, string projectName)
    {
        var sourceDir = Path.Combine(projectRoot, "Source");
        if (Directory.Exists(sourceDir))
        {
            try
            {
                var target = Directory.EnumerateFiles(sourceDir, "*Editor.Target.cs")
                    .Select(p => Path.GetFileName(p).Replace(".Target.cs", "", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(n => n.Length)
                    .FirstOrDefault();
                if (!string.IsNullOrEmpty(target)) return target;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return projectName + "Editor";
    }

    /// <summary>Writes a config if the project has none. Never overwrites an existing one.</summary>
    public static BootstrapResult Ensure(string projectRoot)
    {
        if (ConfigLoader.FindSharedConfig(projectRoot) is { } existing)
        {
            var name = Path.GetFileNameWithoutExtension(FindUproject(projectRoot) ?? "project");
            return new BootstrapResult(existing, Created: false, name);
        }

        var uproject = FindUproject(projectRoot)
            ?? throw new InvalidOperationException(
                $"No .uproject file in {projectRoot}, so this is not an Unreal project folder.");

        var projectFile = Path.GetFileName(uproject);
        var projectName = Path.GetFileNameWithoutExtension(uproject);

        // The engine is only needed for the BuildId we record as the team's expected
        // value. If it cannot be resolved, leave enforcement off rather than block setup
        // with a value we would be guessing at.
        string expectedBuildId = "";
        try
        {
            expectedBuildId = EngineLocator.LocateFor(projectRoot, projectFile).BuildId;
        }
        catch (Exception e) when (e is InvalidOperationException or FileNotFoundException) { }

        var config = new JsonObject
        {
            ["$comment"] = new JsonArray(
                $"Unhinged Sync configuration for {projectName}. COMMIT THIS FILE.",
                "It is how the team agrees on the editor target, the retention count and the",
                "Syncthing folder ID -- that ID must be byte-identical on every machine or",
                "Syncthing will not consider you to be sharing the same folder.",
                "",
                "Machine-specific paths do NOT belong here. The publish root is asked for on",
                "first run and stored per machine in %LOCALAPPDATA%\\UnhingedSync.",
                "",
                "PDBs are never published. Every build produces them locally, which is wanted,",
                "but they are ~80x the size of the binaries and would replicate to everyone.",
                "To debug, build locally and use your own.",
                "",
                "Generated automatically; edit freely."),
            ["schemaVersion"] = 1,
            ["projectName"] = projectName,
            ["projectFile"] = projectFile,
            ["editorTarget"] = DeriveEditorTarget(projectRoot, projectName),
            ["platform"] = "Win64",
            ["configuration"] = "Development",
            ["branch"] = "main",
            ["publishRootDefault"] = "",
            ["retainBuilds"] = 10,
            ["syncthingFolderId"] = DeriveFolderId(projectName),
            ["toolchain"] = new JsonObject
            {
                ["compilerVersion"] = "Latest",
                ["useXge"] = false
            },
            ["engine"] = new JsonObject
            {
                ["expectedBuildId"] = expectedBuildId,
                ["enforceBuildIdMatch"] = expectedBuildId.Length > 0
            }
        };

        var toolsDir = Path.Combine(projectRoot, "Tools");
        Directory.CreateDirectory(toolsDir);
        var path = Path.Combine(toolsDir, ConfigFileName);

        File.WriteAllText(path,
            config.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return new BootstrapResult(path, Created: true, projectName);
    }
}
