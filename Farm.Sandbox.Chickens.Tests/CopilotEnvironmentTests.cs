using Farm.Copilot;
using Xunit;

namespace Farm.Sandbox.Chickens.Tests;

public sealed class CopilotEnvironmentTests
{
    [Fact]
    public void Runtime_environment_excludes_credentials_and_session_receives_explicit_token()
    {
        const string sentinel = "copilot-runtime-secret-sentinel";
        string[] credentialNames =
        [
            "FARM_AZURE_DEVOPS_PAT", "FARM_CHICKENS_GIT_AUTH_TOKEN", "COPILOT_TOKEN", "GITHUB_TOKEN", "GH_TOKEN",
            "GIT_AUTH_TOKEN", "GIT_ASKPASS", "SSH_ASKPASS", "NUGET_AUTH_TOKEN", "HTTPS_PROXY"
        ];
        var previous = credentialNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in credentialNames)
            {
                Environment.SetEnvironmentVariable(name, sentinel);
            }

            var brain = new CopilotReviewBrain(new CopilotReviewOptions
            {
                SafeProcessEnvironment = new Dictionary<string, string>
                {
                    ["FEATURE_FLAG"] = "enabled",
                    ["GITHUB_TOKEN"] = sentinel,
                    ["HTTPS_PROXY"] = string.Concat("https://user:", sentinel, "@", "example.test")
                }
            });
            var client = brain.CreateClientOptions(Path.GetTempPath());
            var session = brain.BuildSessionConfig(Path.GetTempPath(), "purpose");

            Assert.DoesNotContain(client.Environment!, variable => credentialNames.Contains(variable.Key, StringComparer.OrdinalIgnoreCase));
            Assert.DoesNotContain("HTTPS_PROXY", client.Environment!.Keys, StringComparer.OrdinalIgnoreCase);
            Assert.Equal("enabled", client.Environment!["FEATURE_FLAG"]);
            Assert.Null(client.GitHubToken);
            Assert.True(client.UseLoggedInUser);
            Assert.StartsWith("farm-chicken-", session.SessionId);
        }
        finally
        {
            foreach (var variable in previous)
            {
                Environment.SetEnvironmentVariable(variable.Key, variable.Value);
            }
        }
    }
}