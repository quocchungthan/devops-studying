using Farm.Core.Chickens;
using Farm.Core.Contracts;

namespace Farm.Azure;

public interface IAzureWorkItemCommentsClient
{
    Task<IReadOnlyList<WorkItemComment>> GetWorkItemCommentsAsync(
        int id,
        CancellationToken cancellationToken = default);
}

public sealed class AzureWorkItemCommentsSource(IAzureWorkItemCommentsClient client) : IWorkItemCommentsSource
{
    public Task<IReadOnlyList<WorkItemComment>> ListCommentsAsync(
        int workItemId,
        CancellationToken cancellationToken = default)
    {
        if (workItemId <= 0)
        {
            throw new ArgumentException("Work item ID must be positive.", nameof(workItemId));
        }

        return client.GetWorkItemCommentsAsync(workItemId, cancellationToken);
    }
}
