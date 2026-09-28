namespace Farm.Azure;

public interface IAzureDevOpsClient
{
	Task<IReadOnlyList<AzureWorkItemDto>> GetWorkItemsAsync(
		IEnumerable<int> ids,
		CancellationToken cancellationToken = default);
}