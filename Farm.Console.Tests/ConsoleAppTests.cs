using Farm.Core.Contracts;
using Farm.Core.Domain;
using Microsoft.VisualStudio.Services.Common;
using Spectre.Console;
using Xunit;

namespace Farm.Console.Tests;

[Collection("Console output")]
public sealed class ConsoleAppTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("-4")]
    public async Task RunAsync_rejects_invalid_ids_without_loading_configuration(string id)
    {
        var exitCode = await ConsoleApp.RunAsync(["work-items", id]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RunAsync_rejects_duplicate_ids_without_loading_configuration()
    {
        var exitCode = await ConsoleApp.RunAsync(["work-items", "1", "1"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RunAsync_with_no_args_prints_help_and_exits_zero()
    {
        var output = new StringWriter();
        var originalOut = System.Console.Out;
        System.Console.SetOut(output);
        try
        {
            var exitCode = await ConsoleApp.RunAsync([]);
            var renderedOutput = output.ToString();

            Assert.Equal(0, exitCode);
            Assert.StartsWith("sam ", renderedOutput);
            Assert.Contains("Usage: sam <command>", renderedOutput);
            Assert.DoesNotContain("Usage: farm ", renderedOutput);
            Assert.Contains("work-items", renderedOutput);
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task RunAsync_help_prints_complete_pull_request_command_forms()
    {
        var output = new StringWriter();
        var originalOut = System.Console.Out;
        System.Console.SetOut(output);
        try
        {
            var exitCode = await ConsoleApp.RunAsync(["--help"]);
            var renderedOutput = output.ToString();

            Assert.Equal(0, exitCode);
            Assert.StartsWith("sam ", renderedOutput);
            Assert.Contains("Usage: sam <command>", renderedOutput);
            Assert.DoesNotContain("Usage: farm ", renderedOutput);
            Assert.Contains("pull-requests approved-by-me", renderedOutput);
            Assert.Contains("pull-requests assigned-to-me", renderedOutput);
            Assert.Contains("pull-requests pending-review", renderedOutput);
            Assert.Contains("pull-requests mine", renderedOutput);
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task RunAsync_global_help_paths_render_identical_complete_metadata()
    {
        var noArgs = await CaptureStandardOutputAsync([]);
        var helpCommand = await CaptureStandardOutputAsync(["help"]);
        var helpFlag = await CaptureStandardOutputAsync(["--help"]);

        Assert.Equal(0, noArgs.ExitCode);
        Assert.Equal(noArgs.Output, helpCommand.Output);
        Assert.Equal(noArgs.Output, helpFlag.Output);
        string[] expectedForms =
        [
            "work-items <id> [<id> ...]",
            "work-items assigned-to <email-or-me>",
            "work-items needs-attention",
            "work-items assign <id> <email-or-unique-name-or-me>",
            "work-items unassign <id>",
            "work-items comment <id> <text>",
            "work-items comment <id> --file <markdown-file>",
            "work-items update-description <id> <markdown-file>",
            "pull-requests approved-by-me",
            "pull-requests assigned-to-me",
            "pull-requests pending-review",
            "pull-requests mine",
            "pr-threads <pr-id>",
            "pr-diff <pr-id>",
            "work-item-comments <id>",
            "work-item-relations <id>"
        ];
        Assert.All(expectedForms, form => Assert.Contains(form, noArgs.Output));
        Assert.Contains("[implemented]", noArgs.Output);
        Assert.Contains("[not implemented]", noArgs.Output);
    }

    [Fact]
    public async Task RunAsync_work_items_help_paths_render_identical_detail_usage()
    {
        var helpCommand = await CaptureStandardOutputAsync(["help", "work-items"]);
        var helpFlag = await CaptureStandardOutputAsync(["work-items", "--help"]);

        Assert.Equal(0, helpCommand.ExitCode);
        Assert.Equal(helpCommand.Output, helpFlag.Output);
        Assert.Contains("Usage: sam work-items <id> [<id> ...]", helpCommand.Output);
        Assert.Contains("sam work-items needs-attention", helpCommand.Output);
        Assert.Contains("sam work-items assign <id> <email-or-unique-name-or-me>", helpCommand.Output);
        Assert.Contains("sam work-items unassign <id>", helpCommand.Output);
        Assert.Contains("sam work-items comment <id> <text>", helpCommand.Output);
        Assert.Contains("sam work-items update-description <id> <markdown-file>", helpCommand.Output);
        Assert.Contains("Show full work-item detail", helpCommand.Output);
        Assert.Contains("Status: implemented", helpCommand.Output);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("-v")]
    public async Task RunAsync_version_flag_exits_zero(string flag)
    {
        var exitCode = await ConsoleApp.RunAsync([flag]);

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task RunAsync_help_flag_exits_zero(string flag)
    {
        var exitCode = await ConsoleApp.RunAsync([flag]);

        Assert.Equal(0, exitCode);
    }

    [Theory]
    [InlineData("pr-diff", "1")]
    [InlineData("work-item-comments", "1")]
    [InlineData("work-item-relations", "1")]
    public async Task RunAsync_stub_commands_return_not_implemented_exit_code(string command, string id)
    {
        var error = new StringWriter();
        var originalError = System.Console.Error;
        System.Console.SetError(error);
        try
        {
            var exitCode = await ConsoleApp.RunAsync([command, id]);

            Assert.Equal(3, exitCode);
            Assert.Contains("not yet implemented", error.ToString());
        }
        finally
        {
            System.Console.SetError(originalError);
        }
    }

    [Theory]
    [InlineData("pr-threads")]
    [InlineData("pr-diff")]
    [InlineData("work-item-comments")]
    [InlineData("work-item-relations")]
    public async Task RunAsync_stub_commands_reject_invalid_args(string command)
    {
        var exitCode = await ConsoleApp.RunAsync([command, "not-a-number"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RunAsync_work_items_assigned_to_rejects_missing_assignee()
    {
        var exitCode = await ConsoleApp.RunAsync(["work-items", "assigned-to"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RunAsync_work_items_assigned_to_rejects_extra_args()
    {
        var exitCode = await ConsoleApp.RunAsync(["work-items", "assigned-to", "me", "extra"]);

        Assert.Equal(2, exitCode);
    }

    [Theory]
    [InlineData("assign")]
    [InlineData("assign 1")]
    [InlineData("assign 0 me")]
    [InlineData("assign -1 person@example.com")]
    [InlineData("assign 1 me extra")]
    [InlineData("unassign")]
    [InlineData("unassign 0")]
    [InlineData("unassign -1")]
    [InlineData("unassign 1 extra")]
    public async Task RunAsync_work_item_mutations_reject_invalid_arguments_without_loading_configuration(
        string arguments)
    {
        var exitCode = await ConsoleApp.RunAsync(["work-items", .. arguments.Split(' ')]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RunAsync_work_item_assign_prints_confirmation_and_updated_details()
    {
        var workItem = CreateWorkItem();
        var assignmentService = new StubWorkItemAssignmentService((id, assignee, _) =>
        {
            Assert.Equal(42, id);
            Assert.Equal("me", assignee);
            return Task.FromResult(workItem);
        });
        var output = new StringWriter();
        var originalOut = System.Console.Out;
        System.Console.SetOut(output);
        AnsiConsole.Record();

        try
        {
            var exitCode = await ConsoleApp.RunAsync(
                ["work-items", "assign", "42", "me"],
                assignmentService);

            Assert.Equal(0, exitCode);
            Assert.Contains("Assigned work item 42 to person@example.com.", output.ToString());
            var details = AnsiConsole.ExportText();
            Assert.Contains("Work Item 42", details);
            Assert.Contains("Assignment test", details);
            Assert.Contains("person@example.com", details);
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task RunAsync_work_item_comment_file_reads_local_markdown_and_calls_service()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sam-comment-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(filePath, "**Comment from disk**");
        var service = new StubWorkItemCommentService();
        try
        {
            var exitCode = await ConsoleApp.RunAsync(
                ["work-items", "comment", "42", "--file", filePath],
                assignmentService: null,
                commentService: service);

            Assert.Equal(0, exitCode);
            Assert.Equal(42, service.Id);
            Assert.Equal("**Comment from disk**", service.Text);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Fact]
    public async Task RunAsync_work_item_update_description_reads_local_markdown_and_calls_service()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"sam-description-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(filePath, "# New description");
        var service = new StubWorkItemDescriptionService();
        try
        {
            var exitCode = await ConsoleApp.RunAsync(
                ["work-items", "update-description", "42", filePath],
                assignmentService: null,
                descriptionService: service);

            Assert.Equal(0, exitCode);
            Assert.Equal(42, service.Id);
            Assert.Equal("# New description", service.Description);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_work_item_assign_returns_one_without_stack_trace_for_expected_failures(
        bool useVssException)
    {
        Exception failure = useVssException
            ? new TestVssException("Azure assignment failed.")
            : new InvalidOperationException("Work item changed during assignment.");
        var assignmentService = new StubWorkItemAssignmentService((_, _, _) => Task.FromException<WorkItem>(failure));
        var error = new StringWriter();
        var originalError = System.Console.Error;
        System.Console.SetError(error);

        try
        {
            var exitCode = await ConsoleApp.RunAsync(
                ["work-items", "assign", "42", "me"],
                assignmentService);

            Assert.Equal(1, exitCode);
            Assert.Equal($"Error: {failure.Message}{Environment.NewLine}", error.ToString());
            Assert.DoesNotContain(nameof(ConsoleAppTests), error.ToString());
            Assert.DoesNotContain(" at ", error.ToString());
        }
        finally
        {
            System.Console.SetError(originalError);
        }
    }

    [Fact]
    public async Task RunAsync_pull_requests_rejects_unknown_subcommand()
    {
        var exitCode = await ConsoleApp.RunAsync(["pull-requests", "not-a-subcommand"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RunAsync_pull_requests_rejects_extra_args()
    {
        var exitCode = await ConsoleApp.RunAsync(["pull-requests", "approved-by-me", "extra"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RunAsync_work_items_needs_attention_rejects_extra_args()
    {
        var exitCode = await ConsoleApp.RunAsync(["work-items", "needs-attention", "extra"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RunAsync_whoami_rejects_arguments()
    {
        var exitCode = await ConsoleApp.RunAsync(["whoami", "extra"]);

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public async Task RunAsync_my_groups_rejects_arguments()
    {
        var exitCode = await ConsoleApp.RunAsync(["my-groups", "extra"]);

        Assert.Equal(2, exitCode);
    }

    [Theory]
    [InlineData("whoami")]
    [InlineData("my-groups")]
    [InlineData("pull-requests")]
    [InlineData("pull-requests assigned-to-me")]
    [InlineData("pull-requests pending-review")]
    [InlineData("pull-requests mine")]
    [InlineData("work-items needs-attention")]
    [InlineData("work-items assign 1 me")]
    [InlineData("work-items unassign 1")]
    [InlineData("pr-threads 1")]
    public async Task RunAsync_commands_requiring_configuration_fail_cleanly_without_environment(string commandLine)
    {
        var organizationUrl = Environment.GetEnvironmentVariable("FARM_AZURE_DEVOPS_ORGANIZATION_URL");
        var project = Environment.GetEnvironmentVariable("FARM_AZURE_DEVOPS_PROJECT");
        var pat = Environment.GetEnvironmentVariable("FARM_AZURE_DEVOPS_PAT");
        Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_ORGANIZATION_URL", null);
        Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_PROJECT", null);
        Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_PAT", null);

        var error = new StringWriter();
        var originalError = System.Console.Error;
        System.Console.SetError(error);
        try
        {
            var exitCode = await ConsoleApp.RunAsync(commandLine.Split(' '));

            Assert.Equal(1, exitCode);
            Assert.Contains("Environment variable", error.ToString());
        }
        finally
        {
            System.Console.SetError(originalError);
            Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_ORGANIZATION_URL", organizationUrl);
            Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_PROJECT", project);
            Environment.SetEnvironmentVariable("FARM_AZURE_DEVOPS_PAT", pat);
        }
    }

    private static async Task<(int ExitCode, string Output)> CaptureStandardOutputAsync(string[] args)
    {
        var output = new StringWriter();
        var originalOut = System.Console.Out;
        System.Console.SetOut(output);
        try
        {
            return (await ConsoleApp.RunAsync(args), output.ToString());
        }
        finally
        {
            System.Console.SetOut(originalOut);
        }
    }

    private static WorkItem CreateWorkItem() =>
        new(
            42,
            3,
            "Assignment test",
            "Active",
            "Task",
            new Identity("person-id", "Example Person", "person@example.com"),
            new Uri("https://example.test/items/42"),
            []);

    private sealed class StubWorkItemAssignmentService(
        Func<int, string, CancellationToken, Task<WorkItem>> assign) : IWorkItemAssignmentService
    {
        public Task<WorkItem> AssignAsync(
            int id,
            string assignee,
            CancellationToken cancellationToken = default) =>
            assign(id, assignee, cancellationToken);

        public Task<WorkItem> UnassignAsync(int id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StubWorkItemCommentService : IWorkItemCommentService
    {
        public int Id { get; private set; }

        public string? Text { get; private set; }

        public Task AddCommentAsync(int id, string text, CancellationToken cancellationToken = default)
        {
            Id = id;
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class StubWorkItemDescriptionService : IWorkItemDescriptionService
    {
        public int Id { get; private set; }

        public string? Description { get; private set; }

        public Task UpdateDescriptionAsync(int id, string description, CancellationToken cancellationToken = default)
        {
            Id = id;
            Description = description;
            return Task.CompletedTask;
        }
    }

    private sealed class TestVssException(string message) : VssException(message);
}

[CollectionDefinition("Console output", DisableParallelization = true)]
public sealed class ConsoleOutputCollection
{
}