using Farm.Core.Domain;
using Spectre.Console;
using Xunit;

namespace Farm.Console.Tests;

[Collection("Console output")]
public sealed class ConsoleTablesTests
{
    [Fact]
    public void EscapeCell_escapes_brackets_in_titles_and_display_names()
    {
        var escaped = ConsoleTables.EscapeCell("[Feature] assigned to [Developer]");

        Assert.Equal("[[Feature]] assigned to [[Developer]]", escaped);
    }

    [Fact]
    public void RenderWorkItemDetails_renders_a_separate_escaped_section_for_each_item()
    {
        var first = CreateWorkItem(42, "[red]Remote title[/]", "<literal> [blue]text[/]");
        var second = CreateWorkItem(43, "Second item", "Another description");
        AnsiConsole.Record();

        ConsoleTables.RenderWorkItemDetails([first, second]);
        var output = AnsiConsole.ExportText();

        Assert.Contains("Work Item 42", output);
        Assert.Contains("Work Item 43", output);
        Assert.Contains("[red]Remote title[/]", output);
        Assert.Contains("<literal> [blue]text[/]", output);
        Assert.Contains("evidence.txt", output);
        Assert.Contains("Related", output);
        Assert.DoesNotContain("Assigned To", output);
    }

    [Fact]
    public void RenderWorkItemDetails_wraps_long_description_without_truncating_it()
    {
        var description = $"Description start {string.Join(' ', Enumerable.Repeat("detailed-content", 30))} description-end-marker";
        var workItem = CreateWorkItem(42, "Long detail", description);
        AnsiConsole.Record();
        var previousOutputLength = AnsiConsole.ExportText().Length;

        ConsoleTables.RenderWorkItemDetails([workItem]);
        var output = AnsiConsole.ExportText()[previousOutputLength..];

        Assert.Contains("Description start", output);
        Assert.Contains("description-end-marker", output);
        Assert.DoesNotContain("…", output);
    }

    [Fact]
    public void RenderWorkItems_renders_expanded_models_as_compact_rows_only()
    {
        var workItem = CreateWorkItem(42, "[red]Remote title[/]", "detail-only description");
        AnsiConsole.Record();
        var previousOutputLength = AnsiConsole.ExportText().Length;

        ConsoleTables.RenderWorkItems([workItem]);
        var output = AnsiConsole.ExportText()[previousOutputLength..];

        Assert.Contains("ID", output);
        Assert.Contains("Title", output);
        Assert.Contains("Type", output);
        Assert.Contains("State", output);
        Assert.Contains("Assigned To", output);
        Assert.Contains("42", output);
        Assert.Contains("[red]Remote title[/]", output);
        Assert.Contains("Task", output);
        Assert.Contains("Active", output);
        Assert.Contains("[Person]", output);
        Assert.DoesNotContain("Work Item 42", output);
        Assert.DoesNotContain("detail-only description", output);
        Assert.DoesNotContain("Reason", output);
        Assert.DoesNotContain("Attachments", output);
        Assert.DoesNotContain("Relations", output);
    }

    private static WorkItem CreateWorkItem(int id, string title, string description) =>
        new(
            id,
            2,
            title,
            "Active",
            "Task",
            new Identity("person-id", "[Person]", "person@example.com"),
            new Uri($"https://example.test/items/{id}"),
            [new Attachment("file-id", "evidence.txt", new Uri("https://example.test/files/1"), "[log]")],
            Reason: "Work started",
            AreaPath: "Example\\Platform",
            IterationPath: "Example\\Sprint 4",
            CreatedAt: new DateTimeOffset(2026, 9, 20, 8, 30, 0, TimeSpan.Zero),
            ChangedAt: new DateTimeOffset(2026, 9, 22, 9, 45, 0, TimeSpan.Zero),
            CreatedBy: new Identity("creator-id", "Creator", null),
            ChangedBy: new Identity("editor-id", "Editor", null),
            Description: description,
            Tags: ["backend", "[urgent]"],
            Relations: [new WorkItemRelation("System.LinkTypes.Related", new Uri("https://example.test/items/1"), "Related")]);
}