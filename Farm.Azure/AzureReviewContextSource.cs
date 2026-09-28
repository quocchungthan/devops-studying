using Farm.Core.Chickens;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using System.Globalization;
using System.Text.RegularExpressions;
using AzureWorkItemComment = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.Comment;
using AzureWorkItemRelation = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItemRelation;

namespace Farm.Azure;

public sealed partial class AzureDevOpsClient : IReviewContextSource
{
    internal delegate Task<IReadOnlyList<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem?>> ReviewWorkItemBatchLoader(
        string project,
        IReadOnlyList<int> ids,
        bool omitUnavailable,
        CancellationToken cancellationToken);

    internal delegate Task<IReadOnlyList<AzureWorkItemComment>> WorkItemCommentLoader(
        string project,
        int id,
        int top,
        CancellationToken cancellationToken);

    internal const int MaxWorkItemBatchSize = 200;
    internal const int MaxCommentsPerWorkItem = 20;
    internal const int MaxContextFieldLength = 4000;

    // Keyed by the relation on the source item; the label describes the target relative to that source.
    private static readonly IReadOnlyDictionary<string, string> FollowedRelationLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["System.LinkTypes.Hierarchy-Forward"] = "child of",
            ["System.LinkTypes.Hierarchy-Reverse"] = "parent of",
            ["System.LinkTypes.Related"] = "related to",
            ["System.LinkTypes.Dependency-Forward"] = "successor of",
            ["System.LinkTypes.Dependency-Reverse"] = "predecessor of"
        };

    private readonly Uri organizationUrl;
    private readonly int reviewContextMaxDepth;
    private readonly int reviewContextMaxWorkItems;
    private readonly ReviewWorkItemBatchLoader? reviewWorkItemBatchLoader;
    private readonly WorkItemCommentLoader? workItemCommentLoader;

    internal AzureDevOpsClient(
        AzureDevOpsSettings settings,
        ReviewWorkItemBatchLoader reviewWorkItemBatchLoader,
        WorkItemCommentLoader workItemCommentLoader)
        : this(settings)
    {
        this.reviewWorkItemBatchLoader = reviewWorkItemBatchLoader
            ?? throw new ArgumentNullException(nameof(reviewWorkItemBatchLoader));
        this.workItemCommentLoader = workItemCommentLoader
            ?? throw new ArgumentNullException(nameof(workItemCommentLoader));
    }

    public async Task<string> GetCurrentUserIdAsync(CancellationToken cancellationToken = default) =>
        (await GetCurrentUserAsync(cancellationToken)).Id;

    public async Task<ReviewCandidateDiscoveryResult> GetCandidatesAsync(CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(cancellationToken);
        gitClient ??= connection.GetClient<GitHttpClient>();
        var pullRequests = await gitClient.GetPullRequestsByProjectAsync(
            project,
            new GitPullRequestSearchCriteria
            {
                Status = PullRequestStatus.Active,
                CreatorId = Guid.Parse(currentUser.Id)
            },
            cancellationToken: cancellationToken);

        var candidates = new List<ReviewCandidate>(pullRequests.Count);
        var skippedCandidates = new List<ReviewCandidateSkip>();
        foreach (var pullRequest in pullRequests)
        {
            if (pullRequest.Repository is null)
            {
                skippedCandidates.Add(new(pullRequest.PullRequestId, "repository_metadata_missing"));
                continue;
            }

            try
            {
                var threads = await gitClient.GetThreadsAsync(
                    project,
                    pullRequest.Repository.Id.ToString(),
                    pullRequest.PullRequestId,
                    cancellationToken: cancellationToken);
                var workItemRefs = await gitClient.GetPullRequestWorkItemRefsAsync(
                    project,
                    pullRequest.Repository.Id,
                    pullRequest.PullRequestId,
                    cancellationToken: cancellationToken);
                if (TryMapCandidate(pullRequest, threads, workItemRefs, organizationUrl, project, out var candidate, out var reason) &&
                    candidate is not null)
                {
                    candidates.Add(candidate);
                }
                else
                {
                    skippedCandidates.Add(new(pullRequest.PullRequestId, reason));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                skippedCandidates.Add(new(pullRequest.PullRequestId, "candidate_context_failed"));
            }
        }

        return new ReviewCandidateDiscoveryResult(candidates, skippedCandidates);
    }

    public async Task<ReviewContext> GetContextAsync(
        ReviewCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var workItems = new List<WorkItemContext>();
        var skipped = new List<WorkItemContextSkip>();
        var relationPaths = new Dictionary<int, string>();
        var visited = new HashSet<int>();
        var level = new List<int>();
        foreach (var id in candidate.LinkedWorkItemIds)
        {
            if (visited.Add(id))
            {
                level.Add(id);
                relationPaths[id] = $"linked to PR #{candidate.PullRequestId}";
            }
        }

        // Breadth-first; PR-linked items (depth 0) are always fetched, related items fill the remaining cap.
        for (var depth = 0; level.Count > 0; depth++)
        {
            var next = new List<int>();
            foreach (var workItem in await LoadReviewWorkItemsAsync(level, depth, skipped, cancellationToken))
            {
                var id = workItem.Id!.Value;
                var comments = await LoadReviewCommentsAsync(id, depth, skipped, cancellationToken);
                workItems.Add(MapWorkItem(workItem, comments, depth, relationPaths[id]));
                if (depth >= reviewContextMaxDepth)
                {
                    continue;
                }

                foreach (var (relatedId, label) in GetFollowedRelations(workItem))
                {
                    if (visited.Count >= reviewContextMaxWorkItems)
                    {
                        break;
                    }

                    if (visited.Add(relatedId))
                    {
                        next.Add(relatedId);
                        relationPaths[relatedId] = $"{relationPaths[id]} > {label} #{id}";
                    }
                }
            }

            level = next;
        }

        return new ReviewContext(candidate, workItems, skipped);
    }

    private async Task<IReadOnlyList<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem>> LoadReviewWorkItemsAsync(
        IReadOnlyList<int> ids,
        int depth,
        List<WorkItemContextSkip> skipped,
        CancellationToken cancellationToken)
    {
        var result = new List<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem>(ids.Count);
        foreach (var chunk in ids.Chunk(MaxWorkItemBatchSize))
        {
            if (depth == 0)
            {
                // Direct PR links keep fail-fast behavior.
                var direct = await LoadReviewWorkItemBatchAsync(chunk, omitUnavailable: false, cancellationToken);
                result.AddRange(direct.OfType<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem>()
                    .Where(workItem => workItem.Id is not null));
                continue;
            }

            IReadOnlyList<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem?> batch;
            try
            {
                batch = await LoadReviewWorkItemBatchAsync(chunk, omitUnavailable: true, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                batch = await LoadReviewWorkItemsIndividuallyAsync(chunk, cancellationToken);
            }

            var loaded = batch.OfType<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem>()
                .Where(workItem => workItem.Id is not null && chunk.Contains(workItem.Id.Value))
                .DistinctBy(workItem => workItem.Id)
                .ToArray();
            var loadedIds = loaded.Select(workItem => workItem.Id!.Value).ToHashSet();
            skipped.AddRange(chunk.Where(id => !loadedIds.Contains(id))
                .Select(id => new WorkItemContextSkip(id, depth, "work_item_unavailable")));
            result.AddRange(loaded);
        }

        return result;
    }

    private async Task<IReadOnlyList<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem?>> LoadReviewWorkItemsIndividuallyAsync(
        IReadOnlyList<int> ids,
        CancellationToken cancellationToken)
    {
        var result = new List<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem?>(ids.Count);
        foreach (var id in ids)
        {
            try
            {
                result.AddRange(await LoadReviewWorkItemBatchAsync([id], omitUnavailable: true, cancellationToken));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
            }
        }

        return result;
    }

    private async Task<IReadOnlyList<Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem?>> LoadReviewWorkItemBatchAsync(
        IReadOnlyList<int> ids,
        bool omitUnavailable,
        CancellationToken cancellationToken)
    {
        if (reviewWorkItemBatchLoader is not null)
        {
            return await reviewWorkItemBatchLoader(project, ids, omitUnavailable, cancellationToken);
        }

        client ??= connection.GetClient<Microsoft.TeamFoundation.WorkItemTracking.WebApi.WorkItemTrackingHttpClient>();
        return await client.GetWorkItemsAsync(
            project,
            ids,
            expand: WorkItemExpand.Relations,
            errorPolicy: omitUnavailable ? WorkItemErrorPolicy.Omit : WorkItemErrorPolicy.Fail,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<AzureWorkItemComment>> LoadReviewCommentsAsync(
        int id,
        int depth,
        List<WorkItemContextSkip> skipped,
        CancellationToken cancellationToken)
    {
        if (depth == 0)
        {
            return await LoadWorkItemCommentsAsync(id, cancellationToken);
        }

        try
        {
            return await LoadWorkItemCommentsAsync(id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            skipped.Add(new WorkItemContextSkip(id, depth, "comments_unavailable"));
            return [];
        }
    }

    private async Task<IReadOnlyList<AzureWorkItemComment>> LoadWorkItemCommentsAsync(
        int id,
        CancellationToken cancellationToken)
    {
        if (workItemCommentLoader is not null)
        {
            return await workItemCommentLoader(project, id, MaxCommentsPerWorkItem, cancellationToken);
        }

        client ??= connection.GetClient<Microsoft.TeamFoundation.WorkItemTracking.WebApi.WorkItemTrackingHttpClient>();
        var comments = await client.GetCommentsAsync(
            project,
            id,
            top: MaxCommentsPerWorkItem,
            order: CommentSortOrder.Desc,
            cancellationToken: cancellationToken);
        return comments.Comments?.ToArray() ?? [];
    }

    internal static IEnumerable<(int Id, string Label)> GetFollowedRelations(
        Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem workItem)
    {
        foreach (var relation in workItem.Relations ?? [])
        {
            if (relation.Rel is null || !FollowedRelationLabels.TryGetValue(relation.Rel, out var label))
            {
                continue;
            }

            var match = WorkItemUrlId().Match(relation.Url ?? string.Empty);
            if (match.Success &&
                int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) &&
                id > 0)
            {
                yield return (id, label);
            }
        }
    }

    internal static ReviewCandidate MapCandidate(
        GitPullRequest pullRequest,
        IReadOnlyList<GitPullRequestCommentThread> threads,
        IReadOnlyList<Microsoft.VisualStudio.Services.WebApi.ResourceRef> workItemRefs,
        Uri organizationUrl,
        string project)
    {
        ArgumentNullException.ThrowIfNull(pullRequest);
        ArgumentNullException.ThrowIfNull(pullRequest.Repository);

        var repositoryUri = ResolveRepositoryUrl(
            pullRequest.Repository,
            organizationUrl,
            project,
            pullRequest.PullRequestId,
            "target");

        var sourceRepository = pullRequest.ForkSource?.Repository ?? pullRequest.Repository;
        var sourceRepositoryUri = TryResolveRepositoryUrl(sourceRepository, organizationUrl, project);

        if (pullRequest.ForkSource is not null && sourceRepositoryUri is null)
        {
            throw new InvalidOperationException($"Pull request {pullRequest.PullRequestId} has insufficient source repository metadata.");
        }

        var headSha = RequireFullCommitSha(
            pullRequest.LastMergeSourceCommit?.CommitId, pullRequest.PullRequestId, "source head");
        var targetSha = RequireFullCommitSha(
            pullRequest.LastMergeTargetCommit?.CommitId, pullRequest.PullRequestId, "target");

        return new ReviewCandidate(
            organizationUrl.AbsoluteUri.TrimEnd('/'),
            project,
            pullRequest.Repository.Id.ToString(),
            pullRequest.Repository.Name,
            repositoryUri,
            pullRequest.PullRequestId,
            pullRequest.Title,
            pullRequest.SourceRefName,
            pullRequest.TargetRefName,
            headSha,
            pullRequest.CreatedBy.Id,
            threads.Where(thread => thread.Comments is { Count: > 0 })
                .Select(thread => MapThread(thread, targetSha, headSha)).ToArray(),
            workItemRefs.Select(reference => int.Parse(reference.Id)).ToArray(),
            targetSha,
            sourceRepository.Id.ToString(),
            sourceRepository.Name,
            sourceRepositoryUri);
    }

    internal static bool TryMapCandidate(
        GitPullRequest pullRequest,
        IReadOnlyList<GitPullRequestCommentThread> threads,
        IReadOnlyList<Microsoft.VisualStudio.Services.WebApi.ResourceRef> workItemRefs,
        Uri organizationUrl,
        string project,
        out ReviewCandidate? candidate) =>
        TryMapCandidate(pullRequest, threads, workItemRefs, organizationUrl, project, out candidate, out _);

    internal static bool TryMapCandidate(
        GitPullRequest pullRequest,
        IReadOnlyList<GitPullRequestCommentThread> threads,
        IReadOnlyList<Microsoft.VisualStudio.Services.WebApi.ResourceRef> workItemRefs,
        Uri organizationUrl,
        string project,
        out ReviewCandidate? candidate,
        out string reason)
    {
        if (pullRequest.Repository is null ||
            TryResolveRepositoryUrl(pullRequest.Repository, organizationUrl, project) is null)
        {
            candidate = null;
            reason = "repository_metadata_missing";
            return false;
        }

        if (pullRequest.ForkSource is not null &&
            (pullRequest.ForkSource.Repository is null ||
             TryResolveRepositoryUrl(pullRequest.ForkSource.Repository, organizationUrl, project) is null))
        {
            candidate = null;
            reason = "repository_metadata_missing";
            return false;
        }

        try
        {
            candidate = MapCandidate(pullRequest, threads, workItemRefs, organizationUrl, project);
            reason = string.Empty;
            return true;
        }
        catch (Exception)
        {
            candidate = null;
            reason = "candidate_mapping_failed";
            return false;
        }
    }

    private static Uri ResolveRepositoryUrl(
        GitRepository repository,
        Uri organizationUrl,
        string project,
        int pullRequestId,
        string repositoryRole)
    {
        return TryResolveRepositoryUrl(repository, organizationUrl, project)
            ?? throw new InvalidOperationException(
                $"Pull request {pullRequestId} has insufficient {repositoryRole} repository metadata.");
    }

    private static Uri? TryResolveRepositoryUrl(
        GitRepository? repository,
        Uri organizationUrl,
        string project)
    {
        if (repository is null)
        {
            return null;
        }

        foreach (var value in new[] { repository.RemoteUrl, repository.WebUrl })
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
            {
                return absoluteUri;
            }
        }

        var repositoryPath = !string.IsNullOrWhiteSpace(repository.Name)
            ? repository.Name
            : repository.Id == Guid.Empty ? null : repository.Id.ToString();
        if (string.IsNullOrWhiteSpace(repositoryPath))
        {
            return null;
        }

        var basePath = organizationUrl.AbsoluteUri.TrimEnd('/');
        return Uri.TryCreate(
            $"{basePath}/{Uri.EscapeDataString(project)}/_git/{Uri.EscapeDataString(repositoryPath)}",
            UriKind.Absolute,
            out var derivedUri)
            ? derivedUri
            : null;
    }

    private static string RequireFullCommitSha(string? value, int pullRequestId, string name)
    {
        if (value is null || !FullCommitSha().IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Pull request {pullRequestId} {name} SHA must be a full 40-character hexadecimal commit ID.");
        }

        return value;
    }


    public static FeedbackThread MapThread(GitPullRequestCommentThread thread, string? leftCommitId, string? rightCommitId)
    {
        var context = thread.ThreadContext;
        var pullRequestContext = thread.PullRequestThreadContext;
        var anchor = context is null
            ? null
            : new FeedbackAnchor(
                context.FilePath,
                context.RightFileStart?.Line,
                context.RightFileEnd?.Line,
                rightCommitId,
                context.LeftFileStart?.Line,
                context.LeftFileStart?.Offset,
                context.LeftFileEnd?.Line,
                context.LeftFileEnd?.Offset,
                context.RightFileStart?.Line,
                context.RightFileStart?.Offset,
                context.RightFileEnd?.Line,
                context.RightFileEnd?.Offset,
                pullRequestContext?.IterationContext?.FirstComparingIteration,
                pullRequestContext?.IterationContext?.SecondComparingIteration,
                pullRequestContext?.ChangeTrackingId,
                leftCommitId,
                rightCommitId);
        var comments = (thread.Comments ?? [])
            .Select(comment => new FeedbackComment(
                comment.Id,
                comment.Author?.Id ?? string.Empty,
                comment.Author?.DisplayName ?? "Unknown",
                comment.Content ?? string.Empty,
                new DateTimeOffset(comment.PublishedDate.ToUniversalTime()),
                comment.IsDeleted == true))
            .ToArray();
        return new FeedbackThread(thread.Id, AzurePullRequestMapper.IsThreadResolved(thread.Status.ToString()), anchor, comments);
    }

    private static WorkItemContext MapWorkItem(
        Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem workItem,
        IEnumerable<AzureWorkItemComment> comments,
        int depth,
        string relationPath)
    {
        var relations = workItem.Relations ?? [];
        return new WorkItemContext(
            workItem.Id ?? 0,
            GetField(workItem, "System.Title"),
            GetField(workItem, "System.State"),
            ToContextText(GetField(workItem, "System.Description")),
            comments.OrderBy(comment => comment.CreatedDate).Select(comment => new Farm.Core.Chickens.WorkItemComment(
                comment.Id,
                comment.CreatedBy?.DisplayName,
                ToContextText(comment.Text) ?? string.Empty,
                new DateTimeOffset(comment.CreatedDate.ToUniversalTime()),
                comment.CreatedBy?.Id,
                comment.Version,
                comment.ModifiedDate == default ? null : new DateTimeOffset(comment.ModifiedDate.ToUniversalTime()))).ToArray(),
            relations.Select(relation => new Farm.Core.Chickens.WorkItemRelation(
                relation.Rel,
                new Uri(relation.Url),
                GetAttribute(relation, "name"))).ToArray(),
            relations.Where(relation => string.Equals(relation.Rel, "AttachedFile", StringComparison.OrdinalIgnoreCase))
                .Select(relation => new WorkItemAttachment(
                    GetAttribute(relation, "name") ?? "attachment",
                    new Uri(relation.Url),
                    GetAttribute(relation, "comment")))
                .ToArray(),
            GetField(workItem, "System.WorkItemType"),
            ToContextText(GetField(workItem, "Microsoft.VSTS.TCM.ReproSteps")),
            ToContextText(GetField(workItem, "Microsoft.VSTS.Common.AcceptanceCriteria")),
            depth,
            relationPath,
            GetDateField(workItem, "System.ChangedDate"));
    }

    private static DateTimeOffset? GetDateField(
        Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem workItem,
        string name) =>
        workItem.Fields.TryGetValue(name, out var value)
            ? value switch
            {
                DateTime date => new DateTimeOffset(date.ToUniversalTime()),
                DateTimeOffset offset => offset.ToUniversalTime(),
                string text when DateTimeOffset.TryParse(
                    text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) => parsed.ToUniversalTime(),
                _ => null
            }
            : null;

    internal static string? ToContextText(string? html)
    {
        var text = AzureWorkItemMapper.HtmlToText(html);
        return text is null || text.Length <= MaxContextFieldLength
            ? text
            : $"{text[..MaxContextFieldLength]} [truncated]";
    }

    private static string? GetField(Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem workItem, string name) =>
        workItem.Fields.TryGetValue(name, out var value) ? value?.ToString() : null;

    private static string? GetAttribute(AzureWorkItemRelation relation, string name) =>
        relation.Attributes is not null && relation.Attributes.TryGetValue(name, out var value) ? value?.ToString() : null;

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex FullCommitSha();

    [GeneratedRegex(@"/workItems/(\d+)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WorkItemUrlId();
}