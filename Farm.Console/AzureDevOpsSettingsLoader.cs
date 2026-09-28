using Farm.Azure;

public static class AzureDevOpsSettingsLoader
{
    public static AzureDevOpsSettings LoadFromEnvironment()
    {
        var organizationUrl = GetRequired("FARM_AZURE_DEVOPS_ORGANIZATION_URL");
        var project = GetRequired("FARM_AZURE_DEVOPS_PROJECT");
        var token = GetRequired("FARM_AZURE_DEVOPS_PAT");
        var team = GetOptional("FARM_AZURE_DEVOPS_TEAM");
        var terminalStates = GetOptional("FARM_AZURE_DEVOPS_TERMINAL_STATES")?
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        if (!Uri.TryCreate(organizationUrl, UriKind.Absolute, out var parsedOrganizationUrl))
        {
            throw new InvalidOperationException("Environment variable 'FARM_AZURE_DEVOPS_ORGANIZATION_URL' must be a valid absolute URL.");
        }

        var settings = new AzureDevOpsSettings
        {
            OrganizationUrl = parsedOrganizationUrl,
            Project = project,
            Team = team,
            PersonalAccessToken = token,
            TerminalStates = terminalStates ?? AzureDevOpsSettings.DefaultTerminalStates
        };

        settings.Validate();
        return settings;
    }

    private static string GetRequired(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Environment variable '{name}' is required.");

    private static string? GetOptional(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : null;
}