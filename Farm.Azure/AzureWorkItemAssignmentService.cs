using Farm.Core.Contracts;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using CoreIdentity = Farm.Core.Domain.Identity;
using CoreWorkItem = Farm.Core.Domain.WorkItem;
using AzureWorkItem = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem;

namespace Farm.Azure;

public interface IAzureWorkItemMutationClient
{
    Task<CoreIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default);

    Task<AzureWorkItem> GetWorkItemForUpdateAsync(
        int id,
        CancellationToken cancellationToken = default);

    Task<AzureWorkItem> UpdateWorkItemAsync(
        JsonPatchDocument patch,
        int id,
        CancellationToken cancellationToken = default);
}

public sealed class AzureWorkItemAssignmentService(IAzureWorkItemMutationClient client) : IWorkItemAssignmentService
{
    private const string AssignedToPath = "/fields/System.AssignedTo";

    public async Task<CoreWorkItem> AssignAsync(
        int id,
        string assignee,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(assignee);

        var assignmentValue = assignee.Trim();
        if (string.Equals(assignmentValue, "me", StringComparison.OrdinalIgnoreCase))
        {
            var currentUser = await client.GetCurrentUserAsync(cancellationToken);
            assignmentValue = string.IsNullOrWhiteSpace(currentUser.UniqueName)
                ? throw new InvalidOperationException("The current Azure DevOps identity has no unique name or email address.")
                : currentUser.UniqueName;
        }

        var current = await client.GetWorkItemForUpdateAsync(id, cancellationToken);
        var updated = await client.UpdateWorkItemAsync(
            BuildAssignmentPatch(current.Rev ?? 0, assignmentValue),
            id,
            cancellationToken);
        return AzureWorkItemMapper.ToDomain(updated);
    }

    public async Task<CoreWorkItem> UnassignAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);

        var current = await client.GetWorkItemForUpdateAsync(id, cancellationToken);
        var updated = await client.UpdateWorkItemAsync(
            BuildUnassignmentPatch(current.Rev ?? 0),
            id,
            cancellationToken);
        return AzureWorkItemMapper.ToDomain(updated);
    }

    internal static JsonPatchDocument BuildAssignmentPatch(int revision, string assignee) =>
    [
        RevisionTest(revision),
        new JsonPatchOperation
        {
            Operation = Operation.Add,
            Path = AssignedToPath,
            Value = assignee
        }
    ];

    internal static JsonPatchDocument BuildUnassignmentPatch(int revision) =>
    [
        RevisionTest(revision),
        new JsonPatchOperation
        {
            Operation = Operation.Add,
            Path = AssignedToPath,
            Value = string.Empty
        }
    ];

    private static JsonPatchOperation RevisionTest(int revision) => new()
    {
        Operation = Operation.Test,
        Path = "/rev",
        Value = revision
    };

    private static void ValidateId(int id)
    {
        if (id <= 0)
        {
            throw new ArgumentException("Work item ID must be positive.", nameof(id));
        }
    }
}