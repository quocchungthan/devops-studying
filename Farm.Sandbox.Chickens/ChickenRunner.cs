using System.Text.Json;
using Farm.Copilot;
using Farm.Core.Chickens;

namespace Farm.Sandbox.Chickens;

public sealed class ChickenRunner(
    IReviewContextSource contextSource,
    IRepositoryWorkspaceManager workspaces,
    IReviewBrain brain,
    IAttemptStore attempts,
    ChickenOptions options,
    ISensitiveDataRedactor redactor,
    ISensitiveContentScanner contentScanner,
    ILogger<ChickenRunner> logger,
    ChickenStatusWriter? statusWriter = null)
{
    private readonly string ownerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<ChickenCycleResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var currentUserId = await contextSource.GetCurrentUserIdAsync(cancellationToken);
        var discovery = await contextSource.GetCandidatesAsync(cancellationToken);
        foreach (var skipped in discovery.SkippedCandidates)
        {
            logger.LogWarning(
                ChickenLogEvents.CandidateSkipped,
                "candidate_skipped pull_request_id={PullRequestId} reason={Reason}",
                skipped.PullRequestId,
                skipped.Reason);
        }

        var candidates = discovery.Candidates;
        var completed = 0;
        var deferred = 0;
        var failed = 0;
        foreach (var candidate in candidates)
        {
            logger.LogInformation(ChickenLogEvents.CandidateDiscovered, "candidate_discovered pull_request_id={PullRequestId}", candidate.PullRequestId);
            var result = await ProcessCandidateAsync(candidate, currentUserId, cancellationToken);
            switch (result)
            {
                case CandidateResult.Completed:
                    completed++;
                    break;
                case CandidateResult.Deferred:
                    deferred++;
                    break;
                case CandidateResult.Failed:
                    failed++;
                    break;
            }
        }

        return new ChickenCycleResult(candidates.Count, completed, deferred, failed);
    }

    private async Task<CandidateResult> ProcessCandidateAsync(
        ReviewCandidate candidate,
        string currentUserId,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var eligibility = ReviewEligibilityEvaluator.Evaluate(
            candidate,
            currentUserId,
            now,
            options.QuietPeriod);
        if (!eligibility.IsEligible)
        {
            logger.LogInformation(ChickenLogEvents.CandidateDeferred, "candidate_deferred pull_request_id={PullRequestId} reason={Reason}", candidate.PullRequestId, eligibility.Reason);
            return CandidateResult.Deferred;
        }

        // Fetched once per cycle: the same redacted context drives the fingerprint and the Copilot run.
        ReviewContext context;
        try
        {
            context = await FetchContextAsync(candidate, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ChickenLogEvents.CandidateFailed, "candidate_failed pull_request_id={PullRequestId} error={Error}", candidate.PullRequestId, redactor.Redact(exception.ToString()));
            await WriteTerminalOutcomeAsync(
                candidate, eligibility.Fingerprint, ReviewOutcomeKind.Failed, exception, cancellationToken);
            return CandidateResult.Failed;
        }

        var legacyKey = new AttemptKey(
            candidate.Organization,
            candidate.Project,
            candidate.RepositoryId,
            candidate.PullRequestId,
            candidate.HeadSha,
            eligibility.Fingerprint.Value);
        var baseline = await attempts.GetBaselineAsync(legacyKey, cancellationToken);
        var baselineHashes = baseline?.WorkItemFingerprint is null ? null : baseline.WorkItemHashes ?? new Dictionary<int, string>();
        var workItems = WorkItemFingerprint.Create(context, currentUserId, baselineHashes);
        var changedWorkItemIds = baselineHashes is null ? [] : workItems.ChangedSince(baselineHashes);
        if (WorkItemFingerprint.LatestActivity(context, currentUserId, changedWorkItemIds) > now - options.QuietPeriod)
        {
            logger.LogInformation(ChickenLogEvents.CandidateDeferred, "candidate_deferred pull_request_id={PullRequestId} reason={Reason}", candidate.PullRequestId, ReviewEligibilityReason.WorkItemFeedbackStillCoolingDown);
            return CandidateResult.Deferred;
        }

        var key = legacyKey with { WorkItemFingerprint = workItems.Value };
        if (baseline is { WorkItemFingerprint: null } &&
            await attempts.AdoptLegacyCompletionAsync(key, workItems.ItemHashes, cancellationToken))
        {
            logger.LogInformation(
                ChickenLogEvents.AttemptBaselineAdopted,
                "attempt_baseline_adopted pull_request_id={PullRequestId} work_item_count={WorkItemCount}",
                candidate.PullRequestId,
                workItems.ItemHashes.Count);
            return CandidateResult.Deferred;
        }

        if (changedWorkItemIds.Count > 0)
        {
            logger.LogInformation(
                ChickenLogEvents.FeedbackChangedWorkItems,
                "feedback_changed_work_items pull_request_id={PullRequestId} changed_work_item_ids={ChangedWorkItemIds}",
                candidate.PullRequestId,
                string.Join(',', changedWorkItemIds));
        }

        var fingerprint = eligibility.Fingerprint.WithWorkItems(workItems);
        var lease = await attempts.TryAcquireLeaseAsync(key, ownerId, options.LeaseDuration, cancellationToken);
        if (lease is null)
        {
            logger.LogInformation(ChickenLogEvents.CandidateDeferred, "candidate_deferred pull_request_id={PullRequestId} reason=lease_unavailable", candidate.PullRequestId);
            return CandidateResult.Deferred;
        }

        logger.LogInformation(ChickenLogEvents.LeaseAcquired, "lease_acquired pull_request_id={PullRequestId}", candidate.PullRequestId);
        if (statusWriter is not null)
        {
            await statusWriter.RecordCandidateAsync(candidate.PullRequestId, fingerprint.Value, cancellationToken);
        }

        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var renewal = RenewLeaseAsync(lease, execution);
        var leaseCompleted = false;
        var result = CandidateResult.Failed;
        try
        {
            leaseCompleted = await ExecuteAsync(candidate, context, fingerprint, workItems.ItemHashes, lease, execution.Token);
            result = leaseCompleted ? CandidateResult.Completed : CandidateResult.Failed;
        }
        catch (ReviewDeferredException exception)
        {
            logger.LogWarning(ChickenLogEvents.CandidateDeferred, "candidate_deferred pull_request_id={PullRequestId} reason={Reason}", candidate.PullRequestId, redactor.Redact(exception.Message));
            await WriteTerminalOutcomeAsync(
                candidate, fingerprint, ReviewOutcomeKind.Deferred, exception, cancellationToken);
            result = CandidateResult.Deferred;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            logger.LogError(ChickenLogEvents.CandidateFailed, "candidate_failed pull_request_id={PullRequestId} error={Error}", candidate.PullRequestId, redactor.Redact(exception.ToString()));
            await WriteTerminalOutcomeAsync(
                candidate, fingerprint, ReviewOutcomeKind.Failed, exception, cancellationToken);
            result = CandidateResult.Failed;
        }
        finally
        {
            execution.Cancel();
            try
            {
                await renewal;
            }
            finally
            {
                if (!leaseCompleted)
                {
                    await attempts.ReleaseAsync(lease, CancellationToken.None);
                    logger.LogInformation(ChickenLogEvents.LeaseReleased, "lease_released pull_request_id={PullRequestId}", candidate.PullRequestId);
                }
                if (statusWriter is not null)
                {
                    await statusWriter.ClearCurrentCandidateAsync(CancellationToken.None);
                }
            }
        }

        if (result == CandidateResult.Failed)
        {
            logger.LogError(ChickenLogEvents.CandidateFailed, "candidate_failed pull_request_id={PullRequestId} reason=review_outcome", candidate.PullRequestId);
        }

        return result;
    }

    private enum CandidateResult
    {
        Completed,
        Deferred,
        Failed
    }

    private async Task<ReviewContext> FetchContextAsync(ReviewCandidate candidate, CancellationToken cancellationToken)
    {
        var context = RedactWorkItems(await contextSource.GetContextAsync(candidate, cancellationToken));
        foreach (var skipped in context.SkippedWorkItems ?? [])
        {
            logger.LogWarning(
                ChickenLogEvents.ContextWorkItemSkipped,
                "context_work_item_skipped pull_request_id={PullRequestId} work_item_id={WorkItemId} depth={Depth} reason={Reason}",
                candidate.PullRequestId,
                skipped.WorkItemId,
                skipped.Depth,
                skipped.Reason);
        }

        logger.LogInformation(
            ChickenLogEvents.ContextFetched,
            "context_fetched pull_request_id={PullRequestId} work_item_count={WorkItemCount} max_depth_reached={MaxDepthReached} comment_count={CommentCount} skipped_count={SkippedCount}",
            candidate.PullRequestId,
            context.WorkItems.Count,
            context.WorkItems.Count == 0 ? 0 : context.WorkItems.Max(workItem => workItem.Depth),
            context.WorkItems.Sum(workItem => workItem.Comments.Count),
            context.SkippedWorkItems?.Count ?? 0);
        return context;
    }

    private async Task<bool> ExecuteAsync(
        ReviewCandidate candidate,
        ReviewContext context,
        FeedbackFingerprint fingerprint,
        IReadOnlyDictionary<int, string> workItemHashes,
        Lease lease,
        CancellationToken cancellationToken)
    {
        var artifactDirectory = BuildArtifactDirectory(
            options.ArtifactsPath, candidate.RepositoryName, candidate.PullRequestId, fingerprint.Value);
        Directory.CreateDirectory(artifactDirectory);

        await WriteJsonAsync(Path.Combine(artifactDirectory, "context.json"), context, cancellationToken);
        RepositoryWorkspace? workspace = null;
        try
        {
            workspace = await workspaces.PrepareAsync(candidate, fingerprint, cancellationToken);
            logger.LogInformation(workspace.HasRebaseConflicts ? ChickenLogEvents.RebaseConflict : ChickenLogEvents.RebaseCompleted, "{EventName} pull_request_id={PullRequestId}", workspace.HasRebaseConflicts ? ChickenLogEvents.RebaseConflict.Name : ChickenLogEvents.RebaseCompleted.Name, candidate.PullRequestId);
            logger.LogInformation(ChickenLogEvents.CopilotStarted, "copilot_started pull_request_id={PullRequestId}", candidate.PullRequestId);
            var brainOutcome = await brain.ResolveAsync(
                new CopilotReviewRequest(context, workspace, CopilotOverrideLoader.DefaultPurpose, artifactDirectory),
                cancellationToken);
            logger.LogInformation(ChickenLogEvents.CopilotCompleted, "copilot_completed pull_request_id={PullRequestId} outcome={Outcome}", candidate.PullRequestId, brainOutcome.Kind);

            var readiness = await workspaces.VerifyReadyAsync(workspace, cancellationToken);
            var patch = readiness.Succeeded
                ? await workspaces.CreatePatchAsync(workspace, cancellationToken)
                : string.Empty;
            var validation = readiness.Succeeded
                ? await workspaces.ValidateAsync(workspace, cancellationToken)
                : readiness;
            logger.LogInformation(ChickenLogEvents.ValidationCompleted, "validation_completed pull_request_id={PullRequestId} succeeded={Succeeded} exit_code={ExitCode}", candidate.PullRequestId, validation.Succeeded, validation.ExitCode);
            await WriteTextAsync(Path.Combine(artifactDirectory, "changes.patch"), patch, cancellationToken);
            await WriteTextAsync(Path.Combine(artifactDirectory, "validation.txt"), validation.Output, cancellationToken);

            var outcome = DetermineOutcome(brainOutcome, patch, validation);
            if (outcome.Kind == ReviewOutcomeKind.ChangesProduced)
            {
                var publication = await workspaces.CommitAndPushAsync(
                    workspace, candidate, fingerprint, cancellationToken);
                await WriteJsonAsync(Path.Combine(artifactDirectory, "publication.json"), publication, cancellationToken);
                logger.LogInformation(publication.Succeeded && publication.Pushed ? ChickenLogEvents.PushCompleted : ChickenLogEvents.PushRejected, "{EventName} pull_request_id={PullRequestId} pushed={Pushed} summary={Summary}", publication.Succeeded && publication.Pushed ? ChickenLogEvents.PushCompleted.Name : ChickenLogEvents.PushRejected.Name, candidate.PullRequestId, publication.Pushed, redactor.Redact(publication.Summary));
                outcome = publication.Succeeded && publication.Pushed
                    ? outcome with
                    {
                        Summary = $"{outcome.Summary} {publication.Summary}",
                        CommitSha = publication.CommitSha,
                        Published = true
                    }
                    : new ReviewOutcome(
                        ReviewOutcomeKind.Failed,
                        publication.Summary,
                        brainOutcome.Transcript,
                        publication.CommitSha,
                        false);
            }
            if (outcome.Kind == ReviewOutcomeKind.ExplanationOnly)
            {
                await WriteTextAsync(
                    Path.Combine(artifactDirectory, "explanation.md"), outcome.Transcript ?? outcome.Summary, cancellationToken);
            }

            await WriteJsonAsync(Path.Combine(artifactDirectory, "result.json"), outcome, cancellationToken);
            if (outcome.Transcript is not null)
            {
                await WriteTextAsync(Path.Combine(artifactDirectory, "transcript.md"), outcome.Transcript, cancellationToken);
            }

            if (outcome.Kind is ReviewOutcomeKind.ChangesProduced or ReviewOutcomeKind.ExplanationOnly)
            {
                await attempts.CompleteAsync(
                    lease,
                    new AttemptState(lease.Key, outcome.Kind, DateTimeOffset.UtcNow, artifactDirectory, workItemHashes),
                    cancellationToken);
                return true;
            }

            return false;
        }
        finally
        {
            if (workspace is not null)
            {
                await workspaces.CleanupAsync(workspace, CancellationToken.None);
            }
        }
    }

    public static ReviewOutcome DetermineOutcome(ReviewOutcome brainOutcome, string patch, ValidationResult validation)
    {
        if (brainOutcome.Kind == ReviewOutcomeKind.Failed)
        {
            return brainOutcome;
        }

        if (!string.IsNullOrWhiteSpace(patch))
        {
            return validation.Succeeded
                ? brainOutcome with { Kind = ReviewOutcomeKind.ChangesProduced }
                : new ReviewOutcome(ReviewOutcomeKind.Failed, "Code changes failed configured validation.", brainOutcome.Transcript);
        }

        return brainOutcome.Kind == ReviewOutcomeKind.ExplanationOnly && !string.IsNullOrWhiteSpace(brainOutcome.Summary)
            ? brainOutcome
            : new ReviewOutcome(ReviewOutcomeKind.Failed, "No code changes or reviewer explanation were produced.", brainOutcome.Transcript);
    }

    // Work item text is untrusted third-party content that reaches both artifacts and the Copilot prompt.
    internal ReviewContext RedactWorkItems(ReviewContext context) => context with
    {
        WorkItems = context.WorkItems.Select(workItem => workItem with
        {
            Title = RedactOptional(workItem.Title),
            Description = RedactOptional(workItem.Description),
            ReproSteps = RedactOptional(workItem.ReproSteps),
            AcceptanceCriteria = RedactOptional(workItem.AcceptanceCriteria),
            Comments = workItem.Comments
                .Select(comment => comment with { Content = redactor.Redact(comment.Content) })
                .ToArray()
        }).ToArray()
    };

    private string? RedactOptional(string? value) => value is null ? null : redactor.Redact(value);

    private async Task RenewLeaseAsync(Lease initialLease, CancellationTokenSource execution)
    {
        try
        {
            var lease = initialLease;
            var interval = TimeSpan.FromTicks(Math.Max(1, options.LeaseDuration.Ticks / 2));
            while (!execution.IsCancellationRequested)
            {
                await Task.Delay(interval, execution.Token);
                lease = await attempts.RenewLeaseAsync(lease, options.LeaseDuration, execution.Token)
                    ?? throw new InvalidOperationException("Attempt lease ownership was lost during execution.");
                logger.LogInformation(ChickenLogEvents.LeaseRenewed, "lease_renewed pull_request_id={PullRequestId}", lease.Key.PullRequestId);
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
        }
        catch
        {
            execution.Cancel();
            throw;
        }
    }

    private async Task WriteTerminalOutcomeAsync(
        ReviewCandidate candidate,
        FeedbackFingerprint fingerprint,
        ReviewOutcomeKind kind,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var artifactDirectory = BuildArtifactDirectory(
            options.ArtifactsPath, candidate.RepositoryName, candidate.PullRequestId, fingerprint.Value);
        Directory.CreateDirectory(artifactDirectory);
        var outcome = new ReviewOutcome(kind, exception.Message, exception.ToString());
        await WriteJsonAsync(Path.Combine(artifactDirectory, "result.json"), outcome, cancellationToken);
        await WriteTextAsync(Path.Combine(artifactDirectory, "transcript.md"), exception.ToString(), cancellationToken);
    }

    private Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken) =>
        WriteTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);

    private Task WriteTextAsync(string path, string value, CancellationToken cancellationToken)
    {
        var redacted = redactor.Redact(value);
        var findings = contentScanner.Find(redacted);
        if (findings.Count > 0)
        {
            File.Delete(path);
            throw new InvalidOperationException(
                $"Refusing to persist sensitive content containing: {string.Join(", ", findings)}.");
        }

        return File.WriteAllTextAsync(path, redacted, cancellationToken);
    }

    internal static string BuildArtifactDirectory(
        string artifactsPath,
        string repositoryName,
        int pullRequestId,
        string fingerprint)
    {
        var root = Path.GetFullPath(artifactsPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repositorySegment = string.Concat(repositoryName.Select(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'
                ? character
                : '_'));
        if (repositorySegment.Length == 0)
        {
            throw new InvalidOperationException("Repository name cannot produce an empty artifact directory segment.");
        }

        var candidate = Path.GetFullPath(Path.Combine(root, repositorySegment, $"pr-{pullRequestId}", fingerprint));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException("Artifact directory must remain beneath the configured artifacts path.");
        }

        return candidate;
    }
}