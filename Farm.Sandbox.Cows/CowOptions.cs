using System.Text.Json;

namespace Farm.Sandbox.Cows;

public sealed class CowOptions
{
    public string NotesPath { get; init; } = "/workspace/notes";
    public string RepositoryPath { get; init; } = "/workspace/repository";
    public string ResourcesPath { get; init; } = Path.Combine(AppContext.BaseDirectory, "resources");
    public string StateRootPath { get; init; } = "/workspace/state";
    public string StatePath { get; init; } = "/workspace/state/cows.db";
    public string StatusPath { get; init; } = "/workspace/state/status.json";
    public string LockPath { get; init; } = "/workspace/state/cows.lock";
    public TimeSpan SchedulePeriod { get; init; } = TimeSpan.FromHours(2);
    public bool RunImmediately { get; init; } = true;
    public int MaxPatchesPerRun { get; init; } = 20;
    public int MaxRelatedItems { get; init; } = 50;
    public TimeSpan MaxNoteAge { get; init; } = TimeSpan.FromHours(24);

    public string SkillFilePath => Path.Combine(ResourcesPath, "SKILL.md");
    public string McpConfigPath => Path.Combine(ResourcesPath, "mcp.json");

    public static CowOptions FromEnvironment()
    {
        var stateRootPath = Optional("FARM_COWS_STATE_CONTAINER_PATH") ?? "/workspace/state";
        var options = new CowOptions
        {
            NotesPath = Optional("FARM_COWS_NOTES_PATH") ?? "/workspace/notes",
            RepositoryPath = Optional("FARM_COWS_REPOSITORY_PATH") ?? "/workspace/repository",
            ResourcesPath = Optional("FARM_COWS_RESOURCES_PATH") ?? Path.Combine(AppContext.BaseDirectory, "resources"),
            StateRootPath = stateRootPath,
            StatePath = Optional("FARM_COWS_STATE_PATH") ?? Path.Combine(stateRootPath, "cows.db"),
            StatusPath = Optional("FARM_COWS_STATUS_PATH") ?? Path.Combine(stateRootPath, "status.json"),
            LockPath = Optional("FARM_COWS_LOCK_PATH") ?? Path.Combine(stateRootPath, "cows.lock"),
            SchedulePeriod = TimeSpan.FromSeconds(OptionalInt("FARM_COWS_SCHEDULE_SECONDS", 7200)),
            RunImmediately = OptionalBool("FARM_COWS_RUN_IMMEDIATELY", true),
            MaxPatchesPerRun = OptionalInt("FARM_COWS_MAX_PATCHES_PER_RUN", 20),
            MaxRelatedItems = OptionalInt("FARM_COWS_MAX_RELATED_ITEMS", 50, minimum: 0),
            MaxNoteAge = TimeSpan.FromHours(OptionalInt("FARM_COWS_MAX_NOTE_AGE_HOURS", 24, minimum: 0))
        };
        options.ValidateStatePaths();
        return options;
    }

    public void ValidateStatePaths()
    {
        ValidatePathUnderRoot(StatePath, StateRootPath);
        ValidatePathUnderRoot(StatusPath, StateRootPath);
        ValidatePathUnderRoot(LockPath, StateRootPath);
    }

    public static string ValidatePathUnderRoot(string path, string rootPath)
    {
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(root, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException($"Path '{path}' must remain beneath the configured root '{rootPath}'.");
        }

        return candidate;
    }

    public static IReadOnlyDictionary<string, string> LoadSafeProcessEnvironment()
    {
        var json = Optional("FARM_COWS_COPILOT_EXTRA_ENV_JSON");
        return json is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static string Required(string name) =>
        Optional(name) ?? throw new InvalidOperationException($"Environment variable '{name}' is required.");

    internal static string? Optional(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value ? value : null;

    internal static int OptionalInt(string name, int fallback, int minimum = 1) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= minimum ? value : fallback;

    internal static bool OptionalBool(string name, bool fallback) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}
