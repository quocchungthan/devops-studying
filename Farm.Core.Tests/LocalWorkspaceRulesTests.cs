using Farm.Core.Workspaces;
using Xunit;

namespace Farm.Core.Tests;

public sealed class LocalWorkspaceRulesTests
{
    [Fact]
    public void GetWorkspaceDirectory_returns_a_child_of_the_absolute_output_root()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "farm-core-tests"));

        var result = LocalWorkspaceRules.GetWorkspaceDirectory(root, "sprint_42-export");

        Assert.Equal(Path.Combine(root, "sprint_42-export"), result);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("release/../../outside")]
    [InlineData("release\\outside")]
    [InlineData("release name")]
    [InlineData("release.")]
    [InlineData("CON")]
    [InlineData("prn")]
    [InlineData("Aux")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("com9")]
    [InlineData("COM\u00B9")]
    [InlineData("com\u00B2")]
    [InlineData("LPT1")]
    [InlineData("lpt9")]
    [InlineData("LPT\u00B3")]
    [InlineData("CON.txt")]
    public void GetWorkspaceDirectory_rejects_unsafe_workspace_names(string workspaceName)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "farm-core-tests"));

        var exception = Assert.Throws<ArgumentException>(() =>
            LocalWorkspaceRules.GetWorkspaceDirectory(root, workspaceName));

        Assert.Equal("workspaceName", exception.ParamName);
    }

    [Fact]
    public void EnsureWorkspaceDirectory_creates_the_workspace_and_manifest_stays_inside_it()
    {
        var root = Path.Combine(Path.GetTempPath(), $"farm-core-tests-{Guid.NewGuid():N}");

        try
        {
            var directory = LocalWorkspaceRules.EnsureWorkspaceDirectory(root, "release-1");
            var manifestPath = LocalWorkspaceRules.GetManifestPath(root, "release-1");

            Assert.True(Directory.Exists(directory));
            Assert.Equal(Path.Combine(directory, "manifest.json"), manifestPath);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}