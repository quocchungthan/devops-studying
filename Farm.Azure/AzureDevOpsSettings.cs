namespace Farm.Azure;

public sealed class AzureDevOpsSettings
{
    public static IReadOnlyList<string> DefaultTerminalStates { get; } =
        Array.AsReadOnly(["Done", "Closed", "Removed"]);

    public required Uri OrganizationUrl { get; init; }

    public required string Project { get; init; }

    public string? Team { get; init; }

    public required string PersonalAccessToken { get; init; }

    public IReadOnlyList<string> TerminalStates { get; init; } = DefaultTerminalStates;

    public int ReviewContextMaxDepth { get; init; } = 3;

    public int ReviewContextMaxWorkItems { get; init; } = 50;

    public void Validate()
    {
        if (OrganizationUrl is null || !OrganizationUrl.IsAbsoluteUri || OrganizationUrl.Scheme is not "https")
        {
            throw new InvalidOperationException("Azure DevOps organization URL must be an absolute HTTPS URL.");
        }

        if (string.IsNullOrWhiteSpace(Project))
        {
            throw new InvalidOperationException("Azure DevOps project is required.");
        }

        if (Team is not null && string.IsNullOrWhiteSpace(Team))
        {
            throw new InvalidOperationException("Azure DevOps team must not be empty when configured.");
        }

        if (string.IsNullOrWhiteSpace(PersonalAccessToken))
        {
            throw new InvalidOperationException("Azure DevOps personal access token is required.");
        }

        if (TerminalStates.Count == 0 || TerminalStates.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("Azure DevOps terminal states must contain at least one non-empty state.");
        }

        if (ReviewContextMaxDepth < 0)
        {
            throw new InvalidOperationException("Review context work item depth must not be negative.");
        }

        if (ReviewContextMaxWorkItems < 1)
        {
            throw new InvalidOperationException("Review context work item cap must be at least one.");
        }
    }
}