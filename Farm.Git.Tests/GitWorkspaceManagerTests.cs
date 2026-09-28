using System.Diagnostics;
using Farm.Core.Chickens;
using Xunit;

namespace Farm.Git.Tests;

public sealed class GitWorkspaceManagerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"farm-git-{Guid.NewGuid():N}");

    [Fact]
    public async Task Prepare_creates_isolated_worktree_and_patch_includes_changes()
    {
        var origin = Path.Combine(root, "origin.git");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(root);
        Run(root, "init", "--bare", origin);
        Run(root, "clone", origin, repository);
        Run(repository, "config", "user.email", "test@example.com");
        Run(repository, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repository, "file.txt"), "before\n");
        Run(repository, "add", ".");
        Run(repository, "commit", "-m", "initial");
        Run(repository, "push", "origin", "HEAD:main");
        var sha = Run(repository, "rev-parse", "HEAD").Trim();

        var manager = new GitWorkspaceManager(new GitWorkspaceOptions
        {
            RepositoryPath = repository,
            CachePath = Path.Combine(root, "cache"),
            WorktreesPath = Path.Combine(root, "worktrees"),
            BaseBranch = "main",
            UserName = "Farm Chickens",
            UserEmail = "farm-chickens@example.invalid",
            AllowFileRepositoryUrls = true
        });
        var workspace = await manager.PrepareAsync(Candidate(sha, new Uri(origin)), Fingerprint("a"));
        await File.WriteAllTextAsync(Path.Combine(workspace.WorktreePath, "file.txt"), "after\n");

        var patch = await manager.CreatePatchAsync(workspace);

        Assert.Contains("-before", patch);
        Assert.Contains("+after", patch);
        Assert.False(File.Exists(Path.Combine(repository, ".git", "FETCH_HEAD")));
        Assert.Equal("Farm Chickens", Run(workspace.RepositoryPath, "config", "--local", "user.name").Trim());
        Assert.Equal("farm-chickens@example.invalid", Run(workspace.RepositoryPath, "config", "--local", "user.email").Trim());
        Assert.Equal("Test", Run(repository, "config", "--local", "user.name").Trim());
        Assert.Equal("test@example.com", Run(repository, "config", "--local", "user.email").Trim());
        await manager.CleanupAsync(workspace);
        Assert.False(Directory.Exists(workspace.WorktreePath));
        await manager.CleanupAsync(workspace);
    }

    [Fact]
    public async Task Prepare_succeeds_when_cache_repository_has_dirty_uncommitted_changes()
    {
        var origin = Path.Combine(root, "origin.git");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(root);
        Run(root, "init", "--bare", origin);
        Run(root, "clone", origin, repository);
        ConfigureUser(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "file.txt"), "before\n");
        Run(repository, "add", ".");
        Run(repository, "commit", "-m", "initial");
        Run(repository, "push", "origin", "HEAD:main");
        var sha = Run(repository, "rev-parse", "HEAD").Trim();
        var candidate = Candidate(sha, new Uri(origin));

        var cachePath = Path.Combine(root, "cache");
        var cachedRepositoryPath = Path.Combine(cachePath, candidate.RepositoryId);
        Directory.CreateDirectory(cachePath);
        Run(cachePath, "clone", "--branch", "main", origin, cachedRepositoryPath);
        ConfigureUser(cachedRepositoryPath);
        await File.WriteAllTextAsync(Path.Combine(cachedRepositoryPath, "file.txt"), "left over dirty edit\n");
        await File.WriteAllTextAsync(Path.Combine(cachedRepositoryPath, "untracked.txt"), "left over untracked\n");

        var manager = Manager(repository);
        var workspace = await manager.PrepareAsync(candidate, Fingerprint("a"));

        Assert.True((await manager.VerifyReadyAsync(workspace)).Succeeded);
        Assert.Equal(string.Empty, Run(cachedRepositoryPath, "status", "--porcelain").Trim());
        await manager.CleanupAsync(workspace);
    }

    [Fact]
    public async Task Prepare_recovers_owned_stale_worktree_for_same_attempt()
    {
        var origin = Path.Combine(root, "origin.git");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(root);
        Run(root, "init", "--bare", origin);
        Run(root, "clone", origin, repository);
        Run(repository, "config", "user.email", "test@example.com");
        Run(repository, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repository, "file.txt"), "base\n");
        Run(repository, "add", ".");
        Run(repository, "commit", "-m", "base");
        Run(repository, "push", "origin", "HEAD:main");
        var sha = Run(repository, "rev-parse", "HEAD").Trim();
        var manager = Manager(repository);
        var candidate = Candidate(sha, new Uri(origin));
        var fingerprint = Fingerprint("c");
        var stale = await manager.PrepareAsync(candidate, fingerprint);
        await File.WriteAllTextAsync(Path.Combine(stale.WorktreePath, "stale.txt"), "stale");

        var recovered = await manager.PrepareAsync(candidate, fingerprint);

        Assert.Equal(stale.WorktreePath, recovered.WorktreePath);
        Assert.False(File.Exists(Path.Combine(recovered.WorktreePath, "stale.txt")));
        await manager.CleanupAsync(recovered);
    }

    [Fact]
    public async Task Prepare_rebases_onto_latest_fetched_base_branch_commit()
    {
        var origin = Path.Combine(root, "origin.git");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(root);
        Run(root, "init", "--bare", origin);
        Run(root, "clone", origin, repository);
        Run(repository, "config", "user.email", "test@example.com");
        Run(repository, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repository, "file.txt"), "base\n");
        Run(repository, "add", ".");
        Run(repository, "commit", "-m", "base");
        Run(repository, "branch", "-M", "main");
        Run(repository, "push", "origin", "main");
        var targetSha = Run(repository, "rev-parse", "HEAD").Trim();
        Run(repository, "checkout", "-b", "feature");
        await File.WriteAllTextAsync(Path.Combine(repository, "feature.txt"), "feature\n");
        Run(repository, "add", ".");
        Run(repository, "commit", "-m", "feature");
        Run(repository, "push", "origin", "feature");
        var headSha = Run(repository, "rev-parse", "HEAD").Trim();
        Run(repository, "checkout", "main");
        await File.WriteAllTextAsync(Path.Combine(repository, "main.txt"), "moved\n");
        Run(repository, "add", ".");
        Run(repository, "commit", "-m", "move target");
        Run(repository, "push", "origin", "main");

        var manager = Manager(repository);
        var workspace = await manager.PrepareAsync(
            Candidate(headSha, new Uri(origin), targetSha, "refs/heads/feature"), Fingerprint("b"));

        Assert.Equal(Run(repository, "rev-parse", "main").Trim(), workspace.BaseSha);
        Assert.Equal("feature", Run(workspace.WorktreePath, "show", "HEAD:feature.txt").Trim());
        Assert.True((await manager.VerifyReadyAsync(workspace)).Succeeded);
        await manager.CleanupAsync(workspace);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("000000000000000000000000000000000000000g")]
    public async Task Prepare_rejects_non_full_hex_sha(string sha)
    {
        var manager = new GitWorkspaceManager(new GitWorkspaceOptions
        {
            RepositoryPath = root,
            CachePath = Path.Combine(root, "cache"),
            WorktreesPath = Path.Combine(root, "worktrees"),
            BaseBranch = "main"
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PrepareAsync(
            Candidate(sha, new Uri("https://example.test/repo")), Fingerprint("a")));
    }

    [Fact]
    public async Task Prepare_rejects_non_full_target_sha()
    {
        var manager = Manager(root);

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PrepareAsync(
            Candidate(new string('a', 40), new Uri("https://example.test/repo"), "abc"), Fingerprint("a")));
    }

    [Theory]
    [InlineData("../main")]
    [InlineData("refs/heads/main")]
    [InlineData(".main")]
    [InlineData("feature/.hidden")]
    [InlineData("main.lock")]
    public async Task Prepare_rejects_unsafe_base_branch(string baseBranch)
    {
        var manager = new GitWorkspaceManager(new GitWorkspaceOptions
        {
            RepositoryPath = root,
            CachePath = Path.Combine(root, "cache"),
            WorktreesPath = Path.Combine(root, "worktrees"),
            BaseBranch = baseBranch
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PrepareAsync(
            Candidate(new string('a', 40), new Uri("https://example.test/repo")), Fingerprint("a")));

        Assert.Contains("base branch", exception.Message);
    }

    [Fact]
    public async Task Conflicted_rebase_is_handed_to_agent_and_can_be_continued()
    {
        var fixture = await CreateFeatureFixtureAsync(conflict: true);
        var manager = Manager(fixture.Repository);

        var workspace = await manager.PrepareAsync(fixture.Candidate, Fingerprint("d"));

        Assert.True(workspace.HasRebaseConflicts);
        Assert.False((await manager.VerifyReadyAsync(workspace)).Succeeded);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorktreePath, "file.txt"), "base\nmain\nfeature\n");
        Run(workspace.WorktreePath, "add", "file.txt");
        Run(workspace.WorktreePath, "-c", "core.editor=true", "rebase", "--continue");
        Assert.True((await manager.VerifyReadyAsync(workspace)).Succeeded);
        await manager.CleanupAsync(workspace);
    }

    [Fact]
    public async Task Unresolved_conflict_prevents_push()
    {
        var fixture = await CreateFeatureFixtureAsync(conflict: true);
        var manager = Manager(fixture.Repository, enablePush: true);
        var workspace = await manager.PrepareAsync(fixture.Candidate, Fingerprint("e"));

        var publication = await manager.CommitAndPushAsync(workspace, fixture.Candidate, Fingerprint("e"));

        Assert.False(publication.Succeeded);
        Assert.False(publication.Pushed);
        Assert.Equal(fixture.Candidate.HeadSha, Run(fixture.Origin, "rev-parse", fixture.Candidate.SourceRef).Trim());
        await manager.CleanupAsync(workspace);
    }

    [Fact]
    public async Task Commit_and_push_uses_exact_force_with_lease()
    {
        var fixture = await CreateFeatureFixtureAsync();
        var manager = Manager(fixture.Repository, enablePush: true);
        var fingerprint = Fingerprint("f");
        var workspace = await manager.PrepareAsync(fixture.Candidate, fingerprint);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorktreePath, "review-fix.txt"), "fixed\n");

        var publication = await manager.CommitAndPushAsync(workspace, fixture.Candidate, fingerprint);

        Assert.True(publication.Succeeded);
        Assert.True(publication.Pushed);
        Assert.Equal(publication.CommitSha, Run(fixture.Origin, "rev-parse", fixture.Candidate.SourceRef).Trim());
        Assert.Contains("PR 1 feedback ffffffffffff", Run(workspace.WorktreePath, "log", "-1", "--format=%s"));
        await manager.CleanupAsync(workspace);
    }

    [Fact]
    public async Task Fork_fetches_base_from_target_and_pushes_source_remote()
    {
        var target = Path.Combine(root, "target.git");
        var source = Path.Combine(root, "source.git");
        var repository = Path.Combine(root, "repository");
        var forkWork = Path.Combine(root, "fork-work");
        Directory.CreateDirectory(root);
        Run(root, "init", "--bare", target);
        Run(root, "init", "--bare", source);
        Run(root, "clone", target, repository);
        ConfigureUser(repository);
        await File.WriteAllTextAsync(Path.Combine(repository, "base.txt"), "base\n");
        Run(repository, "add", ".");
        Run(repository, "commit", "-m", "base");
        Run(repository, "branch", "-M", "main");
        Run(repository, "push", "origin", "main");
        var targetSha = Run(repository, "rev-parse", "HEAD").Trim();
        Run(root, "clone", target, forkWork);
        ConfigureUser(forkWork);
        Run(forkWork, "checkout", "-b", "feature", "origin/main");
        await File.WriteAllTextAsync(Path.Combine(forkWork, "feature.txt"), "feature\n");
        Run(forkWork, "add", ".");
        Run(forkWork, "commit", "-m", "feature");
        var headSha = Run(forkWork, "rev-parse", "HEAD").Trim();
        Run(forkWork, "push", source, "feature");
        await File.WriteAllTextAsync(Path.Combine(repository, "target.txt"), "target moved\n");
        Run(repository, "add", ".");
        Run(repository, "commit", "-m", "advance target");
        Run(repository, "push", "origin", "main");
        var latestTargetSha = Run(repository, "rev-parse", "HEAD").Trim();
        var candidate = Candidate(headSha, new Uri(target), targetSha, "refs/heads/feature") with
        {
            SourceRepositoryId = "fork-repo",
            SourceRepositoryName = "fork",
            SourceRepositoryUrl = new Uri(source)
        };
        var manager = Manager(repository, enablePush: true);
        var fingerprint = Fingerprint("3");

        var workspace = await manager.PrepareAsync(candidate, fingerprint);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorktreePath, "review-fix.txt"), "fixed\n");
        var publication = await manager.CommitAndPushAsync(workspace, candidate, fingerprint);

        Assert.Equal(new Uri(target).AbsoluteUri, Run(workspace.RepositoryPath, "remote", "get-url", "origin").Trim());
        Assert.Equal(new Uri(source).AbsoluteUri, Run(workspace.RepositoryPath, "remote", "get-url", "chickens-source").Trim());
        Assert.Equal(latestTargetSha, workspace.BaseSha);
        Assert.True(publication.Succeeded);
        Assert.Equal(publication.CommitSha, Run(source, "rev-parse", "refs/heads/feature").Trim());
        Assert.Equal(1, RunExitCode(target, "show-ref", "--verify", "--quiet", "refs/heads/feature"));
        await manager.CleanupAsync(workspace);
    }

    [Fact]
    public async Task Missing_fork_clone_url_is_deferred_before_workspace_creation()
    {
        var fixture = await CreateFeatureFixtureAsync();
        var candidate = fixture.Candidate with
        {
            SourceRepositoryId = "fork-repo",
            SourceRepositoryName = "fork",
            SourceRepositoryUrl = null
        };

        var exception = await Assert.ThrowsAsync<ReviewDeferredException>(() =>
            Manager(fixture.Repository, enablePush: true).PrepareAsync(candidate, Fingerprint("4")));

        Assert.Contains("no clone URL", exception.Message);
    }

    [Fact]
    public async Task Retry_with_unchanged_base_and_source_produces_same_commit_sha()
    {
        var fixture = await CreateFeatureFixtureAsync();
        var manager = Manager(fixture.Repository, enablePush: true);
        var fingerprint = Fingerprint("5");
        var feedbackDate = new DateTimeOffset(2026, 9, 23, 12, 34, 56, TimeSpan.Zero);
        var candidate = fixture.Candidate with
        {
            Threads = [new FeedbackThread(1, false, null,
                [new FeedbackComment(1, "reviewer", "Reviewer", "fix", feedbackDate)])]
        };
        var firstWorkspace = await manager.PrepareAsync(candidate, fingerprint);
        await File.WriteAllTextAsync(Path.Combine(firstWorkspace.WorktreePath, "review-fix.txt"), "fixed\n");
        var first = await manager.CommitAndPushAsync(firstWorkspace, candidate, fingerprint);
        Assert.Equal("2026-09-23T12:34:56Z 2026-09-23T12:34:56Z",
            Run(firstWorkspace.WorktreePath, "show", "-s", "--format=%aI %cI", "HEAD").Trim());
        await manager.CleanupAsync(firstWorkspace);
        Run(fixture.Repository, "push", "--force", fixture.Origin,
            $"{candidate.HeadSha}:{candidate.SourceRef}");

        var retryWorkspace = await manager.PrepareAsync(candidate, fingerprint);
        await File.WriteAllTextAsync(Path.Combine(retryWorkspace.WorktreePath, "review-fix.txt"), "fixed\n");
        var retry = await manager.CommitAndPushAsync(retryWorkspace, candidate, fingerprint);

        Assert.True(first.Succeeded);
        Assert.True(retry.Succeeded);
        Assert.Equal(first.CommitSha, retry.CommitSha);
        await manager.CleanupAsync(retryWorkspace);
    }

    [Fact]
    public async Task Remote_movement_rejects_force_with_lease_without_overwrite()
    {
        var fixture = await CreateFeatureFixtureAsync();
        var manager = Manager(fixture.Repository, enablePush: true);
        var fingerprint = Fingerprint("1");
        var workspace = await manager.PrepareAsync(fixture.Candidate, fingerprint);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorktreePath, "review-fix.txt"), "fixed\n");
        var mover = Path.Combine(root, "mover");
        Run(root, "clone", fixture.Origin, mover);
        Run(mover, "config", "user.email", "mover@example.com");
        Run(mover, "config", "user.name", "Mover");
        Run(mover, "checkout", "feature");
        await File.WriteAllTextAsync(Path.Combine(mover, "remote.txt"), "moved\n");
        Run(mover, "add", ".");
        Run(mover, "commit", "-m", "remote move");
        Run(mover, "push", "origin", "feature");
        var movedSha = Run(mover, "rev-parse", "HEAD").Trim();

        var publication = await manager.CommitAndPushAsync(workspace, fixture.Candidate, fingerprint);

        Assert.False(publication.Succeeded);
        Assert.False(publication.Pushed);
        Assert.NotNull(publication.CommitSha);
        Assert.Equal(movedSha, Run(fixture.Origin, "rev-parse", fixture.Candidate.SourceRef).Trim());
        await manager.CleanupAsync(workspace);
    }

    [Fact]
    public async Task Push_disabled_does_not_commit_or_change_remote()
    {
        var fixture = await CreateFeatureFixtureAsync();
        var manager = Manager(fixture.Repository);
        var fingerprint = Fingerprint("2");
        var workspace = await manager.PrepareAsync(fixture.Candidate, fingerprint);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorktreePath, "review-fix.txt"), "fixed\n");
        var before = Run(workspace.WorktreePath, "rev-parse", "HEAD").Trim();

        var publication = await manager.CommitAndPushAsync(workspace, fixture.Candidate, fingerprint);

        Assert.False(publication.Succeeded);
        Assert.False(publication.Pushed);
        Assert.Null(publication.CommitSha);
        Assert.Equal(before, Run(workspace.WorktreePath, "rev-parse", "HEAD").Trim());
        Assert.Equal(fixture.Candidate.HeadSha, Run(fixture.Origin, "rev-parse", fixture.Candidate.SourceRef).Trim());
        await manager.CleanupAsync(workspace);
    }

    [Fact]
    public async Task Sensitive_staged_content_is_not_committed_or_pushed()
    {
        const string secret = "configured-publication-secret";
        var fixture = await CreateFeatureFixtureAsync();
        var manager = new GitWorkspaceManager(new GitWorkspaceOptions
        {
            RepositoryPath = fixture.Repository,
            CachePath = Path.Combine(root, "cache"),
            WorktreesPath = Path.Combine(root, "worktrees"),
            BaseBranch = "main",
            UserName = "Farm Chickens",
            UserEmail = "farm-chickens@example.invalid",
            EnablePush = true,
            AllowFileRepositoryUrls = true,
            Redactor = new SensitiveDataRedactor([secret]),
            ContentScanner = new SensitiveContentScanner([secret])
        });
        var workspace = await manager.PrepareAsync(fixture.Candidate, Fingerprint("6"));
        await File.WriteAllTextAsync(Path.Combine(workspace.WorktreePath, "leak.txt"), secret);

        var publication = await manager.CommitAndPushAsync(workspace, fixture.Candidate, Fingerprint("6"));

        Assert.False(publication.Succeeded);
        Assert.False(publication.Pushed);
        Assert.Null(publication.CommitSha);
        Assert.Contains("configured secret", publication.Summary);
        Assert.Equal(fixture.Candidate.HeadSha, Run(fixture.Origin, "rev-parse", fixture.Candidate.SourceRef).Trim());
        await manager.CleanupAsync(workspace);
    }

    [Fact]
    public async Task Validation_redacts_secret_arguments_and_process_output()
    {
        const string secret = "validation-secret-sentinel";
        Directory.CreateDirectory(root);
        var manager = new GitWorkspaceManager(new GitWorkspaceOptions
        {
            RepositoryPath = root,
            CachePath = Path.Combine(root, "cache"),
            WorktreesPath = Path.Combine(root, "worktrees"),
            BaseBranch = "main",
            Redactor = new SensitiveDataRedactor([secret]),
            ValidationCommands = [new GitValidationCommand(
                "git", ["-c", $"alias.leak=!echo {secret}", "leak"])]
        });
        var workspace = new RepositoryWorkspace(root, root, "chickens/test", "refs/heads/main", new string('a', 40), new string('a', 40));

        var validation = await manager.ValidateAsync(workspace);

        Assert.True(validation.Succeeded);
        Assert.DoesNotContain(secret, validation.Output);
        Assert.Contains("[REDACTED]", validation.Output);
    }

    [Fact]
    public async Task Validation_does_not_inherit_broker_credentials()
    {
        string[] credentialNames =
        [
            "FARM_AZURE_DEVOPS_PAT", "FARM_CHICKENS_GIT_AUTH_TOKEN", "COPILOT_TOKEN", "GITHUB_TOKEN", "GH_TOKEN",
            "GIT_AUTH_TOKEN", "GIT_ASKPASS", "SSH_ASKPASS", "NUGET_AUTH_TOKEN", "HTTPS_PROXY"
        ];
        Directory.CreateDirectory(root);
        var previous = credentialNames.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var credentialName in credentialNames)
            {
                Environment.SetEnvironmentVariable(
                    credentialName,
                    credentialName == "HTTPS_PROXY"
                        ? string.Concat("https://user:", "credential-sentinel", "@", "example.test")
                        : "credential-sentinel");
            }

            var checks = string.Join(
                OperatingSystem.IsWindows() ? " " : " && ",
                credentialNames.Select(name => OperatingSystem.IsWindows()
                    ? $"if defined {name} exit 1"
                    : $"test -z \"${name}\""));
            var command = OperatingSystem.IsWindows()
                ? new GitValidationCommand("cmd.exe", ["/d", "/c", checks])
                : new GitValidationCommand("/bin/sh", ["-c", checks]);
            var manager = new GitWorkspaceManager(new GitWorkspaceOptions
            {
                RepositoryPath = root,
                CachePath = Path.Combine(root, "cache"),
                WorktreesPath = Path.Combine(root, "worktrees"),
                BaseBranch = "main",
                ValidationCommands = [command]
            });
            var workspace = new RepositoryWorkspace(root, root, "chickens/test", "refs/heads/main", new string('a', 40), new string('a', 40));

            var validation = await manager.ValidateAsync(workspace);

            Assert.True(validation.Succeeded, validation.Output);
        }
        finally
        {
            foreach (var credential in previous)
            {
                Environment.SetEnvironmentVariable(credential.Key, credential.Value);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(root, true);
        }
    }

    private GitWorkspaceManager Manager(string repositoryPath, bool enablePush = false) => new(new GitWorkspaceOptions
    {
        RepositoryPath = repositoryPath,
        CachePath = Path.Combine(root, "cache"),
        WorktreesPath = Path.Combine(root, "worktrees"),
        BaseBranch = "main",
        UserName = "Farm Chickens",
        UserEmail = "farm-chickens@example.invalid",
        EnablePush = enablePush,
        AllowFileRepositoryUrls = true
    });

    private async Task<RepositoryFixture> CreateFeatureFixtureAsync(bool conflict = false)
    {
        var origin = Path.Combine(root, "origin.git");
        var repository = Path.Combine(root, "repository");
        Directory.CreateDirectory(root);
        Run(root, "init", "--bare", origin);
        Run(root, "clone", origin, repository);
        Run(repository, "config", "user.email", "test@example.com");
        Run(repository, "config", "user.name", "Test");
        await File.WriteAllTextAsync(Path.Combine(repository, "file.txt"), "base\n");
        Run(repository, "add", ".");
        Run(repository, "commit", "-m", "base");
        Run(repository, "branch", "-M", "main");
        Run(repository, "push", "origin", "main");
        var targetSha = Run(repository, "rev-parse", "HEAD").Trim();
        Run(repository, "checkout", "-b", "feature");
        await File.WriteAllTextAsync(
            Path.Combine(repository, conflict ? "file.txt" : "feature.txt"), conflict ? "base\nfeature\n" : "feature\n");
        Run(repository, "add", ".");
        Run(repository, "commit", "-m", "feature");
        Run(repository, "push", "origin", "feature");
        var headSha = Run(repository, "rev-parse", "HEAD").Trim();
        Run(repository, "checkout", "main");
        await File.WriteAllTextAsync(
            Path.Combine(repository, conflict ? "file.txt" : "main.txt"), conflict ? "base\nmain\n" : "main\n");
        Run(repository, "add", ".");
        Run(repository, "commit", "-m", "advance main");
        Run(repository, "push", "origin", "main");
        return new RepositoryFixture(
            origin,
            repository,
            Candidate(headSha, new Uri(origin), targetSha, "refs/heads/feature"));
    }

    private sealed record RepositoryFixture(string Origin, string Repository, ReviewCandidate Candidate);

    private static ReviewCandidate Candidate(
        string sha,
        Uri repositoryUrl,
        string? targetSha = null,
        string sourceRef = "refs/heads/main") => new(
        "org", "project", "repo", "repo", repositoryUrl, 1, "PR",
        sourceRef, "refs/heads/main", sha, "author", [], [], targetSha ?? sha);

    private static FeedbackFingerprint Fingerprint(string hexCharacter) => new(new string(hexCharacter[0], 64));

    private static void ConfigureUser(string repository)
    {
        Run(repository, "config", "user.email", "test@example.com");
        Run(repository, "config", "user.name", "Test");
    }

    private static int RunExitCode(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static string Run(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output);
        return output;
    }
}