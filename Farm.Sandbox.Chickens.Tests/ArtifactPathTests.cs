using Xunit;

namespace Farm.Sandbox.Chickens.Tests;

public sealed class ArtifactPathTests
{
    [Theory]
    [InlineData("repo", "repo")]
    [InlineData("..", "__")]
    [InlineData("repo.name", "repo_name")]
    [InlineData("repo/name", "repo_name")]
    [InlineData("repo\\name", "repo_name")]
    [InlineData("repo-名", "repo-_")]
    public void Repository_name_is_normalized_and_confined(string repositoryName, string expectedSegment)
    {
        var root = Path.Combine(Path.GetTempPath(), $"artifact-path-{Guid.NewGuid():N}");

        var result = ChickenRunner.BuildArtifactDirectory(root, repositoryName, 42, new string('a', 64));

        Assert.Equal(expectedSegment, new DirectoryInfo(result).Parent!.Parent!.Name);
        Assert.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, result, StringComparison.OrdinalIgnoreCase);
    }
}