using System.Text.Json;
using Farm.Git;

namespace Farm.Sandbox.Chickens;

public sealed class ChickenOptions
{
    public required string ArtifactsPath { get; init; }
    public string StateRootPath { get; init; } = "/workspace/state";
    public string StatePath { get; init; } = "/workspace/state/chickens.db";
    public string StatusPath { get; init; } = "status.json";
    public string LockPath { get; init; } = "/workspace/state/chickens.lock";
    public TimeSpan SchedulePeriod { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan QuietPeriod { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromHours(1);
    public bool RunImmediately { get; init; } = true;
    public bool EnablePush { get; init; }
    public int WorkItemMaxDepth { get; init; } = 3;
    public int WorkItemMaxCount { get; init; } = 50;

    public static ChickenOptions FromEnvironment()
    {
        var stateRootPath = Environment.GetEnvironmentVariable("FARM_CHICKENS_STATE_CONTAINER_PATH")?.Trim()
            is { Length: > 0 } configuredRoot
            ? configuredRoot
            : "/workspace/state";
        var statusPath = Environment.GetEnvironmentVariable("FARM_CHICKENS_STATUS_PATH")?.Trim()
            is { Length: > 0 } configuredStatus
            ? configuredStatus
            : Path.Combine(stateRootPath, "status.json");
        var statePath = Environment.GetEnvironmentVariable("FARM_CHICKENS_STATE_PATH")?.Trim()
            is { Length: > 0 } configuredState
            ? configuredState
            : Path.Combine(stateRootPath, "chickens.db");
        var lockPath = Environment.GetEnvironmentVariable("FARM_CHICKENS_LOCK_PATH")?.Trim()
            is { Length: > 0 } configuredLock
            ? configuredLock
            : Path.Combine(stateRootPath, "chickens.lock");
        var options = new ChickenOptions
        {
            ArtifactsPath = Required("FARM_CHICKENS_ARTIFACTS_PATH"),
            StateRootPath = stateRootPath,
            StatePath = statePath,
            StatusPath = statusPath,
            LockPath = lockPath,
            SchedulePeriod = TimeSpan.FromSeconds(OptionalInt("FARM_CHICKENS_SCHEDULE_SECONDS", 3600)),
            QuietPeriod = TimeSpan.FromSeconds(OptionalInt("FARM_CHICKENS_QUIET_PERIOD_SECONDS", 3600)),
            LeaseDuration = TimeSpan.FromSeconds(OptionalInt("FARM_CHICKENS_LEASE_SECONDS", 3600)),
            RunImmediately = OptionalBool("FARM_CHICKENS_RUN_IMMEDIATELY", true),
            EnablePush = OptionalBool("FARM_CHICKENS_ENABLE_PUSH", false),
            WorkItemMaxDepth = OptionalInt("FARM_CHICKENS_WORK_ITEM_MAX_DEPTH", 3, minimum: 0),
            WorkItemMaxCount = OptionalInt("FARM_CHICKENS_WORK_ITEM_MAX_COUNT", 50)
        };
        options.ValidateStatePaths();
        return options;
    }

    public void ValidateStatePaths()
    {
        ValidatePathUnderStateRoot(StatePath);
        ValidatePathUnderStateRoot(StatusPath);
        ValidatePathUnderStateRoot(LockPath);
    }

    public void ValidatePathUnderStateRoot(string path)
    {
        var root = Path.GetFullPath(StateRootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = Path.IsPathFullyQualified(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(root, path));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException($"Path '{path}' must remain beneath the configured state root '{StateRootPath}'.");
        }
    }

    public static IReadOnlyList<GitValidationCommand> LoadValidationCommands()
    {
        var json = Environment.GetEnvironmentVariable("FARM_CHICKENS_VALIDATION_COMMANDS_JSON");
        return string.IsNullOrWhiteSpace(json)
            ? []
            : JsonSerializer.Deserialize<List<GitValidationCommand>>(json) ?? [];
    }

    public static IReadOnlyDictionary<string, string> LoadSafeProcessEnvironment()
    {
        var json = Environment.GetEnvironmentVariable("FARM_CHICKENS_SAFE_PROCESS_ENV_JSON");
        return string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    internal static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)?.Trim() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable '{name}' is required.");

    private static int OptionalInt(string name, int fallback, int minimum = 1) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value >= minimum ? value : fallback;

    private static bool OptionalBool(string name, bool fallback) =>
        bool.TryParse(Environment.GetEnvironmentVariable(name), out var value) ? value : fallback;
}