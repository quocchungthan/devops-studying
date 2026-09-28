using System.Net;
using System.Text.RegularExpressions;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.WebApi;
using AzureWorkItem = Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models.WorkItem;
using CoreAttachment = Farm.Core.Domain.Attachment;
using CoreIdentity = Farm.Core.Domain.Identity;
using CoreWorkItem = Farm.Core.Domain.WorkItem;
using CoreWorkItemRelation = Farm.Core.Domain.WorkItemRelation;

namespace Farm.Azure;

public static partial class AzureWorkItemMapper
{
    public static AzureWorkItemDto ToDto(AzureWorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        return new AzureWorkItemDto(
            workItem.Id ?? 0,
            workItem.Rev ?? 0,
            GetString(workItem, "System.Title"),
            GetString(workItem, "System.State"),
            GetString(workItem, "System.WorkItemType"),
            GetString(workItem, "System.AssignedTo"),
            workItem.Url);
    }

    public static CoreWorkItem ToDomain(AzureWorkItem workItem)
    {
        ArgumentNullException.ThrowIfNull(workItem);

        var relations = workItem.Relations ?? [];

        return new CoreWorkItem(
            workItem.Id ?? 0,
            workItem.Rev ?? 0,
            GetString(workItem, "System.Title"),
            GetString(workItem, "System.State"),
            GetString(workItem, "System.WorkItemType"),
            GetIdentity(workItem, "System.AssignedTo"),
            CreateUri(workItem.Url),
            relations
                .Where(IsAttachment)
                .Select(ToAttachment)
                .Where(attachment => attachment is not null)
                .Cast<CoreAttachment>()
                .ToArray(),
            GetString(workItem, "System.Reason"),
            GetString(workItem, "System.AreaPath"),
            GetString(workItem, "System.IterationPath"),
            GetDate(workItem, "System.CreatedDate"),
            GetDate(workItem, "System.ChangedDate"),
            GetIdentity(workItem, "System.CreatedBy"),
            GetIdentity(workItem, "System.ChangedBy"),
            HtmlToText(GetString(workItem, "System.Description")),
            ParseTags(GetString(workItem, "System.Tags")),
            relations
                .Where(relation => !IsAttachment(relation))
                .Select(ToRelation)
                .Where(relation => relation is not null)
                .Cast<CoreWorkItemRelation>()
                .ToArray());
    }

    private static string? GetString(AzureWorkItem workItem, string fieldName) =>
        workItem.Fields.TryGetValue(fieldName, out var value) ? value?.ToString() : null;

    private static CoreIdentity? GetIdentity(AzureWorkItem workItem, string fieldName) =>
        workItem.Fields.TryGetValue(fieldName, out var value) && value is IdentityRef identity
            ? new CoreIdentity(identity.Id, identity.DisplayName, identity.UniqueName)
            : null;

    private static DateTimeOffset? GetDate(AzureWorkItem workItem, string fieldName)
    {
        if (!workItem.Fields.TryGetValue(fieldName, out var value))
        {
            return null;
        }

        return value switch
        {
            DateTimeOffset date => date,
            DateTime date => new DateTimeOffset(date),
            _ when DateTimeOffset.TryParse(value?.ToString(), out var date) => date,
            _ => null
        };
    }

    private static IReadOnlyList<string> ParseTags(string? tags) =>
        string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    internal static string? HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var withoutExecutableContent = ScriptAndStyleRegex().Replace(html, string.Empty);
        var withLineBreaks = BlockEndingRegex().Replace(withoutExecutableContent, "\n");
        var text = WebUtility.HtmlDecode(HtmlTagRegex().Replace(withLineBreaks, string.Empty));
        var lines = text
            .Split('\n')
            .Select(line => InlineWhitespaceRegex().Replace(line, " ").Trim())
            .Where(line => line.Length > 0);
        return string.Join(Environment.NewLine, lines);
    }

    private static bool IsAttachment(WorkItemRelation relation) =>
        string.Equals(relation.Rel, "AttachedFile", StringComparison.OrdinalIgnoreCase);

    private static CoreAttachment? ToAttachment(WorkItemRelation relation)
    {
        var url = CreateUri(relation.Url);
        if (url is null)
        {
            return null;
        }

        return new CoreAttachment(
            url.Segments.LastOrDefault()?.Trim('/') ?? relation.Url,
            GetAttribute(relation, "name") ?? "attachment",
            url,
            GetAttribute(relation, "comment"));
    }

    private static CoreWorkItemRelation? ToRelation(WorkItemRelation relation)
    {
        var url = CreateUri(relation.Url);
        return url is null
            ? null
            : new CoreWorkItemRelation(relation.Rel ?? "related", url, GetAttribute(relation, "name"));
    }

    private static string? GetAttribute(WorkItemRelation relation, string name) =>
        relation.Attributes is not null && relation.Attributes.TryGetValue(name, out var value)
            ? value?.ToString()
            : null;

    private static Uri? CreateUri(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptAndStyleRegex();

    [GeneratedRegex(@"<\s*(br\s*/?|/p|/div|/li|/h[1-6])\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEndingRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[\t\f\v ]+")]
    private static partial Regex InlineWhitespaceRegex();
}