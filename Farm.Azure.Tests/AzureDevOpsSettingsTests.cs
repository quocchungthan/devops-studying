using Xunit;

namespace Farm.Azure.Tests;

public sealed class AzureDevOpsSettingsTests
{
    [Theory]
    [InlineData("http://dev.azure.com/example")]
    [InlineData("relative")]
    public void Validate_rejects_non_https_organization_urls(string url)
    {
        var settings = CreateSettings(new Uri(url, UriKind.RelativeOrAbsolute));

        var exception = Assert.Throws<InvalidOperationException>(settings.Validate);

        Assert.Contains("HTTPS", exception.Message);
    }

    [Fact]
    public void Validate_accepts_https_organization_url()
    {
        var settings = CreateSettings(new Uri("https://dev.azure.com/example"));

        settings.Validate();
    }

    [Fact]
    public void TerminalStates_default_to_standard_completed_states()
    {
        var settings = CreateSettings(new Uri("https://dev.azure.com/example"));

        Assert.Equal(["Done", "Closed", "Removed"], settings.TerminalStates);
    }

    [Fact]
    public void Validate_rejects_empty_terminal_states()
    {
        var settings = new AzureDevOpsSettings
        {
            OrganizationUrl = new Uri("https://dev.azure.com/example"),
            Project = "Project",
            PersonalAccessToken = "token",
            TerminalStates = []
        };

        var exception = Assert.Throws<InvalidOperationException>(settings.Validate);

        Assert.Contains("terminal states", exception.Message);
    }

    private static AzureDevOpsSettings CreateSettings(Uri organizationUrl) => new()
    {
        OrganizationUrl = organizationUrl,
        Project = "Project",
        PersonalAccessToken = "token"
    };
}