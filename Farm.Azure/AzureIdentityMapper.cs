using AzureGroup = Microsoft.VisualStudio.Services.Graph.Client.GraphGroup;
using AzureProfile = Microsoft.VisualStudio.Services.Profile.Profile;
using CoreGroup = Farm.Core.Domain.Group;
using CoreIdentity = Farm.Core.Domain.Identity;

namespace Farm.Azure;

public static class AzureIdentityMapper
{
    public static CoreIdentity ToDomain(AzureProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new CoreIdentity(profile.Id.ToString(), profile.DisplayName, profile.EmailAddress);
    }

    public static CoreGroup ToGroup(AzureGroup group, string? legacyId = null)
    {
        ArgumentNullException.ThrowIfNull(group);

        return new CoreGroup(group.Descriptor.ToString(), group.DisplayName, [], legacyId);
    }
}

