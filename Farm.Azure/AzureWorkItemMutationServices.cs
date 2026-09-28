using Farm.Core.Contracts;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;

namespace Farm.Azure;

public interface IAzureWorkItemCommentClient
{
    Task AddWorkItemCommentAsync(
        int id,
        string text,
        CancellationToken cancellationToken = default);
}

public sealed class AzureWorkItemCommentService(IAzureWorkItemCommentClient client) : IWorkItemCommentService
{
    public async Task AddCommentAsync(
        int id,
        string text,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        await client.AddWorkItemCommentAsync(id, text, cancellationToken);
    }

    private static void ValidateId(int id)
    {
        if (id <= 0)
        {
            throw new ArgumentException("Work item ID must be positive.", nameof(id));
        }
    }
}

public sealed class AzureWorkItemDescriptionService(IAzureWorkItemMutationClient client) : IWorkItemDescriptionService
{
    private const string DescriptionPath = "/fields/System.Description";

    public async Task UpdateDescriptionAsync(
        int id,
        string description,
        CancellationToken cancellationToken = default)
    {
        ValidateId(id);
        ArgumentNullException.ThrowIfNull(description);

        var current = await client.GetWorkItemForUpdateAsync(id, cancellationToken);
        await client.UpdateWorkItemAsync(
            BuildDescriptionPatch(current.Rev ?? 0, description),
            id,
            cancellationToken);
    }

    internal static JsonPatchDocument BuildDescriptionPatch(int revision, string description) =>
    [
        new JsonPatchOperation
        {
            Operation = Operation.Test,
            Path = "/rev",
            Value = revision
        },
        new JsonPatchOperation
        {
            Operation = Operation.Add,
            Path = DescriptionPath,
            Value = description
        }
    ];

    private static void ValidateId(int id)
    {
        if (id <= 0)
        {
            throw new ArgumentException("Work item ID must be positive.", nameof(id));
        }
    }
}