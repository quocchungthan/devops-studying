using System.Reflection;
using Microsoft.VisualStudio.Services.Profile;
using Xunit;

namespace Farm.Azure.Tests;

public sealed class AzureIdentityMapperTests
{
    [Fact]
    public void ToDomain_maps_profile_fields()
    {
        var profile = CreateProfile(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            "Person Example",
            "person@example.com");

        var result = AzureIdentityMapper.ToDomain(profile);

        Assert.Equal("11111111-1111-1111-1111-111111111111", result.Id);
        Assert.Equal("Person Example", result.DisplayName);
        Assert.Equal("person@example.com", result.UniqueName);
    }

    [Fact]
    public void ToGroup_maps_graph_group_fields_with_no_members()
    {
        var group = Newtonsoft.Json.JsonConvert.DeserializeObject<Microsoft.VisualStudio.Services.Graph.Client.GraphGroup>(
            "{\"subjectKind\":\"group\",\"descriptor\":\"vssgp.abc\",\"displayName\":\"[Project]\\\\Contributors\"}")!;

        var result = AzureIdentityMapper.ToGroup(group);

        Assert.Equal(group.Descriptor.ToString(), result.Id);
        Assert.Equal("[Project]\\Contributors", result.DisplayName);
        Assert.Empty(result.Members);
    }

    // Profile.DisplayName/EmailAddress are read from an internal CoreAttributes dictionary rather than
    // simple settable properties, so tests populate it via reflection to mirror what GetProfileAsync returns.
    private static Profile CreateProfile(Guid id, string displayName, string email)
    {
        var profile = new Profile();
        typeof(Profile).GetField("<Id>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(profile, id);
        var coreAttributes = new Dictionary<string, CoreProfileAttribute>
        {
            ["DisplayName"] = new() { Value = displayName },
            ["EmailAddress"] = new() { Value = email }
        };
        typeof(Profile).GetProperty("CoreAttributes", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(profile, coreAttributes);
        return profile;
    }
}


