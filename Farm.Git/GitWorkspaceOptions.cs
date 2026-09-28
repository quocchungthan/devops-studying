using Farm.Core.Chickens;

namespace Farm.Git;

public sealed class GitWorkspaceOptions
{
    public required string RepositoryPath { get; init; }
    public required string CachePath { get; init; }
    public required string WorktreesPath { get; init; }
    public required string BaseBranch { get; init; }
    public string RemoteName { get; init; } = "origin";
    public string? HttpExtraHeader { get; init; }
    public string? UserName { get; init; }
    public string? UserEmail { get; init; }
    public bool EnablePush { get; init; }
    public bool AllowFileRepositoryUrls { get; init; }
    public IReadOnlyList<GitValidationCommand> ValidationCommands { get; init; } = [];
    public IReadOnlyDictionary<string, string> SafeProcessEnvironment { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public ISensitiveDataRedactor Redactor { get; init; } = SensitiveDataRedactor.Empty;
    public ISensitiveContentScanner ContentScanner { get; init; } = SensitiveContentScanner.Empty;
}

public sealed record GitValidationCommand(string FileName, IReadOnlyList<string> Arguments);