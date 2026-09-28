namespace Farm.Azure;

public sealed record AzureWorkItemDto(
    int Id,
    int Revision,
    string? Title,
    string? State,
    string? WorkItemType,
    string? AssignedTo,
    string? Url);