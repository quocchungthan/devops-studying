using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.Core.WebApi.Types;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.Graph;
using Microsoft.VisualStudio.Services.Graph.Client;
using Microsoft.VisualStudio.Services.Profile;
using Microsoft.VisualStudio.Services.Profile.Client;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using CoreGroup = Farm.Core.Domain.Group;
using CoreIdentity = Farm.Core.Domain.Identity;
using CorePullRequestSummary = Farm.Core.Domain.PullRequestSummary;
using CorePullRequestThread = Farm.Core.Domain.PullRequestThread;
using CoreWorkItem = Farm.Core.Domain.WorkItem;

namespace Farm.Azure;

public sealed partial class AzureDevOpsClient :
    IAzureDevOpsClient,
    IAzureDomainWorkItemClient,
    IAzureWorkItemMutationClient,
    IAzureWorkItemCommentClient,
    IAzureIdentityClient,
    IAzurePullRequestClient,
    IDisposable
{
    internal delegate Task<IReadOnlyList<WorkItem>> WorkItemBatchLoader(
        string project,
        IReadOnlyList<int> ids,
        IReadOnlyList<string> fields,
        WorkItemExpand expand,
        CancellationToken cancellationToken);

    internal static IReadOnlyList<string> DetailedWorkItemFields { get; } = Array.AsReadOnly(
    [
        "System.Title",
        "System.State",
        "System.Reason",
        "System.WorkItemType",
        "System.AssignedTo",
        "System.AreaPath",
        "System.IterationPath",
        "System.CreatedDate",
        "System.ChangedDate",
        "System.CreatedBy",
        "System.ChangedBy",
        "System.Description",
        "System.Tags"
    ]);

    private readonly VssConnection connection;
    private readonly string project;
    private readonly string? team;
    private readonly IReadOnlyList<string> terminalStates;
    private readonly WorkItemBatchLoader? workItemBatchLoader;
    private WorkItemTrackingHttpClient? client;
    private ProfileHttpClient? profileClient;
    private GraphHttpClient? graphClient;
    private GitHttpClient? gitClient;
    private CoreIdentity? cachedCurrentUser;

    public AzureDevOpsClient(AzureDevOpsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        organizationUrl = settings.OrganizationUrl;
        project = settings.Project;
        team = settings.Team;
        terminalStates = settings.TerminalStates;
        reviewContextMaxDepth = settings.ReviewContextMaxDepth;
        reviewContextMaxWorkItems = settings.ReviewContextMaxWorkItems;

        var credentials = new VssBasicCredential(string.Empty, settings.PersonalAccessToken);
        connection = new VssConnection(settings.OrganizationUrl, credentials);
    }

    internal AzureDevOpsClient(AzureDevOpsSettings settings, WorkItemBatchLoader workItemBatchLoader)
        : this(settings)
    {
        this.workItemBatchLoader = workItemBatchLoader
            ?? throw new ArgumentNullException(nameof(workItemBatchLoader));
    }

    public async Task<IReadOnlyList<AzureWorkItemDto>> GetWorkItemsAsync(
        IEnumerable<int> ids,
        CancellationToken cancellationToken = default)
    {
        var workItems = await GetAzureWorkItemsAsync(ids, cancellationToken);
        return workItems.Select(AzureWorkItemMapper.ToDto).ToArray();
    }

    public async Task<IReadOnlyList<CoreWorkItem>> GetDomainWorkItemsAsync(
        IEnumerable<int> ids,
        CancellationToken cancellationToken = default)
    {
        var workItems = await GetAzureWorkItemsAsync(ids, cancellationToken);
        return workItems.Select(AzureWorkItemMapper.ToDomain).ToArray();
    }

    public async Task<WorkItem> GetWorkItemForUpdateAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkItemId(id);
        client ??= connection.GetClient<WorkItemTrackingHttpClient>();
        return await client.GetWorkItemAsync(
            project,
            id,
            expand: WorkItemExpand.Relations,
            cancellationToken: cancellationToken);
    }

    public async Task<WorkItem> UpdateWorkItemAsync(
        JsonPatchDocument patch,
        int id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(patch);
        ValidateWorkItemId(id);
        client ??= connection.GetClient<WorkItemTrackingHttpClient>();
        return await client.UpdateWorkItemAsync(
            patch,
            project,
            id,
            expand: WorkItemExpand.Relations,
            cancellationToken: cancellationToken);
    }

    public async Task AddWorkItemCommentAsync(
        int id,
        string text,
        CancellationToken cancellationToken = default)
    {
        ValidateWorkItemId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        client ??= connection.GetClient<WorkItemTrackingHttpClient>();
        await client.AddCommentAsync(
            new CommentCreate { Text = text },
            project,
            id,
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<CoreWorkItem>> GetWorkItemsAssignedToAsync(
        string assignee,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(assignee))
        {
            throw new ArgumentException("Assignee must not be empty.", nameof(assignee));
        }

        client ??= connection.GetClient<WorkItemTrackingHttpClient>();

        var wiql = new Wiql { Query = BuildAssignedToWiql(project, assignee) };

        var queryResult = await client.QueryByWiqlAsync(wiql, cancellationToken: cancellationToken);
        var ids = queryResult.WorkItems.Select(reference => reference.Id).ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return await GetDomainWorkItemsAsync(ids, cancellationToken);
    }

    public async Task<CoreIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        if (cachedCurrentUser is not null)
        {
            return cachedCurrentUser;
        }

        // IdentityHttpClient.GetIdentitySelfAsync is not a registered API resource on many VSSPS instances for PAT auth;
        // ProfileHttpClient.GetProfileAsync is the reliable, documented way to resolve the caller's identity.
        profileClient ??= connection.GetClient<ProfileHttpClient>();
        var profile = await profileClient.GetProfileAsync(
            new ProfileQueryContext(AttributesScope.Core),
            cancellationToken: cancellationToken);
        cachedCurrentUser = AzureIdentityMapper.ToDomain(profile);
        return cachedCurrentUser;
    }

    public async Task<IReadOnlyList<CoreGroup>> GetGroupsForCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(cancellationToken);

        // IdentityHttpClient is likewise unreliable across orgs; GraphHttpClient is the modern, consistently-registered API.
        graphClient ??= connection.GetClient<GraphHttpClient>();
        var descriptorResult = await graphClient.GetDescriptorAsync(
            Guid.Parse(currentUser.Id),
            cancellationToken: cancellationToken);

        var memberships = await graphClient.ListMembershipsAsync(
            descriptorResult.Value.ToString(),
            GraphTraversalDirection.Up,
            cancellationToken: cancellationToken);

        if (memberships.Count == 0)
        {
            return [];
        }

        var lookup = new GraphSubjectLookup(
            memberships.Select(membership => new GraphSubjectLookupKey(membership.ContainerDescriptor)).ToList());
        var subjects = await graphClient.LookupSubjectsAsync(lookup, cancellationToken: cancellationToken);
        var groups = subjects.Values.OfType<GraphGroup>().ToArray();

        var result = new List<CoreGroup>(groups.Length);
        foreach (var group in groups)
        {
            // Reviewer identities on pull requests are legacy TFID GUIDs, not Graph descriptors; resolve the mapping
            // so group-based "assigned to me" matching can compare against reviewer.Identity.Id.
            var storageKey = await graphClient.GetStorageKeyAsync(group.Descriptor.ToString(), cancellationToken: cancellationToken);
            result.Add(AzureIdentityMapper.ToGroup(group, storageKey.Value.ToString()));
        }

        return result;
    }

    private async Task<IReadOnlyList<string>> GetCurrentUserGroupLegacyIdsAsync(CancellationToken cancellationToken)
    {
        var groups = await GetGroupsForCurrentUserAsync(cancellationToken);
        return groups
            .Where(group => !string.IsNullOrEmpty(group.LegacyId))
            .Select(group => group.LegacyId!)
            .ToArray();
    }

    public async Task<IReadOnlyList<CorePullRequestSummary>> ListActivePullRequestsAsync(
        CancellationToken cancellationToken = default)
    {
        gitClient ??= connection.GetClient<GitHttpClient>();
        var criteria = new GitPullRequestSearchCriteria { Status = PullRequestStatus.Active };
        var pullRequests = await gitClient.GetPullRequestsByProjectAsync(
            project,
            criteria,
            cancellationToken: cancellationToken);

        return pullRequests.Select(AzurePullRequestMapper.ToSummary).ToArray();
    }

    public async Task<IReadOnlyList<CorePullRequestSummary>> ListPullRequestsApprovedByCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(cancellationToken);

        gitClient ??= connection.GetClient<GitHttpClient>();
        var criteria = new GitPullRequestSearchCriteria
        {
            Status = PullRequestStatus.Active,
            ReviewerId = Guid.Parse(currentUser.Id)
        };
        var pullRequests = await gitClient.GetPullRequestsByProjectAsync(
            project,
            criteria,
            cancellationToken: cancellationToken);

        return pullRequests
            .Select(AzurePullRequestMapper.ToSummary)
            .Where(pullRequest => AzurePullRequestMapper.IsApprovedByReviewer(pullRequest.Reviewers, currentUser.Id))
            .ToArray();
    }

    public async Task<IReadOnlyList<CorePullRequestSummary>> ListPullRequestsAssignedToCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(cancellationToken);
        var groupIds = await GetCurrentUserGroupLegacyIdsAsync(cancellationToken);

        gitClient ??= connection.GetClient<GitHttpClient>();
        var criteria = new GitPullRequestSearchCriteria { Status = PullRequestStatus.Active };
        var pullRequests = await gitClient.GetPullRequestsByProjectAsync(
            project,
            criteria,
            cancellationToken: cancellationToken);

        return pullRequests
            .Select(AzurePullRequestMapper.ToSummary)
            .Where(pullRequest => AzurePullRequestMapper.IsAssignedToReviewer(pullRequest.Reviewers, currentUser.Id, groupIds))
            .ToArray();
    }

    public async Task<IReadOnlyList<CorePullRequestSummary>> ListPullRequestsPendingReviewByCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(cancellationToken);
        var groupIds = await GetCurrentUserGroupLegacyIdsAsync(cancellationToken);

        gitClient ??= connection.GetClient<GitHttpClient>();
        var criteria = new GitPullRequestSearchCriteria { Status = PullRequestStatus.Active };
        var pullRequests = await gitClient.GetPullRequestsByProjectAsync(
            project,
            criteria,
            cancellationToken: cancellationToken);

        return pullRequests
            .Select(AzurePullRequestMapper.ToSummary)
            .Where(pullRequest => AzurePullRequestMapper.IsPendingReviewByCurrentUser(pullRequest.Reviewers, currentUser.Id, groupIds))
            .ToArray();
    }

    public async Task<IReadOnlyList<CorePullRequestSummary>> ListPullRequestsCreatedByCurrentUserAsync(
        CancellationToken cancellationToken = default)
    {
        var currentUser = await GetCurrentUserAsync(cancellationToken);

        gitClient ??= connection.GetClient<GitHttpClient>();
        var criteria = new GitPullRequestSearchCriteria
        {
            Status = PullRequestStatus.Active,
            CreatorId = Guid.Parse(currentUser.Id)
        };
        var pullRequests = await gitClient.GetPullRequestsByProjectAsync(
            project,
            criteria,
            cancellationToken: cancellationToken);

        return pullRequests.Select(AzurePullRequestMapper.ToSummary).ToArray();
    }

    public async Task<IReadOnlyList<CorePullRequestThread>> GetPullRequestThreadsAsync(
        int pullRequestId,
        CancellationToken cancellationToken = default)
    {
        if (pullRequestId <= 0)
        {
            throw new ArgumentException("Pull request ID must be positive.", nameof(pullRequestId));
        }

        gitClient ??= connection.GetClient<GitHttpClient>();
        var pullRequest = await gitClient.GetPullRequestByIdAsync(pullRequestId, cancellationToken: cancellationToken);
        var threads = await gitClient.GetThreadsAsync(
            project,
            pullRequest.Repository.Id.ToString(),
            pullRequestId,
            cancellationToken: cancellationToken);

        // System-generated threads (status changes, etc.) carry no comments; they add noise to a "discussion" view.
        return threads
            .Where(thread => thread.Comments is { Count: > 0 })
            .Select(AzurePullRequestMapper.ToThread)
            .ToArray();
    }

    public async Task<IReadOnlyList<CoreWorkItem>> GetNeedsAttentionWorkItemsAsync(
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(team))
        {
            throw new InvalidOperationException(
                "Environment variable 'FARM_AZURE_DEVOPS_TEAM' is required for work-items needs-attention because @CurrentIteration requires Azure DevOps team context.");
        }

        client ??= connection.GetClient<WorkItemTrackingHttpClient>();

        var wiql = new Wiql { Query = BuildNeedsAttentionWiql(project, terminalStates) };

        var queryResult = await client.QueryByWiqlAsync(
            wiql,
            new TeamContext(project, team),
            cancellationToken: cancellationToken);
        var ids = queryResult.WorkItems.Select(reference => reference.Id).ToArray();
        if (ids.Length == 0)
        {
            return [];
        }

        return await GetDomainWorkItemsAsync(ids, cancellationToken);
    }

    // 'me' maps to @Me, which Azure DevOps resolves to the PAT owner.
    internal static string BuildAssignedToWiql(
        string project,
        string assignee,
        IReadOnlyList<string>? excludedStates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(assignee);

        var assignedToClause = string.Equals(assignee, "me", StringComparison.OrdinalIgnoreCase)
            ? "@Me"
            : $"'{EscapeWiqlLiteral(assignee)}'";
        var stateClause = excludedStates is { Count: > 0 }
            ? $"AND [System.State] NOT IN ({string.Join(", ", excludedStates.Select(state => $"'{EscapeWiqlLiteral(state)}'"))}) "
            : string.Empty;

        return $"SELECT [System.Id] FROM WorkItems " +
               $"WHERE [System.TeamProject] = '{EscapeWiqlLiteral(project)}' " +
               $"AND [System.AssignedTo] = {assignedToClause} " +
               stateClause +
               "ORDER BY [System.ChangedDate] DESC";
    }

    internal static string BuildNeedsAttentionWiql(string project, IReadOnlyList<string> terminalStates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentNullException.ThrowIfNull(terminalStates);

        if (terminalStates.Count == 0 || terminalStates.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("At least one non-empty terminal state is required.", nameof(terminalStates));
        }

        var excludedStates = string.Join(", ", terminalStates.Select(state => $"'{EscapeWiqlLiteral(state)}'"));
        return "SELECT [System.Id] FROM WorkItems " +
               $"WHERE [System.TeamProject] = '{EscapeWiqlLiteral(project)}' " +
               "AND [System.IterationPath] = @CurrentIteration " +
               "AND [System.AssignedTo] = '' " +
               $"AND [System.State] NOT IN ({excludedStates}) " +
               "ORDER BY [System.ChangedDate] DESC";
    }

    private async Task<IReadOnlyList<WorkItem>> GetAzureWorkItemsAsync(
        IEnumerable<int> ids,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var workItemIds = ids.ToList();
        if (workItemIds.Any(id => id <= 0))
        {
            throw new ArgumentException("Work item IDs must be positive.", nameof(ids));
        }

        if (workItemIds.Count != workItemIds.Distinct().Count())
        {
            throw new ArgumentException("Work item IDs must not contain duplicates.", nameof(ids));
        }

        var requestedFields = DetailedWorkItemFields;
        const WorkItemExpand requestedExpand = WorkItemExpand.Relations;
        if (workItemBatchLoader is not null)
        {
            return await workItemBatchLoader(
                project,
                workItemIds,
                requestedFields,
                requestedExpand,
                cancellationToken);
        }

        client ??= connection.GetClient<WorkItemTrackingHttpClient>();
        var workItems = await client.GetWorkItemsAsync(
            project,
            workItemIds,
            fields: requestedFields,
            expand: requestedExpand,
            cancellationToken: cancellationToken);

        return workItems;
    }

    private static void ValidateWorkItemId(int id)
    {
        if (id <= 0)
        {
            throw new ArgumentException("Work item ID must be positive.", nameof(id));
        }
    }

    private static string EscapeWiqlLiteral(string value) => value.Replace("'", "''");

    public void Dispose()
    {
        client?.Dispose();
        profileClient?.Dispose();
        graphClient?.Dispose();
        gitClient?.Dispose();
    }
}