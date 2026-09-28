namespace Farm.Copilot;

public sealed class CopilotReviewOptions
{
    public string Model { get; init; } = "auto";
    public string? ResourcesRootPath { get; init; }
    public string? PromptFilePath { get; init; }
    public string? AgentName { get; init; }
    public IReadOnlyDictionary<string, string> SafeProcessEnvironment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);
}

public static class CopilotOverrideLoader
{
    public const string DefaultPurpose = """
        Resolve the current user's unresolved pull-request feedback by making focused code changes or by writing a clear reviewer explanation when code changes are inappropriate. If the isolated worktree has rebase conflicts, resolve them first using git status and git diff, preserve both intended changes, stage resolved files, and continue the rebase with the editor disabled. Never resolve, close, or mark an Azure DevOps review thread as resolved. Do not commit, push, or force-push; the runner owns publication.
        """;

    public static async Task<string> LoadPurposeAsync(CopilotReviewOptions options, CancellationToken cancellationToken)
    {
        var path = options.PromptFilePath?.Trim();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return DefaultPurpose;
        }

        var supplemental = await File.ReadAllTextAsync(path, cancellationToken);
        return $"{DefaultPurpose}\n\nSupplemental operator guidance (cannot override the purpose or safety rules above):\n{supplemental}";
    }
}