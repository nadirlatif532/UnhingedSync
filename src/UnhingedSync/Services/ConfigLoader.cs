using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using UnhingedSync.Models;

namespace UnhingedSync.Services;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
}

public static class ConfigLoader
{
    private const string LocalConfigRelative = @"UnhingedSync\config.local.json";

    public static string LocalConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        LocalConfigRelative);

    /// <summary>
    /// Finds the project without prompting. Returns null when the executable lives
    /// outside the project tree and nothing has been configured yet -- the caller
    /// then asks the user once and calls <see cref="PersistProjectRoot"/>.
    /// </summary>
    public static string? TryResolveProjectRoot()
    {
        var fromEnv = Environment.GetEnvironmentVariable("UNHINGEDSYNC_PROJECT_ROOT");
        if (IsProjectRoot(fromEnv)) return fromEnv;

        if (ReadLocalOverrides()?.ProjectRoot is { } saved && IsProjectRoot(saved)) return saved;

        // Walk up from the executable, which covers running from inside the project.
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            if (IsProjectRoot(dir)) return dir;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }

    /// <summary>
    /// Config file names this tool will accept. The current name comes first; older
    /// ones stay so that a project synced before a rename still opens, and so that a
    /// mixed fleet mid-rollout does not hard-fail.
    /// </summary>
    private static readonly string[] ConfigFileNames = ["unhingedsync.json", "lahoresync.json"];

    /// <summary>Path to the shared config inside a project, or null if there isn't one.</summary>
    public static string? FindSharedConfig(string projectRoot)
    {
        foreach (var name in ConfigFileNames)
        {
            var candidate = Path.Combine(projectRoot, "Tools", name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// A folder is openable if it is an Unreal project. The config is NOT required --
    /// it is generated on first open, which is what lets this tool work on any project
    /// rather than only one that has been prepared for it.
    /// </summary>
    public static bool IsProjectRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return false;
        try
        {
            return Directory.EnumerateFiles(path, "*.uproject").Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What a rejected folder was actually missing, for a message worth reading.</summary>
    public static string DescribeWhyNotProjectRoot(string path)
    {
        if (!Directory.Exists(path)) return "That folder does not exist.";

        return Directory.EnumerateFiles(path, "*.uproject").Any()
            ? "Unknown reason — it does contain a .uproject."
            : "It contains no .uproject file, so it isn't the root of an Unreal project. " +
              "Pick the folder that has the .uproject in it, not a folder above or below it.";
    }

    /// <summary>Remembers the chosen project, preserving any other local settings.</summary>
    public static void PersistProjectRoot(string projectRoot) =>
        SaveLocalSetting("projectRoot", projectRoot);

    /// <summary>
    /// Remembers where this machine keeps the shared binaries. Per machine on purpose:
    /// the tool is portable and every teammate's disks are laid out differently, so a
    /// shared default would be wrong for most of them.
    /// </summary>
    public static void PersistPublishRoot(string publishRoot) =>
        SaveLocalSetting("publishRoot", publishRoot);

    /// <summary>
    /// This machine's role, as chosen during Syncthing setup: artist, programmer or
    /// buildhost. Empty when setup has not run. Used to decide who may hand out
    /// introducer trust.
    /// </summary>
    public static string GetRole() =>
        ReadLocalJson()?["role"]?.GetValue<string>() ?? "";

    /// <summary>Which engine install this machine uses for a given project, if overridden.</summary>
    public static string GetEngineOverride(string projectRoot)
    {
        var engines = ReadLocalJson()?["engines"]?.AsObject();
        if (engines is null) return "";

        foreach (var (key, value) in engines)
        {
            if (key.Equals(projectRoot, StringComparison.OrdinalIgnoreCase))
                return value?.GetValue<string>() ?? "";
        }
        return "";
    }

    /// <summary>Pass an empty engineDir to go back to resolving from the .uproject.</summary>
    public static void SetEngineOverride(string projectRoot, string engineDir)
    {
        var root = ReadLocalJson() ?? new JsonObject();
        var engines = root["engines"]?.AsObject() ?? new JsonObject();

        // Rebuild rather than mutate: keys are paths and comparisons must be
        // case-insensitive, which JsonObject's indexer is not.
        var rebuilt = new JsonObject();
        foreach (var (key, value) in engines)
        {
            if (!key.Equals(projectRoot, StringComparison.OrdinalIgnoreCase))
                rebuilt[key] = value?.GetValue<string>();
        }
        if (!string.IsNullOrWhiteSpace(engineDir)) rebuilt[projectRoot] = engineDir;

        root["engines"] = rebuilt;
        WriteLocalJson(root);
    }

    /// <summary>
    /// Projects this machine knows about, in the order they were added. Seeded from the
    /// older single 'projectRoot' setting so an existing install keeps its project.
    /// </summary>
    public static List<string> GetKnownProjects()
    {
        var root = ReadLocalJson();
        var known = new List<string>();

        if (root?["projects"] is JsonArray array)
        {
            foreach (var entry in array)
            {
                var path = entry?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(path)) known.Add(path);
            }
        }

        if (root?["projectRoot"]?.GetValue<string>() is { Length: > 0 } legacy &&
            !known.Contains(legacy, StringComparer.OrdinalIgnoreCase))
        {
            known.Insert(0, legacy);
        }

        return known.Where(IsProjectRoot).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static void AddKnownProject(string projectRoot)
    {
        var known = GetKnownProjects();
        if (known.Contains(projectRoot, StringComparer.OrdinalIgnoreCase)) return;
        known.Add(projectRoot);
        SaveKnownProjects(known);
    }

    public static void RemoveKnownProject(string projectRoot)
    {
        var known = GetKnownProjects()
            .Where(p => !p.Equals(projectRoot, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SaveKnownProjects(known);
    }

    private static void SaveKnownProjects(IEnumerable<string> projects)
    {
        var root = ReadLocalJson() ?? new JsonObject();
        var array = new JsonArray();
        foreach (var project in projects) array.Add(project);
        root["projects"] = array;

        // 'projectRoot' stays in step with the first project so an older build of the
        // tool, or the PowerShell scripts, still resolve something sensible.
        var first = projects.FirstOrDefault();
        if (first is not null) root["projectRoot"] = first;

        WriteLocalJson(root);
    }

    private static JsonObject? ReadLocalJson()
    {
        if (!File.Exists(LocalConfigPath)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(LocalConfigPath))?.AsObject();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    private static void WriteLocalJson(JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LocalConfigPath)!);
        File.WriteAllText(LocalConfigPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void SaveLocalSetting(string key, string value)
    {
        var path = LocalConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject(); // A corrupt override file should not block setup.
        }

        root[key] = value;
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static AppConfig Load() =>
        Load(TryResolveProjectRoot() ?? throw new InvalidOperationException(
            "Could not locate the Lahore project. Set UNHINGEDSYNC_PROJECT_ROOT, put " +
            "UnhingedSync.exe inside the project tree, or run it once interactively to pick the folder."));

    public static AppConfig Load(string projectRoot)
    {
        // Generate one if this project has never been opened. Doing it here means every
        // entry point -- window, tabs, headless modes -- gets the same treatment.
        var sharedPath = FindSharedConfig(projectRoot) ?? ConfigBootstrap.Ensure(projectRoot).ConfigPath;

        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(sharedPath), Json.Options)
            ?? throw new InvalidOperationException($"Could not parse {sharedPath}");

        config.ProjectRoot = projectRoot;
        config.EngineDirOverride = GetEngineOverride(projectRoot);

        // A relative publish root is resolved against the project, so the share can
        // live inside the project tree and still mean the same thing on every machine.
        var publishRoot = ResolvePublishRoot(config);
        config.PublishRoot = Path.IsPathRooted(publishRoot)
            ? publishRoot
            : Path.GetFullPath(Path.Combine(projectRoot, publishRoot));

        return config;
    }

    private static string ResolvePublishRoot(AppConfig config)
    {
        var fromEnv = Environment.GetEnvironmentVariable("UNHINGEDSYNC_PUBLISH_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;

        if (ReadLocalOverrides()?.PublishRoot is { Length: > 0 } configured) return configured;

        // The exe is normally distributed inside the replicated share itself, either at
        // its root or one level down (\App). Recognising both means a teammate can run
        // it straight off the share with nothing configured.
        var beside = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        foreach (var candidate in new[] { beside, Path.GetDirectoryName(beside) })
        {
            if (!string.IsNullOrEmpty(candidate) &&
                Directory.Exists(Path.Combine(candidate, "records")))
                return candidate;
        }

        if (!string.IsNullOrWhiteSpace(config.PublishRootDefault)) return config.PublishRootDefault;

        // A working default rather than a question. Where the binaries land is not a
        // decision a new teammate can make well on their first run, and the one genuinely
        // wrong answer -- inside the project, where 'dv clean' deletes ignored files -- is
        // exactly the one they might pick by accident. Visible and obviously named, so it
        // can be found again when pointing Syncthing at it.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UnhingedShare");
    }

    private static LocalOverrides? ReadLocalOverrides()
    {
        if (!File.Exists(LocalConfigPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<LocalOverrides>(File.ReadAllText(LocalConfigPath), Json.Options);
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    private sealed class LocalOverrides
    {
        public string? PublishRoot { get; set; }
        public string? ProjectRoot { get; set; }
        public string? EngineDir { get; set; }
        public bool IsBuildHost { get; set; }
    }
}
