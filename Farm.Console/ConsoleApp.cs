using System.Net.Http;
using System.Reflection;
using Farm.Azure;
using Farm.Core.Contracts;
using Microsoft.VisualStudio.Services.Common;

public static class ConsoleApp
{
    private const int NotImplementedExitCode = 3;

    private enum ImplementationStatus
    {
        Implemented,
        NotImplemented
    }

    private sealed record ToolDefinition(
        string Name,
        string Description,
        IReadOnlyList<string> UsageForms);

    private sealed record CommandDefinition(
        string Name,
        string Description,
        IReadOnlyList<string> UsageForms,
        IReadOnlyList<string> Subcommands,
        ImplementationStatus Status,
        Func<string[], CancellationToken, Task<int>> Handler);

    private static readonly ToolDefinition Tool = new(
        "sam",
        "Work with Azure DevOps work items, pull requests, and identities.",
        ["<command> [args]", "help [command]", "--help|-h", "--version|-v"]);

    private static readonly CommandDefinition[] Commands =
    [
        new(
            "work-items",
            "Show full work-item detail, assign, comment on, or replace the description of an item.",
            [
                "work-items <id> [<id> ...]",
                "work-items assigned-to <email-or-me>",
                "work-items needs-attention",
                "work-items assign <id> <email-or-unique-name-or-me>",
                "work-items unassign <id>",
                "work-items comment <id> <text>",
                "work-items comment <id> --file <markdown-file>",
                "work-items update-description <id> <markdown-file>"
            ],
            ["assigned-to", "needs-attention", "assign", "unassign", "comment", "update-description"],
            ImplementationStatus.Implemented,
            (args, cancellationToken) => RunWorkItemsAsync(args, cancellationToken)),
        new(
            "whoami",
            "Print the current Azure DevOps identity (id, display name, unique name).",
            ["whoami"],
            [],
            ImplementationStatus.Implemented,
            RunWhoAmIAsync),
        new(
            "my-groups",
            "List the Azure DevOps groups/teams the current user belongs to.",
            ["my-groups"],
            [],
            ImplementationStatus.Implemented,
            RunMyGroupsAsync),
        new(
            "pull-requests",
            "List active pull requests, or filter by approved/assigned/pending-review/mine.",
            [
                "pull-requests",
                "pull-requests approved-by-me",
                "pull-requests assigned-to-me",
                "pull-requests pending-review",
                "pull-requests mine"
            ],
            ["approved-by-me", "assigned-to-me", "pending-review", "mine"],
            ImplementationStatus.Implemented,
            RunPullRequestsAsync),
        new(
            "pr-threads",
            "List discussion threads on a pull request, with resolved/unresolved status.",
            ["pr-threads <pr-id>"],
            [],
            ImplementationStatus.Implemented,
            RunPrThreadsAsync),
        new(
            "pr-diff",
            "Show the file diff for a pull request.",
            ["pr-diff <pr-id>"],
            [],
            ImplementationStatus.NotImplemented,
            (args, _) => Task.FromResult(RunNotImplementedStub(args, "pull request ID", "pr-diff"))),
        new(
            "work-item-comments",
            "List comments/discussion on a work item.",
            ["work-item-comments <id>"],
            [],
            ImplementationStatus.NotImplemented,
            (args, _) => Task.FromResult(RunNotImplementedStub(args, "work item ID", "work-item-comments"))),
        new(
            "work-item-relations",
            "List related items/attachments for a work item.",
            ["work-item-relations <id>"],
            [],
            ImplementationStatus.NotImplemented,
            (args, _) => Task.FromResult(RunNotImplementedStub(args, "work item ID", "work-item-relations"))),
        new(
            "help",
            "Show all commands or detailed help for one command.",
            ["help", "help <command>"],
            [],
            ImplementationStatus.Implemented,
            (args, _) => Task.FromResult(RunHelp(args)))
    ];

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
        => await RunAsync(args, assignmentService: null, cancellationToken);

