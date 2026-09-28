using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Farm.Core.Chickens;

namespace Farm.Git;

public sealed class GitWorkspaceManager(GitWorkspaceOptions options) : IRepositoryWorkspaceManager
{
    private const string SourceRemoteName = "chickens-source";
    private static readonly Regex FullSha = new("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant);
    private static readonly Regex Sha256 = new("^[0-9a-fA-F]{64}$", RegexOptions.CultureInvariant);
    private static readonly Regex BranchRef = new("^refs/heads/[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant);
    private static readonly Regex BranchName = new("^[A-Za-z0-9][A-Za-z0-9._/-]*$", RegexOptions.CultureInvariant);

    public async Task<RepositoryWorkspace> PrepareAsync(
        ReviewCandidate candidate,
        FeedbackFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ValidateCandidate(candidate, options.AllowFileRepositoryUrls);
        ValidateBranchName(options.BaseBranch);
        if (!Sha256.IsMatch(fingerprint.Value))
        {
            throw new InvalidOperationException("Feedback fingerprint must be a 64-character hexadecimal SHA-256 value.");
        }
        var seedPath = Path.GetFullPath(options.RepositoryPath);
        if (!Directory.Exists(seedPath))
        {
            throw new DirectoryNotFoundException($"Mounted repository seed does not exist: {seedPath}");
        }

        var inside = await RunAsync(seedPath, "git", ["rev-parse", "--is-inside-work-tree"], cancellationToken);
        if (inside.ExitCode != 0 || !inside.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Mounted seed is not a Git work tree: {seedPath}{Environment.NewLine}{inside.Output}");
        }

        var cacheRoot = Path.GetFullPath(options.CachePath);
        Directory.CreateDirectory(cacheRoot);
        var repositoryPath = Path.Combine(cacheRoot, Sanitize(candidate.RepositoryId));
        if (!Directory.Exists(repositoryPath))
        {
            await RunRequiredAsync(cacheRoot, "git", ["clone", "--no-checkout", seedPath, repositoryPath], cancellationToken);
        }
        else
        {
            await SelfHealCacheRepositoryAsync(repositoryPath, candidate.RepositoryId, cancellationToken);
        }

        await ConfigureIdentityAsync(repositoryPath, cancellationToken);

        await RunRequiredAsync(repositoryPath, "git", ["remote", "set-url", options.RemoteName, candidate.RepositoryUrl.AbsoluteUri], cancellationToken);
        var sourceRepositoryUrl = ResolveSourceRepositoryUrl(candidate);
        var sourceRemote = await RunAsync(repositoryPath, "git", ["remote", "get-url", SourceRemoteName], cancellationToken);
        await RunRequiredAsync(
            repositoryPath,
            "git",
            sourceRemote.ExitCode == 0
                ? ["remote", "set-url", SourceRemoteName, sourceRepositoryUrl.AbsoluteUri]
                : ["remote", "add", SourceRemoteName, sourceRepositoryUrl.AbsoluteUri],
            cancellationToken);
        var sourceBranch = RemoveHeadsPrefix(candidate.SourceRef);
        var baseRemoteRef = $"refs/remotes/{options.RemoteName}/chickens-base";
        await RunRequiredAsync(
            repositoryPath,
            "git",
            ["fetch", "--prune", options.RemoteName,
                $"+refs/heads/{options.BaseBranch}:{baseRemoteRef}",
                candidate.TargetSha!],
            cancellationToken);
        var sourceRemoteRef = $"refs/remotes/{SourceRemoteName}/pr-{candidate.PullRequestId}-source";
        var sourceFetch = await RunAsync(
            repositoryPath,
            "git",
            ["fetch", "--prune", SourceRemoteName, $"+{candidate.SourceRef}:{sourceRemoteRef}"],
            cancellationToken);
        if (sourceFetch.ExitCode != 0)
        {
            throw new ReviewDeferredException(
                $"PR source repository '{candidate.SourceRepositoryName ?? candidate.RepositoryName}' is unavailable; retry after access or repository metadata is restored.");
        }

        var fetchedSource = await RunRequiredAsync(
            repositoryPath, "git", ["rev-parse", sourceRemoteRef], cancellationToken);
        if (!fetchedSource.Output.Trim().Equals(candidate.HeadSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Fetched source ref '{sourceBranch}' does not match the requested head SHA.");
        }

        if (options.EnablePush)
        {
            var writable = await RunAsync(
                repositoryPath,
                "git",
                ["push", "--dry-run", $"--force-with-lease={candidate.SourceRef}:{candidate.HeadSha}",
                    SourceRemoteName, $"{candidate.HeadSha}:{candidate.SourceRef}"],
                cancellationToken);
            if (writable.ExitCode != 0)
            {
                throw new ReviewDeferredException(
                    $"PR source repository '{candidate.SourceRepositoryName ?? candidate.RepositoryName}' is not writable; retry after source-branch permissions are restored.");
            }
        }
        var fetchedTarget = await RunRequiredAsync(
            repositoryPath, "git", ["rev-parse", "--verify", $"{candidate.TargetSha}^{{commit}}"], cancellationToken);
        if (!fetchedTarget.Output.Trim().Equals(candidate.TargetSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Fetched target commit does not match the requested target SHA.");
        }
        var fetchedBase = await RunRequiredAsync(
            repositoryPath, "git", ["rev-parse", "--verify", $"{baseRemoteRef}^{{commit}}"], cancellationToken);
        var baseSha = fetchedBase.Output.Trim();

        var branchName = $"chickens/pr-{candidate.PullRequestId}-{candidate.HeadSha[..12]}-{fingerprint.Value[..12]}";
        var worktreePath = Path.Combine(
            Path.GetFullPath(options.WorktreesPath), $"pr-{candidate.PullRequestId}", candidate.HeadSha, fingerprint.Value);
        var workspace = new RepositoryWorkspace(
            repositoryPath, worktreePath, branchName, candidate.SourceRef, candidate.HeadSha, baseSha);
        if (Directory.Exists(worktreePath) || await BranchExistsAsync(repositoryPath, branchName, cancellationToken))
        {
            await CleanupAsync(workspace, CancellationToken.None);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(worktreePath)!);
        await RunRequiredAsync(
            repositoryPath,
            "git",
            ["worktree", "add", "-b", branchName, worktreePath, candidate.HeadSha],
            cancellationToken);
        var rebase = await RunAsync(
            worktreePath,
            "git",
            ["rebase", "--autostash", "--committer-date-is-author-date", baseSha],
            cancellationToken);
        if (rebase.ExitCode == 0)
        {
            return workspace;
        }

        if (await HasUnmergedEntriesAsync(worktreePath, cancellationToken) &&
            await IsRebaseInProgressAsync(worktreePath, cancellationToken))
        {
            return workspace with { HasRebaseConflicts = true };
        }

        await CleanupAsync(workspace, CancellationToken.None);
        throw new InvalidOperationException($"git rebase failed with exit code {rebase.ExitCode}:{Environment.NewLine}{rebase.Output}");
    }

    public async Task<ValidationResult> VerifyReadyAsync(
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        ValidateOwnedWorkspace(workspace);
        if (await HasUnmergedEntriesAsync(workspace.WorktreePath, cancellationToken))
        {
            return new ValidationResult(false, -1, "The worktree still contains unmerged entries.");
        }

        if (await IsRebaseInProgressAsync(workspace.WorktreePath, cancellationToken))
        {
            return new ValidationResult(false, -1, "The worktree still has a rebase in progress.");
        }

        var mergeBase = await RunAsync(
            workspace.WorktreePath, "git", ["merge-base", "--is-ancestor", workspace.BaseSha, "HEAD"], cancellationToken);
        return mergeBase.ExitCode == 0
            ? new ValidationResult(true, 0, "Rebase completed and the worktree has no unmerged entries.")
            : new ValidationResult(false, mergeBase.ExitCode, "HEAD is not based on the fetched base commit.");
    }

    public async Task CleanupAsync(RepositoryWorkspace workspace, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ValidateOwnedWorkspace(workspace);
        var failures = new List<Exception>();
        var wasRegistered = false;
        try
        {
            var listed = await RunAsync(workspace.RepositoryPath, "git", ["worktree", "list", "--porcelain"], cancellationToken);
            if (listed.ExitCode != 0)
            {
                failures.Add(CleanupFailure("list worktrees", listed));
            }
            else
            {
                wasRegistered = listed.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Where(line => line.StartsWith("worktree ", StringComparison.Ordinal))
                    .Select(line => line["worktree ".Length..])
                    .Any(path => Path.GetFullPath(path).Equals(
                        Path.GetFullPath(workspace.WorktreePath), StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            var removed = await RunAsync(
                workspace.RepositoryPath, "git", ["worktree", "remove", "--force", "--force", workspace.WorktreePath], cancellationToken);
            if (removed.ExitCode != 0 && wasRegistered)
            {
                failures.Add(CleanupFailure("remove worktree", removed));
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            var pruned = await RunAsync(workspace.RepositoryPath, "git", ["worktree", "prune"], cancellationToken);
            if (pruned.ExitCode != 0) failures.Add(CleanupFailure("prune worktrees", pruned));
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            if (Directory.Exists(workspace.WorktreePath))
            {
                var attributes = File.GetAttributes(workspace.WorktreePath);
                Directory.Delete(workspace.WorktreePath, !attributes.HasFlag(FileAttributes.ReparsePoint));
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            if (await BranchExistsAsync(workspace.RepositoryPath, workspace.BranchName, cancellationToken))
            {
                var deleted = await RunAsync(
                    workspace.RepositoryPath, "git", ["branch", "-D", workspace.BranchName], cancellationToken);
                if (deleted.ExitCode != 0) failures.Add(CleanupFailure("delete branch", deleted));
            }
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        if (failures.Count > 0)
        {
            throw new AggregateException($"Cleanup failed for owned worktree '{workspace.WorktreePath}'.", failures);
        }
    }

    public async Task<string> CreatePatchAsync(
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        await RunRequiredAsync(workspace.WorktreePath, "git", ["add", "--intent-to-add", "."], cancellationToken);
        var result = await RunRequiredAsync(
            workspace.WorktreePath,
            "git",
            ["diff", "--binary", "--no-ext-diff", "HEAD"],
            cancellationToken);
        return result.Output;
    }

    public async Task<ValidationResult> ValidateAsync(
        RepositoryWorkspace workspace,
        CancellationToken cancellationToken = default)
    {
        if (options.ValidationCommands.Count == 0)
        {
            return new ValidationResult(false, -1, "No validation commands are configured.");
        }

        var output = new StringBuilder();
        foreach (var command in options.ValidationCommands)
        {
            var result = await RunAsync(
                workspace.WorktreePath,
                command.FileName,
                command.Arguments,
                cancellationToken,
                useRestrictedEnvironment: true);
            output.AppendLine(options.Redactor.Redact($"> {command.FileName} {string.Join(' ', command.Arguments)}"));
            output.AppendLine(result.Output);
            if (result.ExitCode != 0)
            {
                return new ValidationResult(false, result.ExitCode, output.ToString());
            }
        }

        return new ValidationResult(true, 0, output.ToString());
    }

    public async Task<PublicationResult> CommitAndPushAsync(
        RepositoryWorkspace workspace,
        ReviewCandidate candidate,
        FeedbackFingerprint fingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(fingerprint);
        ValidateOwnedWorkspace(workspace);
        if (!options.EnablePush)
        {
            return new PublicationResult(false, false, null, "Publishing is disabled by FARM_CHICKENS_ENABLE_PUSH.");
        }

        var readiness = await VerifyReadyAsync(workspace, cancellationToken);
        if (!readiness.Succeeded)
        {
            return new PublicationResult(false, false, null, readiness.Output);
        }

        if (string.IsNullOrWhiteSpace(options.UserName) || string.IsNullOrWhiteSpace(options.UserEmail))
        {
            return new PublicationResult(false, false, null, "Git user name and email are required for publication.");
        }

        var status = await RunRequiredAsync(
            workspace.WorktreePath, "git", ["status", "--porcelain=v1", "--untracked-files=all"], cancellationToken);
        var changedPaths = status.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Length > 3 ? line[3..].Split(" -> ", StringSplitOptions.TrimEntries).Last() : string.Empty)
            .ToArray();
        var unsafePath = changedPaths.FirstOrDefault(IsSecretOrArtifactPath);
        if (unsafePath is not null)
        {
            return new PublicationResult(false, false, null, $"Refusing to commit secret or artifact path '{unsafePath}'.");
        }

        await RunRequiredAsync(workspace.WorktreePath, "git", ["add", "-A"], cancellationToken);
        var staged = await RunAsync(workspace.WorktreePath, "git", ["diff", "--cached", "--quiet"], cancellationToken);
        if (staged.ExitCode == 0)
        {
            return new PublicationResult(false, false, null, "No code changes are staged for publication.");
        }
        if (staged.ExitCode != 1)
        {
            return new PublicationResult(false, false, null, "Git could not inspect the staged changes.");
        }

        var stagedContent = await RunAsync(
            workspace.WorktreePath,
            "git",
            ["diff", "--cached", "--binary", "--no-ext-diff"],
            cancellationToken,
            redactOutput: false);
        if (stagedContent.ExitCode != 0)
        {
            return new PublicationResult(false, false, null, "Git could not scan the staged content.");
        }
        var findings = options.ContentScanner.Find(stagedContent.Output);
        var stagedPaths = await RunAsync(
            workspace.WorktreePath,
            "git",
            ["diff", "--cached", "--name-only", "--diff-filter=ACMR", "-z"],
            cancellationToken,
            redactOutput: false);
        if (stagedPaths.ExitCode != 0)
        {
            return new PublicationResult(false, false, null, "Git could not list staged content for scanning.");
        }

        foreach (var path in stagedPaths.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var blob = await RunAsync(
                workspace.WorktreePath,
                "git",
                ["show", $":{path}"],
                cancellationToken,
                redactOutput: false);
            if (blob.ExitCode != 0)
            {
                return new PublicationResult(false, false, null, "Git could not read staged content for scanning.");
            }

            findings = findings.Concat(options.ContentScanner.Find(blob.Output)).Distinct(StringComparer.Ordinal).ToArray();
        }

        if (findings.Count > 0)
        {
            return new PublicationResult(
                false,
                false,
                null,
                $"Refusing to publish staged content containing: {string.Join(", ", findings)}.");
        }

        var message = $"Farm Chickens: address PR {candidate.PullRequestId} feedback {fingerprint.Value[..12]}";
        await RunRequiredAsync(
            workspace.WorktreePath,
            "git",
            ["commit", "-m", message],
            cancellationToken,
            DeterministicCommitDate(candidate, fingerprint));
        var commit = await RunRequiredAsync(workspace.WorktreePath, "git", ["rev-parse", "HEAD"], cancellationToken);
        var commitSha = commit.Output.Trim();
        var push = await RunAsync(
            workspace.WorktreePath,
            "git",
            ["push", $"--force-with-lease={workspace.SourceRef}:{workspace.ExpectedOldHeadSha}",
                SourceRemoteName, $"HEAD:{workspace.SourceRef}"],
            cancellationToken);
        return push.ExitCode == 0
            ? new PublicationResult(true, true, commitSha, "Commit pushed with an exact force-with-lease.")
            : new PublicationResult(
                false,
                false,
                commitSha,
                "Push was rejected. The source branch may have moved, may be read-only, or may belong to an inaccessible fork.");
    }

    // The shared cache clone is disposable/reconstructable from origin; reset it if a prior run left local
    // modifications on disk (e.g. an interrupted validation or rebase), so it never blocks the next fetch/rebase.
    private async Task SelfHealCacheRepositoryAsync(
        string repositoryPath, string repositoryId, CancellationToken cancellationToken)
    {
        var status = await RunRequiredAsync(
            repositoryPath, "git", ["status", "--porcelain=v1", "--untracked-files=all"], cancellationToken);
        var isDirty = status.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => line.Length > 1 && line[1] != ' ');
        if (!isDirty)
        {
            return;
        }

        Console.WriteLine($"cache_repository_reset repository_id={Sanitize(repositoryId)} path={repositoryPath}");
        await RunRequiredAsync(repositoryPath, "git", ["reset", "--hard", "HEAD"], cancellationToken);
        await RunRequiredAsync(repositoryPath, "git", ["clean", "-fdx"], cancellationToken);
    }

    private async Task ConfigureIdentityAsync(string repositoryPath, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            await RunRequiredAsync(repositoryPath, "git", ["config", "--local", "user.name", options.UserName], cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(options.UserEmail))
        {
            await RunRequiredAsync(repositoryPath, "git", ["config", "--local", "user.email", options.UserEmail], cancellationToken);
        }
    }

    private async Task<bool> BranchExistsAsync(
        string repositoryPath,
        string branchName,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
            repositoryPath, "git", ["show-ref", "--verify", "--quiet", $"refs/heads/{branchName}"], cancellationToken);
        if (result.ExitCode is 0 or 1)
        {
            return result.ExitCode == 0;
        }

        throw new InvalidOperationException($"git show-ref failed with exit code {result.ExitCode}:{Environment.NewLine}{result.Output}");
    }

    private async Task<bool> HasUnmergedEntriesAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var result = await RunRequiredAsync(
            worktreePath, "git", ["diff", "--name-only", "--diff-filter=U"], cancellationToken);
        return !string.IsNullOrWhiteSpace(result.Output);
    }

    private async Task<bool> IsRebaseInProgressAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var gitDirectory = await RunRequiredAsync(worktreePath, "git", ["rev-parse", "--git-dir"], cancellationToken);
        var path = gitDirectory.Output.Trim();
        var fullPath = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(worktreePath, path));
        return Directory.Exists(Path.Combine(fullPath, "rebase-merge")) || Directory.Exists(Path.Combine(fullPath, "rebase-apply"));
    }

    private void ValidateOwnedWorkspace(RepositoryWorkspace workspace)
    {
        var worktreesRoot = Path.GetFullPath(options.WorktreesPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var cacheRoot = Path.GetFullPath(options.CachePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var worktreePath = Path.GetFullPath(workspace.WorktreePath);
        var repositoryPath = Path.GetFullPath(workspace.RepositoryPath);
        if (!IsChildPath(worktreePath, worktreesRoot) || !IsChildPath(repositoryPath, cacheRoot) ||
            !workspace.BranchName.StartsWith("chickens/pr-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to clean a workspace that is not owned by Farm Chickens.");
        }
    }

    private static bool IsChildPath(string candidate, string root) =>
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static InvalidOperationException CleanupFailure(string action, ProcessResult result) =>
        new($"Git cleanup could not {action}; exit code {result.ExitCode}:{Environment.NewLine}{result.Output}");

    private async Task<ProcessResult> RunRequiredAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? gitCommitDate = null)
    {
        var result = await RunAsync(workingDirectory, fileName, arguments, cancellationToken, gitCommitDate);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{fileName} failed with exit code {result.ExitCode}:{Environment.NewLine}{result.Output}");
        }

        return result;
    }

    private async Task<ProcessResult> RunAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        DateTimeOffset? gitCommitDate = null,
        bool useRestrictedEnvironment = false,
        bool redactOutput = true)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (useRestrictedEnvironment)
        {
            RestrictValidationEnvironment(startInfo);
        }

        if (!useRestrictedEnvironment &&
            fileName.Equals("git", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(options.HttpExtraHeader))
        {
            startInfo.Environment["GIT_CONFIG_COUNT"] = "1";
            startInfo.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
            startInfo.Environment["GIT_CONFIG_VALUE_0"] = options.HttpExtraHeader;
        }

        if (gitCommitDate is not null)
        {
            var value = gitCommitDate.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss'Z'");
            startInfo.Environment["GIT_AUTHOR_DATE"] = value;
            startInfo.Environment["GIT_COMMITTER_DATE"] = value;
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}.");
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            throw;
        }

        var processOutput = (await standardOutput) + (await standardError);
        return new ProcessResult(process.ExitCode, redactOutput ? options.Redactor.Redact(processOutput) : processOutput);
    }

    private void RestrictValidationEnvironment(ProcessStartInfo startInfo)
    {
        startInfo.Environment.Clear();
        foreach (var variable in RestrictedProcessEnvironment.Create(options.SafeProcessEnvironment))
        {
            startInfo.Environment[variable.Key] = variable.Value;
        }
    }

    private static void ValidateCandidate(ReviewCandidate candidate, bool allowFileRepositoryUrls)
    {
        ValidateRepositoryUrl(candidate.RepositoryUrl, allowFileRepositoryUrls);
        if (candidate.SourceRepositoryUrl is not null)
        {
            try
            {
                ValidateRepositoryUrl(candidate.SourceRepositoryUrl, allowFileRepositoryUrls);
            }
            catch (InvalidOperationException exception)
            {
                throw new ReviewDeferredException(
                    $"PR source repository '{candidate.SourceRepositoryName ?? candidate.SourceRepositoryId}' has an unusable clone URL; retry after repository metadata is corrected.",
                    exception);
            }
        }

        if (!BranchRef.IsMatch(candidate.SourceRef) || candidate.SourceRef.Contains("..", StringComparison.Ordinal) ||
            !BranchRef.IsMatch(candidate.TargetRef) || candidate.TargetRef.Contains("..", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Pull request source and target must be valid refs/heads branch refs.");
        }

        if (!FullSha.IsMatch(candidate.HeadSha) || candidate.TargetSha is null || !FullSha.IsMatch(candidate.TargetSha))
        {
            throw new InvalidOperationException("Pull request head and target SHAs must be full 40-character hexadecimal commit IDs.");
        }
    }

    private static Uri ResolveSourceRepositoryUrl(ReviewCandidate candidate)
    {
        if (candidate.SourceRepositoryUrl is not null)
        {
            return candidate.SourceRepositoryUrl;
        }

        if (string.IsNullOrWhiteSpace(candidate.SourceRepositoryId) ||
            string.Equals(candidate.SourceRepositoryId, candidate.RepositoryId, StringComparison.OrdinalIgnoreCase))
        {
            return candidate.RepositoryUrl;
        }

        throw new ReviewDeferredException(
            $"PR source repository '{candidate.SourceRepositoryName ?? candidate.SourceRepositoryId}' has no clone URL; retry after Azure DevOps returns writable fork metadata.");
    }

    private static void ValidateRepositoryUrl(Uri repositoryUrl, bool allowFileRepositoryUrls)
    {
        if (!repositoryUrl.IsAbsoluteUri ||
            (repositoryUrl.Scheme != Uri.UriSchemeHttps && repositoryUrl.Scheme != Uri.UriSchemeHttp &&
                !(allowFileRepositoryUrls && repositoryUrl.Scheme == Uri.UriSchemeFile)) ||
            !string.IsNullOrEmpty(repositoryUrl.UserInfo))
        {
            throw new InvalidOperationException("Repository URL must be an absolute HTTP(S) URL without embedded credentials.");
        }
    }

    private static DateTimeOffset DeterministicCommitDate(
        ReviewCandidate candidate,
        FeedbackFingerprint fingerprint)
    {
        var latestExternalFeedback = candidate.Threads
            .Where(thread => !thread.IsResolved)
            .SelectMany(thread => thread.Comments)
            .Where(comment => !comment.IsDeleted &&
                !string.Equals(comment.AuthorId, candidate.AuthorId, StringComparison.OrdinalIgnoreCase))
            .Select(comment => (DateTimeOffset?)comment.PublishedAt.ToUniversalTime())
            .Max();
        if (latestExternalFeedback is not null)
        {
            return latestExternalFeedback.Value;
        }

        const long fiftyYearsInSeconds = 50L * 365 * 24 * 60 * 60;
        var fingerprintSeconds = Convert.ToUInt32(fingerprint.Value[..8], 16) % fiftyYearsInSeconds;
        return new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(fingerprintSeconds);
    }

    private static void ValidateBranchName(string branchName)
    {
        if (!BranchName.IsMatch(branchName) || branchName.StartsWith("refs/", StringComparison.OrdinalIgnoreCase) ||
            branchName.Contains("..", StringComparison.Ordinal) ||
            branchName.EndsWith('.') || branchName.EndsWith('/') || branchName.Contains("@{", StringComparison.Ordinal) ||
            branchName.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.') ||
                part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Configured base branch is not a valid safe Git branch name.");
        }
    }

    private static string Sanitize(string value) =>
        string.Concat(value.Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_'));

    private static string RemoveHeadsPrefix(string reference) =>
        reference.StartsWith("refs/heads/", StringComparison.Ordinal)
            ? reference["refs/heads/".Length..]
            : throw new InvalidOperationException($"Expected a branch ref but received '{reference}'.");

    private static bool IsSecretOrArtifactPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var fileName = Path.GetFileName(normalized);
        return normalized.StartsWith("artifacts/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Split('/').Any(segment => segment.Equals("artifacts", StringComparison.OrdinalIgnoreCase)) ||
            fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".key", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}