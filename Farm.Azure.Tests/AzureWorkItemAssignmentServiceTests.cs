using Farm.Core.Domain;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Xunit;
using AzureWorkItem = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem;
using CoreIdentity = Farm.Core.Domain.Identity;

namespace Farm.Azure.Tests;

public sealed class AzureWorkItemAssignmentServiceTests
{
    [Fact]
    public async Task AssignAsync_me_resolves_current_users_unique_name()
    {
        var client = new FakeMutationClient
        {
            CurrentUser = new CoreIdentity("identity-id", "Person Example", "person@example.com")
        };
        var service = new AzureWorkItemAssignmentService(client);

        await service.AssignAsync(42, "me");

        Assert.Equal(1, client.CurrentUserRequests);
        AssertAssignmentPatch(client.Patch!, 3, "person@example.com");
    }

    [Fact]
    public async Task AssignAsync_explicit_assignee_uses_trimmed_value_without_identity_lookup()
    {
        var client = new FakeMutationClient();
        var service = new AzureWorkItemAssignmentService(client);

        await service.AssignAsync(42, "  person@example.com  ");

        Assert.Equal(0, client.CurrentUserRequests);
        AssertAssignmentPatch(client.Patch!, 3, "person@example.com");
    }

    [Fact]
    public async Task UnassignAsync_uses_revision_test_then_resets_assigned_to()
    {
        var client = new FakeMutationClient();
        var service = new AzureWorkItemAssignmentService(client);

        await service.UnassignAsync(42);

        Assert.Collection(
            client.Patch!,
            operation => AssertOperation(operation, Operation.Test, "/rev", 3),
            operation => AssertOperation(operation, Operation.Add, "/fields/System.AssignedTo", string.Empty));
    }

    [Fact]
    public async Task AssignAsync_returns_mapped_updated_work_item()
    {
        var client = new FakeMutationClient
        {
            Updated = CreateWorkItem(
                revision: 4,
                assignedTo: new IdentityRef
                {
                    Id = "identity-id",
                    DisplayName = "Person Example",
                    UniqueName = "person@example.com"
                })
        };
        var service = new AzureWorkItemAssignmentService(client);

        var result = await service.AssignAsync(42, "person@example.com");

        Assert.Equal(42, result.Id);
        Assert.Equal(4, result.Revision);
        Assert.Equal("Test item", result.Title);
        Assert.Equal("person@example.com", result.AssignedTo?.UniqueName);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Mutations_reject_non_positive_ids(int id)
    {
        var service = new AzureWorkItemAssignmentService(new FakeMutationClient());

        await Assert.ThrowsAsync<ArgumentException>(() => service.AssignAsync(id, "person@example.com"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UnassignAsync(id));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AssignAsync_rejects_blank_assignee(string assignee)
    {
        var service = new AzureWorkItemAssignmentService(new FakeMutationClient());

        await Assert.ThrowsAsync<ArgumentException>(() => service.AssignAsync(42, assignee));
    }

    [Fact]
    public async Task AssignAsync_me_requires_current_user_unique_name()
    {
        var client = new FakeMutationClient
        {
            CurrentUser = new CoreIdentity("identity-id", "Person Example", null)
        };
        var service = new AzureWorkItemAssignmentService(client);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AssignAsync(42, "me"));

        Assert.Contains("unique name", exception.Message);
        Assert.Null(client.Patch);
    }

    [Fact]
    public async Task AssignAsync_propagates_provider_errors()
    {
        var expected = new VssServiceException("Assignment rejected by Azure DevOps.");
        var client = new FakeMutationClient { UpdateException = expected };
        var service = new AzureWorkItemAssignmentService(client);

        var actual = await Assert.ThrowsAsync<VssServiceException>(
            () => service.AssignAsync(42, "person@example.com"));

        Assert.Same(expected, actual);
    }

    private static void AssertAssignmentPatch(JsonPatchDocument patch, int revision, string assignee) =>
        Assert.Collection(
            patch,
            operation => AssertOperation(operation, Operation.Test, "/rev", revision),
            operation => AssertOperation(operation, Operation.Add, "/fields/System.AssignedTo", assignee));

    private static void AssertOperation(
        JsonPatchOperation operation,
        Operation expectedOperation,
        string expectedPath,
        object? expectedValue)
    {
        Assert.Equal(expectedOperation, operation.Operation);
        Assert.Equal(expectedPath, operation.Path);
        Assert.Equal(expectedValue, operation.Value);
    }

    private static AzureWorkItem CreateWorkItem(int revision, IdentityRef? assignedTo = null)
    {
        var fields = new Dictionary<string, object>
        {
            ["System.Title"] = "Test item",
            ["System.State"] = "Active",
            ["System.WorkItemType"] = "Task"
        };
        if (assignedTo is not null)
        {
            fields["System.AssignedTo"] = assignedTo;
        }

        return new AzureWorkItem
        {
            Id = 42,
            Rev = revision,
            Url = "https://dev.azure.com/example/_apis/wit/workItems/42",
            Fields = fields,
            Relations = []
        };
    }

    private sealed class FakeMutationClient : IAzureWorkItemMutationClient
    {
        public CoreIdentity CurrentUser { get; init; } =
            new("identity-id", "Person Example", "default@example.com");

        public AzureWorkItem Current { get; init; } = CreateWorkItem(3);

        public AzureWorkItem Updated { get; init; } = CreateWorkItem(4);

        public Exception? UpdateException { get; init; }

        public int CurrentUserRequests { get; private set; }

        public JsonPatchDocument? Patch { get; private set; }

        public Task<CoreIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default)
        {
            CurrentUserRequests++;
            return Task.FromResult(CurrentUser);
        }

        public Task<AzureWorkItem> GetWorkItemForUpdateAsync(
            int id,
            CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task<AzureWorkItem> UpdateWorkItemAsync(
            JsonPatchDocument patch,
            int id,
            CancellationToken cancellationToken = default)
        {
            Patch = patch;
            return UpdateException is null
                ? Task.FromResult(Updated)
                : Task.FromException<AzureWorkItem>(UpdateException);
        }
    }
}