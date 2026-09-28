using System.Globalization;
using System.Net;
using Farm.Core.Chickens;
using Farm.Core.Cows;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using AzureWorkItem = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem;
using AzureWorkItemRelation = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItemRelation;
using WorkItemComment = Farm.Core.Chickens.WorkItemComment;

namespace Farm.Azure;

public sealed partial class AzureDevOpsClient : ICloudWorkItemSource
{
    private const string PullRequestArtifactPrefix = "vstfs:///Git/PullRequestId/";

    // TF401232: work item does not exist or no permission; TF401180: pull request not found.
    private static readonly string[] NotFoundOrForbiddenErrorCodes = ["TF401232", "TF401180"];

    internal delegate Task<GitPullRequest> PullRequestLoader(int pullRequestId, CancellationToken cancellationToken);

    private readonly PullRequestLoader? pullRequestLoader;

    internal AzureDevOpsClient(
        AzureDevOpsSettings settings,
        ReviewWorkItemBatchLoader reviewWorkItemBatchLoader,
        WorkItemCommentLoader workItemCommentLoader,
        PullRequestLoader pullRequestLoader)
        : this(settings, reviewWorkItemBatchLoader, workItemCommentLoader)
    {
        this.pullRequestLoader = pullRequestLoader ?? throw new ArgumentNullException(nameof(pullRequestLoader));
    }

    public async Task<IReadOnlyList<int>> GetAssignedToMeWorkItemIdsAsync(CancellationToken cancellationToken = default)
    {
        client ??= connection.GetClient<Microsoft.TeamFoundation.WorkItemTracking.WebApi.WorkItemTrackingHttpClient>();
        var queryResult = await client.QueryByWiqlAsync(
            new Wiql { Query = BuildAssignedToWiql(project, "me", terminalStates) },
            cancellationToken: cancellationToken);
        return queryResult.WorkItems.Select(reference => reference.Id).Distinct().ToArray();
    }

    public async Task<IReadOnlyList<CloudWorkItemSnapshot>> GetWorkItemsAsync(
        IReadOnlyCollection<int> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Any(id => id <= 0))
        {
            throw new ArgumentException("Work item IDs must be positive.", nameof(ids));
        }

        var result = new List<CloudWorkItemSnapshot>(ids.Count);
        foreach (var chunk in ids.Distinct().Chunk(MaxWorkItemBatchSize))
        {
            var batch = await LoadSyncBatchAsync(chunk, cancellationToken);
            result.AddRange(batch
                .OfType<AzureWorkItem>()
                .Where(workItem => workItem.Id is not null && chunk.Contains(workItem.Id.Value))
                .DistinctBy(workItem => workItem.Id)
                .Select(ToSnapshot));
        }

        return result;
    }

    // A forbidden item rejects the whole batch even with the Omit policy, so retry one by one. Only a 404/403 marks
    // an item missing; any other error fails the cycle instead of reporting items as removed.
    private async Task<IReadOnlyList<AzureWorkItem?>> LoadSyncBatchAsync(int[] ids, CancellationToken cancellationToken)
    {
        try
        {
            return await LoadReviewWorkItemBatchAsync(ids, omitUnavailable: true, cancellationToken);
        }
        catch (Exception batchException) when (IsNotFoundOrForbidden(batchException))
        {
            if (ids.Length == 1)
            {
                return [];
            }

            var result = new List<AzureWorkItem?>(ids.Length);
            foreach (var id in ids)
            {
                try
                {
                    result.AddRange(await LoadReviewWorkItemBatchAsync([id], omitUnavailable: true, cancellationToken));
                }
                catch (Exception exception) when (IsNotFoundOrForbidden(exception))
                {
                }
            }

            return result;
        }
    }

    internal static bool IsNotFoundOrForbidden(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case OperationCanceledException or VssUnauthorizedException:
                    return false;
                case VssServiceResponseException { HttpStatusCode: HttpStatusCode.NotFound or HttpStatusCode.Forbidden }:
                case HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Forbidden }:
                case VssResourceNotFoundException:
                    return true;
                case VssServiceException when NotFoundOrForbiddenErrorCodes.Any(code =>
                    current.Message.StartsWith(code, StringComparison.OrdinalIgnoreCase)):
                    return true;
            }
        }

        return false;
    }

    public async Task<CloudPullRequestState?> GetPullRequestAsync(
        int pullRequestId,
        CancellationToken cancellationToken = default)
    {
        if (pullRequestId <= 0)
        {
            throw new ArgumentException("Pull request ID must be positive.", nameof(pullRequestId));
        }

        try
        {
            var pullRequest = await LoadPullRequestAsync(pullRequestId, cancellationToken);
            return new CloudPullRequestState(
                pullRequest.PullRequestId,
                pullRequest.Status.ToString(),
                pullRequest.Title,
                pullRequest.LastMergeSourceCommit?.CommitId,
                pullRequest.ClosedDate == default ? null : new DateTimeOffset(pullRequest.ClosedDate.ToUniversalTime()),
                Uri.TryCreate(pullRequest.Url, UriKind.Absolute, out var url) ? url : null);
        }
        catch (Exception exception) when (IsNotFoundOrForbidden(exception))
        {
            return CloudPullRequestState.Unavailable(pullRequestId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }
    }

    private async Task<GitPullRequest> LoadPullRequestAsync(int pullRequestId, CancellationToken cancellationToken)
    {
        if (pullRequestLoader is not null)
        {
            return await pullRequestLoader(pullRequestId, cancellationToken);
        }

        gitClient ??= connection.GetClient<GitHttpClient>();
        return await gitClient.GetPullRequestByIdAsync(pullRequestId, cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<WorkItemComment>> GetCommentsAsync(
        int workItemId,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkItemId(workItemId);
        var comments = await LoadWorkItemCommentsAsync(workItemId, cancellationToken);
        return comments
            .Where(comment => comment.IsDeleted != true)
            .OrderBy(comment => comment.CreatedDate)
            .Select(comment => new WorkItemComment(
                comment.Id,
                comment.CreatedBy?.DisplayName,
                ToContextText(comment.Text) ?? string.Empty,
                new DateTimeOffset(comment.CreatedDate.ToUniversalTime()),
                comment.CreatedBy?.Id,
                comment.Version,
                comment.ModifiedDate == default ? null : new DateTimeOffset(comment.ModifiedDate.ToUniversalTime())))
            .ToArray();
    }

    internal static CloudWorkItemSnapshot ToSnapshot(AzureWorkItem workItem) =>
        new(workItem.Rev ?? 0, MapWorkItem(workItem, [], 0, "tracked"), GetLinkedPullRequestIds(workItem.Relations))
        {
            RelatedWorkItemIds = GetFollowedRelations(workItem)
                .Select(relation => relation.Id)
                .Where(id => id != workItem.Id)
                .Distinct()
                .ToArray()
        };

    internal static IReadOnlyList<int> GetLinkedPullRequestIds(IEnumerable<AzureWorkItemRelation>? relations)
    {
        var ids = new SortedSet<int>();
        foreach (var relation in relations ?? [])
        {
            var url = relation.Url;
            if (!string.Equals(relation.Rel, "ArtifactLink", StringComparison.OrdinalIgnoreCase) ||
                url is null || !url.StartsWith(PullRequestArtifactPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var lastSegment = Uri.UnescapeDataString(url[PullRequestArtifactPrefix.Length..]).Split('/').Last();
            if (int.TryParse(lastSegment, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            {
                ids.Add(id);
            }
        }

        return ids.ToArray();
    }
}
