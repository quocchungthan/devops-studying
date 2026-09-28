using Xunit;

namespace Farm.Sandbox.Cows.Tests;

public sealed class CowOptionsTests
{
    private static readonly string[] Names =
    [
        "FARM_COWS_SCHEDULE_SECONDS", "FARM_COWS_RUN_IMMEDIATELY", "FARM_COWS_NOTES_PATH", "FARM_COWS_STATE_CONTAINER_PATH",
        "FARM_COWS_STATE_PATH", "FARM_COWS_STATUS_PATH", "FARM_COWS_LOCK_PATH", "FARM_COWS_MAX_PATCHES_PER_RUN",
        "FARM_COWS_MAX_RELATED_ITEMS", "FARM_COWS_REPOSITORY_PATH", "FARM_COWS_MAX_NOTE_AGE_HOURS"
    ];

    [Fact]
    public void Defaults_run_every_two_hours_immediately_under_workspace_paths()
    {
        WithEnvironment(new Dictionary<string, string?>(), () =>
        {
            var options = CowOptions.FromEnvironment();

            Assert.Equal(TimeSpan.FromSeconds(7200), options.SchedulePeriod);
            Assert.True(options.RunImmediately);
            Assert.Equal("/workspace/notes", options.NotesPath);
            Assert.Equal("/workspace/repository", options.RepositoryPath);
            Assert.Equal(Path.Combine("/workspace/state", "cows.db"), options.StatePath);
            Assert.Equal(Path.Combine("/workspace/state", "status.json"), options.StatusPath);
            Assert.Equal(20, options.MaxPatchesPerRun);
            Assert.Equal(50, options.MaxRelatedItems);
            Assert.Equal(TimeSpan.FromHours(24), options.MaxNoteAge);
            Assert.EndsWith(Path.Combine("resources", "SKILL.md"), options.SkillFilePath);
            Assert.EndsWith(Path.Combine("resources", "mcp.json"), options.McpConfigPath);
        });
    }

    [Fact]
    public void Environment_overrides_schedule_and_invalid_values_fall_back()
    {
        WithEnvironment(new Dictionary<string, string?>
        {
            ["FARM_COWS_SCHEDULE_SECONDS"] = "60",
            ["FARM_COWS_RUN_IMMEDIATELY"] = "false",
            ["FARM_COWS_MAX_PATCHES_PER_RUN"] = "0",
            ["FARM_COWS_MAX_RELATED_ITEMS"] = "0",
            ["FARM_COWS_MAX_NOTE_AGE_HOURS"] = "0"
        }, () =>
        {
            var options = CowOptions.FromEnvironment();

            Assert.Equal(TimeSpan.FromSeconds(60), options.SchedulePeriod);
            Assert.False(options.RunImmediately);
            Assert.Equal(20, options.MaxPatchesPerRun);
            Assert.Equal(0, options.MaxRelatedItems);
            Assert.Equal(TimeSpan.Zero, options.MaxNoteAge);
        });

        WithEnvironment(new Dictionary<string, string?>
        {
            ["FARM_COWS_SCHEDULE_SECONDS"] = "-5",
            ["FARM_COWS_MAX_RELATED_ITEMS"] = "-1",
            ["FARM_COWS_MAX_NOTE_AGE_HOURS"] = "-1"
        }, () =>
        {
            var options = CowOptions.FromEnvironment();

            Assert.Equal(TimeSpan.FromHours(2), options.SchedulePeriod);
            Assert.Equal(50, options.MaxRelatedItems);
            Assert.Equal(TimeSpan.FromHours(24), options.MaxNoteAge);
        });
    }

    [Fact]
    public void State_paths_outside_state_root_are_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cow-state-{Guid.NewGuid():N}");
        WithEnvironment(new Dictionary<string, string?>
        {
            ["FARM_COWS_STATE_CONTAINER_PATH"] = root,
            ["FARM_COWS_STATUS_PATH"] = Path.Combine(root, "..", "escape.json")
        }, () => Assert.Throws<InvalidOperationException>(CowOptions.FromEnvironment));
    }

    private static void WithEnvironment(IReadOnlyDictionary<string, string?> values, Action action)
    {
        var previous = Names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in Names)
            {
                Environment.SetEnvironmentVariable(name, values.GetValueOrDefault(name));
            }

            action();
        }
        finally
        {
            foreach (var variable in previous)
            {
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }
        }
    }
}
