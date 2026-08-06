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
            ? "Unknown reason. It does contain a .uproject."
            : "It contains no .uproject file, so it isn't the root of an Unreal project. " +
              "Pick the folder that has the .uproject in it, not a folder above or below it.";
    }

    /// <summary>Remembers the chosen project, preserving any other local settings.</summary>
    public static void PersistProjectRoot(string projectRoot) =>
        SaveLocalSetting("projectRoot", projectRoot);

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

    /// <summary>When the update checker last asked GitHub, so it doesn't ask every launch.</summary>
    public static DateTimeOffset? GetLastUpdateCheckUtc() =>
        ReadLocalJson()?["lastUpdateCheckUtc"]?.GetValue<string>() is { Length: > 0 } s &&
        DateTimeOffset.TryParse(s, out var t) ? t : null;

    public static void SetLastUpdateCheckUtc(DateTimeOffset value) =>
        SaveLocalSetting("lastUpdateCheckUtc", value.ToString("o"));

    /// <summary>
    /// A release the user chose "later" for, so the popup doesn't nag again about the
    /// same version -- it'll ask again once a newer one ships.
    /// </summary>
    public static string GetDismissedUpdateVersion() =>
        ReadLocalJson()?["dismissedUpdateVersion"]?.GetValue<string>() ?? "";

    public static void SetDismissedUpdateVersion(string version) =>
        SaveLocalSetting("dismissedUpdateVersion", version);

    /// <summary>Whether this machine already said "no" to auto-installing PowerShell 7.</summary>
    public static bool GetDeclinedPowerShellInstall() =>
        ReadLocalJson()?["declinedPowerShellInstall"]?.GetValue<bool>() ?? false;

    public static void SetDeclinedPowerShellInstall(bool value)
    {
        var root = ReadLocalJson() ?? new JsonObject();
        root["declinedPowerShellInstall"] = value;
        WriteLocalJson(root);
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
        // Generating a config is not a neutral fallback, so it is recorded rather than done
        // quietly. If a project's Tools folder has not arrived from version control yet, the
        // generated syncthingFolderId will differ from the team's committed one, every
        // Syncthing call will then key on a folder nobody replicates, and the symptom is a
        // permanently empty build list that looks exactly like nobody having published. Worse,
        // the generated file is itself committable, so it can carry that divergence to
        // everybody. Callers surface GeneratedConfigFor and warn.
        var sharedPath = FindSharedConfig(projectRoot);
        if (sharedPath is null)
        {
            sharedPath = ConfigBootstrap.Ensure(projectRoot).ConfigPath;
            lock (GeneratedConfigs) GeneratedConfigs.Add(projectRoot);
        }

        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(sharedPath), Json.Options)
            ?? throw new InvalidOperationException($"Could not parse {sharedPath}");

        config.ProjectRoot = projectRoot;
        config.EngineDirOverride = GetEngineOverride(projectRoot);

        // No publish root to resolve any more. Where builds live is the bucket named in this
        // very file, so there is no per-machine path, no five-step fallback chain, and nothing
        // that can silently disagree with what is actually storing the builds.
        return config;
    }

    private static readonly HashSet<string> GeneratedConfigs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether this session generated the shared config rather than finding a committed one.
    ///
    /// Still worth surfacing after the move to a bucket, for a different reason than before.
    /// A generated config has an empty storage block, so the project has nowhere to read
    /// builds from, and the likeliest cause is that Tools/ has not arrived from version
    /// control yet. Saying so beats an empty list and a shrug.
    /// </summary>
    public static bool WasConfigGeneratedFor(string projectRoot)
    {
        lock (GeneratedConfigs) return GeneratedConfigs.Contains(projectRoot);
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
