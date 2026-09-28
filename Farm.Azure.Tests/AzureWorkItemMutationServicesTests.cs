using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Xunit;
using AzureWorkItem = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem;

namespace Farm.Azure.Tests;

public sealed class AzureWorkItemMutationServicesTests
{
    [Fact]
    public async Task AddCommentAsync_calls_supported_work_item_comments_client()
    {
        var client = new FakeCommentClient();
        var service = new AzureWorkItemCommentService(client);

        await service.AddCommentAsync(42, "A Markdown comment.");

        Assert.Equal(42, client.Id);
        Assert.Equal("A Markdown comment.", client.Text);
    }

    [Fact]
    public async Task UpdateDescriptionAsync_uses_current_revision_and_replaces_description()
    {
        var client = new FakeMutationClient();
        var service = new AzureWorkItemDescriptionService(client);

        await service.UpdateDescriptionAsync(42, "# Replacement");

        Assert.Collection(
            client.Patch!,
            operation => AssertOperation(operation, Operation.Test, "/rev", 7),
            operation => AssertOperation(operation, Operation.Add, "/fields/System.Description", "# Replacement"));
    }

    private static void AssertOperation(
        JsonPatchOperation operation,
        Operation expectedOperation,
        string expectedPath,
        object expectedValue)
    {
        Assert.Equal(expectedOperation, operation.Operation);
        Assert.Equal(expectedPath, operation.Path);
        Assert.Equal(expectedValue, operation.Value);
    }

    private sealed class FakeCommentClient : IAzureWorkItemCommentClient
    {
        public int Id { get; private set; }

        public string? Text { get; private set; }

        public Task AddWorkItemCommentAsync(int id, string text, CancellationToken cancellationToken = default)
        {
            Id = id;
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMutationClient : IAzureWorkItemMutationClient
    {
        public JsonPatchDocument? Patch { get; private set; }

        public Task<Farm.Core.Domain.Identity> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AzureWorkItem> GetWorkItemForUpdateAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AzureWorkItem { Id = id, Rev = 7, Fields = new Dictionary<string, object>() });

        public Task<AzureWorkItem> UpdateWorkItemAsync(JsonPatchDocument patch, int id, CancellationToken cancellationToken = default)
        {
            Patch = patch;
            return Task.FromResult(new AzureWorkItem { Id = id, Rev = 8, Fields = new Dictionary<string, object>() });
        }
    }
}