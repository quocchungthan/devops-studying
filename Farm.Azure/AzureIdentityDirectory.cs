using Farm.Core.Contracts;
using CoreGroup = Farm.Core.Domain.Group;
using CoreIdentity = Farm.Core.Domain.Identity;

namespace Farm.Azure;

public interface IAzureIdentityClient
{
    Task<CoreIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CoreGroup>> GetGroupsForCurrentUserAsync(CancellationToken cancellationToken = default);
}

public sealed class AzureIdentityDirectory(IAzureIdentityClient client) : IIdentityDirectory
{
    public Task<CoreIdentity> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        client.GetCurrentUserAsync(cancellationToken);

    public Task<IReadOnlyList<CoreGroup>> GetGroupsForCurrentUserAsync(CancellationToken cancellationToken = default) =>
        client.GetGroupsForCurrentUserAsync(cancellationToken);
}
