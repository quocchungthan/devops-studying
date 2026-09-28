namespace Farm.Core.Domain;

public sealed record WorkItem(
    int Id,
    int Revision,
    string? Title,
    string? State,
    string? WorkItemType,
    Identity? AssignedTo,
    Uri? Url,
    IReadOnlyList<Attachment> Attachments,
    string? Reason = null,
    string? AreaPath = null,
    string? IterationPath = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? ChangedAt = null,
    Identity? CreatedBy = null,
    Identity? ChangedBy = null,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<WorkItemRelation>? Relations = null);

public sealed record WorkItemRelation(
    string Type,
    Uri Url,
    string? Name);

public sealed record Attachment(
    string Id,
    string FileName,
    Uri DownloadUrl,
    string? Comment);

public sealed record PullRequest(
    int Id,
    string Title,
    string Status,
    Identity? CreatedBy,
    Uri Url,
    IReadOnlyList<PullRequestThread> Threads);

public sealed record PullRequestThread(
    int Id,
    string Status,
    IReadOnlyList<PullRequestComment> Comments);

public sealed record PullRequestComment(
    int Id,
    string Content,
    DateTimeOffset PublishedAt,
    Identity? Author);

public sealed record Identity(
    string Id,
    string DisplayName,
    string? UniqueName);

public sealed record Group(
    string Id,
    string DisplayName,
    IReadOnlyList<Identity> Members,
    string? LegacyId = null);

public sealed record PullRequestSummary(
    int Id,
    string Title,
    string Status,
    Identity? CreatedBy,
    Uri Url,
    IReadOnlyList<PullRequestReviewer> Reviewers);

public sealed record PullRequestReviewer(
    Identity Identity,
    int Vote,
    bool IsRequired,
    bool IsContainer = false);