    internal static async Task<int> RunAsync(
        string[] args,
        IWorkItemAssignmentService? assignmentService,
        CancellationToken cancellationToken = default,
        IWorkItemCommentService? commentService = null,
        IWorkItemDescriptionService? descriptionService = null)
    {
        if (args.Length == 0)
        {
            PrintHelp(Console.Out);
            return 0;
        }

        var first = args[0];
        if (IsVersionFlag(first))
        {
            PrintVersion();
            return 0;
        }

        if (IsHelpFlag(first))
        {
            if (args.Length == 1)
            {
                PrintHelp(Console.Out);
                return 0;
            }

            return RunHelp(args.Skip(1).ToArray());
        }

        var command = FindCommand(first);
        if (command is null)
        {
            Console.Error.WriteLine($"Unknown command: {first}");
            PrintHelp(Console.Error);
            return 2;
        }

        var rest = args.Skip(1).ToArray();
        if (rest.Any(IsHelpFlag))
        {
            PrintCommandHelp(command);
            return 0;
        }

        if (rest.Any(IsVersionFlag))
        {
            PrintVersion();
            return 0;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            return command.Name == "work-items"
                && (assignmentService is not null || commentService is not null || descriptionService is not null)
                ? await RunWorkItemsAsync(rest, cancellation.Token, assignmentService, commentService, descriptionService)
                : await command.Handler(rest, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Console.Error.WriteLine("Operation cancelled.");
            return 130;
        }
        catch (Exception exception) when (exception is ArgumentException
            or InvalidOperationException
            or UriFormatException
            or FormatException
            or IOException
            or UnauthorizedAccessException
            or HttpRequestException
            or VssException)
        {
            Console.Error.WriteLine($"Error: {exception.Message}");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task<int> RunWorkItemsAsync(
        string[] args,
        CancellationToken cancellationToken,
        IWorkItemAssignmentService? assignmentService = null,
        IWorkItemCommentService? commentService = null,
        IWorkItemDescriptionService? descriptionService = null)
    {
        if (args.Length > 0 && string.Equals(args[0], "assign", StringComparison.OrdinalIgnoreCase))
        {
            return await RunWorkItemAssignAsync(args.Skip(1).ToArray(), cancellationToken, assignmentService);
        }

        if (args.Length > 0 && string.Equals(args[0], "unassign", StringComparison.OrdinalIgnoreCase))
        {
            return await RunWorkItemUnassignAsync(args.Skip(1).ToArray(), cancellationToken);
        }

        if (args.Length > 0 && string.Equals(args[0], "comment", StringComparison.OrdinalIgnoreCase))
        {
            return await RunWorkItemCommentAsync(args.Skip(1).ToArray(), cancellationToken, commentService);
        }

        if (args.Length > 0 && string.Equals(args[0], "update-description", StringComparison.OrdinalIgnoreCase))
        {
            return await RunWorkItemDescriptionAsync(args.Skip(1).ToArray(), cancellationToken, descriptionService);
        }

        if (args.Length > 0 && string.Equals(args[0], "assigned-to", StringComparison.OrdinalIgnoreCase))
        {
            return await RunWorkItemsAssignedToAsync(args.Skip(1).ToArray(), cancellationToken);
        }

        if (args.Length == 1 && string.Equals(args[0], "needs-attention", StringComparison.OrdinalIgnoreCase))
        {
            return await RunWorkItemsNeedsAttentionAsync(cancellationToken);
        }

        if (!TryParsePositiveIds(args, "work item ID", out var ids, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage(Console.Error, "work-items", "work-items <id>");
            return 2;
        }

        var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();
        using var client = new AzureDevOpsClient(settings);
        IWorkItemSource workItemSource = new AzureWorkItemSource(client);
        var workItems = await workItemSource.GetWorkItemsAsync(ids, cancellationToken);

        ConsoleTables.RenderWorkItemDetails(workItems);
        return 0;
    }

    private static async Task<int> RunWorkItemCommentAsync(
        string[] args,
        CancellationToken cancellationToken,
        IWorkItemCommentService? commentService)
    {
        if (!TryParseSingleId(args[..Math.Min(args.Length, 1)], "work item ID", out var id, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage(Console.Error, "work-items", "work-items comment");
            return 2;
        }

        string text;
        if (args.Length == 2 && !string.IsNullOrWhiteSpace(args[1]))
        {
            text = args[1];
        }
        else if (args.Length == 3 && string.Equals(args[1], "--file", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(args[2]))
            {
                Console.Error.WriteLine("A Markdown file path is required after --file.");
                PrintUsage(Console.Error, "work-items", "work-items comment");
                return 2;
            }

            text = await File.ReadAllTextAsync(args[2], cancellationToken);
        }
        else
        {
            Console.Error.WriteLine("A non-empty comment text or --file <markdown-file> is required.");
            PrintUsage(Console.Error, "work-items", "work-items comment");
            return 2;
        }

        if (commentService is null)
        {
            var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();
            using var client = new AzureDevOpsClient(settings);
            await new AzureWorkItemCommentService(client).AddCommentAsync(id, text, cancellationToken);
        }
        else
        {
            await commentService.AddCommentAsync(id, text, cancellationToken);
        }

        Console.WriteLine($"Added comment to work item {id}.");
        return 0;
    }

    private static async Task<int> RunWorkItemDescriptionAsync(
        string[] args,
        CancellationToken cancellationToken,
        IWorkItemDescriptionService? descriptionService)
    {
        if (!TryParseSingleId(args[..Math.Min(args.Length, 1)], "work item ID", out var id, out var error)
            || args.Length != 2
            || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine(error ?? "Exactly one Markdown file path is required.");
            PrintUsage(Console.Error, "work-items", "work-items update-description");
            return 2;
        }

        var description = await File.ReadAllTextAsync(args[1], cancellationToken);
        if (descriptionService is null)
        {
            var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();
            using var client = new AzureDevOpsClient(settings);
            await new AzureWorkItemDescriptionService(client).UpdateDescriptionAsync(id, description, cancellationToken);
        }
        else
        {
            await descriptionService.UpdateDescriptionAsync(id, description, cancellationToken);
        }

        Console.WriteLine($"Updated description for work item {id}.");
        return 0;
    }

    private static async Task<int> RunWorkItemAssignAsync(
        string[] args,
        CancellationToken cancellationToken,
        IWorkItemAssignmentService? assignmentService = null)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Exactly one positive work item ID and one non-empty assignee are required.");
            PrintUsage(Console.Error, "work-items", "work-items assign");
            return 2;
        }

        if (!TryParseSingleId(args[..1], "work item ID", out var id, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage(Console.Error, "work-items", "work-items assign");
            return 2;
        }

        if (string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Exactly one non-empty assignee is required.");
            PrintUsage(Console.Error, "work-items", "work-items assign");
            return 2;
        }

        if (assignmentService is null)
        {
            var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();
            using var client = new AzureDevOpsClient(settings);
            return await AssignAsync(new AzureWorkItemAssignmentService(client));
        }

        return await AssignAsync(assignmentService);

        async Task<int> AssignAsync(IWorkItemAssignmentService service)
        {
            var workItem = await service.AssignAsync(id, args[1], cancellationToken);

            var assignedTo = workItem.AssignedTo?.UniqueName ?? workItem.AssignedTo?.DisplayName ?? args[1];
            Console.WriteLine($"Assigned work item {id} to {assignedTo}.");
            ConsoleTables.RenderWorkItemDetails([workItem]);
            return 0;
        }
    }

    private static async Task<int> RunWorkItemUnassignAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseSingleId(args, "work item ID", out var id, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage(Console.Error, "work-items", "work-items unassign");
            return 2;
        }

        var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();
        using var client = new AzureDevOpsClient(settings);
        IWorkItemAssignmentService assignmentService = new AzureWorkItemAssignmentService(client);
        var workItem = await assignmentService.UnassignAsync(id, cancellationToken);

        Console.WriteLine($"Unassigned work item {id}.");
        ConsoleTables.RenderWorkItemDetails([workItem]);
        return 0;
    }

    private static async Task<int> RunWorkItemsNeedsAttentionAsync(CancellationToken cancellationToken)
    {
        var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();
        using var client = new AzureDevOpsClient(settings);
        IWorkItemSource workItemSource = new AzureWorkItemSource(client);
        var workItems = await workItemSource.GetNeedsAttentionAsync(cancellationToken);

        ConsoleTables.RenderWorkItems(workItems, needsAttention: true);
        return 0;
    }

    private static async Task<int> RunWorkItemsAssignedToAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            Console.Error.WriteLine("Exactly one assignee (email address or 'me') is required.");
            PrintUsage(Console.Error, "work-items", "work-items assigned-to");
            return 2;
        }

        var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();
        using var client = new AzureDevOpsClient(settings);
        IWorkItemSource workItemSource = new AzureWorkItemSource(client);
        var workItems = await workItemSource.GetWorkItemsAssignedToAsync(args[0], cancellationToken);

        ConsoleTables.RenderWorkItems(workItems);
        return 0;
    }

    private static async Task<int> RunWhoAmIAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 0)
        {
            Console.Error.WriteLine("whoami takes no arguments.");
            PrintUsage(Console.Error, "whoami");
            return 2;
        }

