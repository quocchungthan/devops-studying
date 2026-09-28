using Farm.Azure;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.WebApi;
using Xunit;

namespace Farm.Azure.Tests;

public sealed class AzureWorkItemMapperTests
{
    [Fact]
    public void ToDomain_preserves_plain_text_description()
    {
        var workItem = new WorkItem
        {
            Fields = new Dictionary<string, object>
            {
                ["System.Description"] = "First line\r\nSecond line"
            }
        };

        var result = AzureWorkItemMapper.ToDomain(workItem);

        Assert.Equal($"First line{Environment.NewLine}Second line", result.Description);
    }

    [Fact]
    public void ToDomain_maps_known_fields_and_preserves_assigned_identity()
    {
        var createdAt = new DateTimeOffset(2026, 9, 20, 8, 30, 0, TimeSpan.Zero);
        var changedAt = createdAt.AddDays(2);
        var workItem = new WorkItem
        {
            Id = 42,
            Rev = 3,
            Url = "https://dev.azure.com/example/_apis/wit/workItems/42",
            Fields = new Dictionary<string, object>
            {
                ["System.Title"] = "Ship it",
                ["System.State"] = "New",
                ["System.Reason"] = "New task",
                ["System.WorkItemType"] = "Task",
                ["System.AreaPath"] = "Example\\Platform",
                ["System.IterationPath"] = "Example\\Sprint 4",
                ["System.CreatedDate"] = createdAt,
                ["System.ChangedDate"] = changedAt,
                ["System.Description"] = "<p>First &amp; second</p><script>ignored()</script><ul><li>Next</li></ul>",
                ["System.Tags"] = "backend; urgent",
                ["System.AssignedTo"] = new IdentityRef
                {
                    Id = "identity-id",
                    DisplayName = "Person Example",
                    UniqueName = "person@example.com"
                },
                ["System.CreatedBy"] = new IdentityRef { Id = "creator", DisplayName = "Creator" },
                ["System.ChangedBy"] = new IdentityRef { Id = "editor", DisplayName = "Editor" }
            },
            Relations =
            [
                new Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItemRelation
                {
                    Rel = "AttachedFile",
                    Url = "https://dev.azure.com/example/_apis/wit/attachments/file-id",
                    Attributes = new Dictionary<string, object> { ["name"] = "evidence.txt", ["comment"] = "logs" }
                },
                new Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItemRelation
                {
                    Rel = "System.LinkTypes.Related",
                    Url = "https://dev.azure.com/example/_apis/wit/workItems/41",
                    Attributes = new Dictionary<string, object> { ["name"] = "Related" }
                }
            ]
        };

        var result = AzureWorkItemMapper.ToDomain(workItem);

        Assert.Equal(42, result.Id);
        Assert.Equal(3, result.Revision);
        Assert.Equal("Ship it", result.Title);
        Assert.Equal("New", result.State);
        Assert.Equal("Task", result.WorkItemType);
        Assert.Equal("New task", result.Reason);
        Assert.Equal("Example\\Platform", result.AreaPath);
        Assert.Equal("Example\\Sprint 4", result.IterationPath);
        Assert.Equal(createdAt, result.CreatedAt);
        Assert.Equal(changedAt, result.ChangedAt);
        Assert.Equal("Creator", result.CreatedBy?.DisplayName);
        Assert.Equal("Editor", result.ChangedBy?.DisplayName);
        Assert.Equal($"First & second{Environment.NewLine}Next", result.Description);
        Assert.Equal(["backend", "urgent"], result.Tags);
        Assert.Equal("identity-id", result.AssignedTo?.Id);
        Assert.Equal("Person Example", result.AssignedTo?.DisplayName);
        Assert.Equal("person@example.com", result.AssignedTo?.UniqueName);
        Assert.Equal(workItem.Url, result.Url?.ToString());
        var attachment = Assert.Single(result.Attachments);
        Assert.Equal("evidence.txt", attachment.FileName);
        Assert.Equal("logs", attachment.Comment);
        var relation = Assert.Single(result.Relations!);
        Assert.Equal("System.LinkTypes.Related", relation.Type);
        Assert.Equal("Related", relation.Name);
    }
}