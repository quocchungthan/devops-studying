namespace Farm.Core.Exports;

public sealed record WorkspaceExportSpecification(
    string WorkspaceName,
    string OutputRootPath,
    IReadOnlyList<ExportResourceKind> Resources);

public enum ExportResourceKind
{
    WorkItems,
    Attachments,
    PullRequests,
    PullRequestThreads,
    Identities,
    Groups
}