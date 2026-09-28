namespace Farm.Core.Exports;

public sealed record WorkspaceExportManifest(
    string WorkspaceName,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<WorkspaceExportResource> Resources);

public sealed record WorkspaceExportResource(
    ExportResourceKind Kind,
    string RelativePath,
    int Count);