        var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();
        using var client = new AzureDevOpsClient(settings);
        IIdentityDirectory identityDirectory = new AzureIdentityDirectory(client);
        var currentUser = await identityDirectory.GetCurrentUserAsync(cancellationToken);

        Console.WriteLine($"Id: {currentUser.Id}");
        Console.WriteLine($"Display name: {currentUser.DisplayName}");
        Console.WriteLine($"Unique name: {currentUser.UniqueName}");

        return 0;
    }

    private static async Task<int> RunMyGroupsAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 0)
        {
            Console.Error.WriteLine("my-groups takes no arguments.");
            PrintUsage(Console.Error, "my-groups");
            return 2;
        }

        var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();
        using var client = new AzureDevOpsClient(settings);
        IIdentityDirectory identityDirectory = new AzureIdentityDirectory(client);
        var groups = await identityDirectory.GetGroupsForCurrentUserAsync(cancellationToken);

        foreach (var group in groups)
        {
            Console.WriteLine(group.DisplayName);
        }

        return 0;
    }

    private static async Task<int> RunPullRequestsAsync(string[] args, CancellationToken cancellationToken)
    {
        var validSubcommands = GetCommand("pull-requests").Subcommands;
        var subcommand = args.Length == 1 ? args[0].ToLowerInvariant() : null;
        if (args.Length > 1 || (args.Length == 1 && !validSubcommands.Contains(subcommand)))
        {
            Console.Error.WriteLine("Unknown pull-requests arguments.");
            PrintUsage(Console.Error, "pull-requests");
            return 2;
        }

        var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();
        using var client = new AzureDevOpsClient(settings);
        IPullRequestSource pullRequestSource = new AzurePullRequestSource(client);

        var pullRequests = subcommand switch
        {
            "approved-by-me" => await pullRequestSource.ListApprovedByCurrentUserAsync(cancellationToken),
            "assigned-to-me" => await pullRequestSource.ListAssignedToCurrentUserAsync(cancellationToken),
            "pending-review" => await pullRequestSource.ListPendingReviewByCurrentUserAsync(cancellationToken),
            "mine" => await pullRequestSource.ListCreatedByCurrentUserAsync(cancellationToken),
            _ => await pullRequestSource.ListActiveAsync(cancellationToken)
        };

        ConsoleTables.RenderPullRequests(pullRequests);
        return 0;
    }

    private static async Task<int> RunPrThreadsAsync(string[] args, CancellationToken cancellationToken)
    {
        if (!TryParseSingleId(args, "pull request ID", out var pullRequestId, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage(Console.Error, "pr-threads");
            return 2;
        }

        var settings = AzureDevOpsSettingsLoader.LoadFromEnvironment();
        using var client = new AzureDevOpsClient(settings);
        IPullRequestSource pullRequestSource = new AzurePullRequestSource(client);
        var threads = await pullRequestSource.ListThreadsAsync(pullRequestId, cancellationToken);

        ConsoleTables.RenderThreads(threads);
        return 0;
    }

    private static int RunNotImplementedStub(string[] args, string idLabel, string commandName)
    {
        if (!TryParseSingleId(args, idLabel, out _, out var error))
        {
            Console.Error.WriteLine(error);
            PrintUsage(Console.Error, commandName);
            return 2;
        }

        Console.Error.WriteLine("This command is not yet implemented.");
        return NotImplementedExitCode;
    }

    private static bool TryParsePositiveIds(string[] args, string idLabel, out int[] ids, out string? error)
    {
        ids = [];
        error = null;

        if (args.Length == 0 || !args.All(value => int.TryParse(value, out var id) && id > 0))
        {
            error = $"At least one positive numeric {idLabel} is required.";
            return false;
        }

        var parsed = args.Select(int.Parse).ToArray();
        if (parsed.Distinct().Count() != parsed.Length)
        {
            error = $"{idLabel}s must not contain duplicates.";
            return false;
        }

        ids = parsed;
        return true;
    }

    private static bool TryParseSingleId(string[] args, string idLabel, out int id, out string? error)
    {
        id = 0;
        error = null;

        if (args.Length != 1 || !int.TryParse(args[0], out id) || id <= 0)
        {
            error = $"Exactly one positive numeric {idLabel} is required.";
            return false;
        }

        return true;
    }

    private static bool IsHelpFlag(string value) => value is "--help" or "-h";

    private static bool IsVersionFlag(string value) => value is "--version" or "-v";

    private static string GetVersion() =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0-dev";

    private static void PrintVersion() => Console.WriteLine($"{Tool.Name} {GetVersion()}");

    private static void PrintHelp(TextWriter writer)
    {
        writer.WriteLine($"{Tool.Name} {GetVersion()}");
        writer.WriteLine(Tool.Description);
        writer.WriteLine();
        foreach (var (form, index) in Tool.UsageForms.Select((form, index) => (form, index)))
        {
            writer.WriteLine($"{(index == 0 ? "Usage:" : "      ")} {Tool.Name} {form}");
        }
        writer.WriteLine();
        writer.WriteLine("Commands:");
        foreach (var command in Commands)
        {
            foreach (var (form, index) in command.UsageForms.Select((form, index) => (form, index)))
            {
                var details = index == 0 ? $"{command.Description} [{FormatStatus(command.Status)}]" : string.Empty;
                writer.WriteLine($"  {form,-62} {details}");
            }
        }

        writer.WriteLine();
        writer.WriteLine("Use 'sam help <command>' or 'sam <command> --help' for command-specific usage.");
    }

    private static void PrintCommandHelp(CommandDefinition command)
    {
        Console.WriteLine($"{Tool.Name} {GetVersion()}");
        Console.WriteLine();
        PrintUsage(Console.Out, command);
        Console.WriteLine(command.Description);
        Console.WriteLine($"Status: {FormatStatus(command.Status)}");
    }

    private static int RunHelp(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp(Console.Out);
            return 0;
        }

        if (args.Length != 1)
        {
            Console.Error.WriteLine("Help accepts at most one command name.");
            PrintUsage(Console.Error, "help");
            return 2;
        }

        var command = FindCommand(args[0]);
        if (command is null)
        {
            Console.Error.WriteLine($"Unknown command: {args[0]}");
            PrintHelp(Console.Error);
            return 2;
        }

        PrintCommandHelp(command);
        return 0;
    }

    private static CommandDefinition? FindCommand(string name) =>
        Commands.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase));

    private static CommandDefinition GetCommand(string name) =>
        FindCommand(name) ?? throw new InvalidOperationException($"Command metadata was not found for '{name}'.");

    private static void PrintUsage(TextWriter writer, string commandName, string? usagePrefix = null)
    {
        PrintUsage(writer, GetCommand(commandName), usagePrefix);
    }

    private static void PrintUsage(TextWriter writer, CommandDefinition command, string? usagePrefix = null)
    {
        var forms = usagePrefix is null
            ? command.UsageForms
            : command.UsageForms
                .Where(form => form == usagePrefix || form.StartsWith($"{usagePrefix} ", StringComparison.Ordinal))
                .ToArray();

        foreach (var (form, index) in forms.Select((form, index) => (form, index)))
        {
            writer.WriteLine($"{(index == 0 ? "Usage:" : "      ")} {Tool.Name} {form}");
        }
    }

    private static string FormatStatus(ImplementationStatus status) => status switch
    {
        ImplementationStatus.Implemented => "implemented",
        ImplementationStatus.NotImplemented => "not implemented",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };
}