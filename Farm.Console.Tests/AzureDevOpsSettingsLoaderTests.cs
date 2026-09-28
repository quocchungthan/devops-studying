using Xunit;

namespace Farm.Console.Tests;

public sealed class AzureDevOpsSettingsLoaderTests
{
    [Fact]
    public void LoadFromEnvironment_rejects_missing_variables()
    {
        var names = new[]
        {
            "FARM_AZURE_DEVOPS_ORGANIZATION_URL",
            "FARM_AZURE_DEVOPS_PROJECT",
            "FARM_AZURE_DEVOPS_PAT"
        };
        var values = names.Select(Environment.GetEnvironmentVariable).ToArray();

        try
        {
            foreach (var name in names)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            var exception = Assert.Throws<InvalidOperationException>(AzureDevOpsSettingsLoader.LoadFromEnvironment);

            Assert.Contains("required", exception.Message);
        }
        finally
        {
            for (var index = 0; index < names.Length; index++)
            {
                Environment.SetEnvironmentVariable(names[index], values[index]);
            }
        }
    }

    [Fact]
    public void LoadFromEnvironment_uses_default_terminal_states_and_optional_team()
    {
        using var environment = new AzureEnvironmentScope();
        environment.SetRequiredValues();
        Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_TEAM", "Platform Team");
        Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_TERMINAL_STATES", null);

        var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();

        Assert.Equal("Platform Team", settings.Team);
        Assert.Equal(["Done", "Closed", "Removed"], settings.TerminalStates);
    }

    [Fact]
    public void LoadFromEnvironment_parses_custom_terminal_states()
    {
        using var environment = new AzureEnvironmentScope();
        environment.SetRequiredValues();
        Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_TERMINAL_STATES", " Complete, Shipped ,Archived ");

        var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();

        Assert.Equal(["Complete", "Shipped", "Archived"], settings.TerminalStates);
    }

    private sealed class AzureEnvironmentScope : IDisposable
    {
        private static readonly string[] Names =
        [
            "FARM_AZURE_DEVOPS_ORGANIZATION_URL",
            "FARM_AZURE_DEVOPS_PROJECT",
            "FARM_AZURE_DEVOPS_PAT",
            "FARM_AZURE_DEVOPS_TEAM",
            "FARM_AZURE_DEVOPS_TERMINAL_STATES"
        ];

        private readonly string?[] values = Names.Select(Environment.GetEnvironmentVariable).ToArray();

        public void SetRequiredValues()
        {
            Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_ORGANIZATION_URL", "https://dev.azure.com/example");
            Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_PROJECT", "Project");
            Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_PAT", "token");
        }

        public void Dispose()
        {
            for (var index = 0; index < Names.Length; index++)
            {
                Environment.SetEnvironmentVariable(Names[index], values[index]);
            }
        }
    }
}