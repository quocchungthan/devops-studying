using System.Globalization;
using Farm.Core.Chickens;
using Farm.Core.Cows;

namespace Farm.Sandbox.Cows;

public sealed class CowRunner(
    ICloudWorkItemSource cloud,
    INoteSyncStore store,
    IInvestigationAuthor author,
    CowNoteStore notes,
    CowOptions options,
    ISensitiveDataRedactor redactor,
    ISensitiveContentScanner contentScanner,
    CowStatusWriter statusWriter,
    ILogger<CowRunner> logger)
{
    public async Task<CowCycleResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var records = await store.GetAllAsync(cancellationToken);
        var assignedIds = (await cloud.GetAssignedToMeWorkItemIdsAsync(cancellationToken))
            .Where(id => id > 0).Distinct().ToArray();
        var snapshots = (await cloud.GetWorkItemsAsync(assignedIds, cancellationToken))
            .ToDictionary(snapshot => snapshot.WorkItem.Id);
        var assignedSet = assignedIds.ToHashSet();
        var candidateRelatedIds = assignedIds
            .SelectMany(id => snapshots.TryGetValue(id, out var snapshot) ? snapshot.RelatedWorkItemIds : [])
            .Where(id => id > 0 && !assignedSet.Contains(id))
            .Distinct()
            .ToArray();
        var relatedIds = candidateRelatedIds.Take(options.MaxRelatedItems).ToArray();
        foreach (var snapshot in await cloud.GetWorkItemsAsync(relatedIds, cancellationToken))
        {
            snapshots.TryAdd(snapshot.WorkItem.Id, snapshot);
        }

        var scope = assignedSet.Concat(relatedIds).ToHashSet();
        var localIds = notes.DiscoverWorkItemIds();
        var outOfScopeCount = localIds.Concat(records.Keys).Where(id => id > 0 && !scope.Contains(id)).Distinct().Count();
        var targetIds = scope.Concat(localIds).Concat(records.Keys)
            .Where(id => id > 0).Distinct().Order().ToArray();
        logger.LogInformation(CowLogEvents.TargetsResolved, "targets_resolved assigned_count={AssignedCount} related_count={RelatedCount} related_capped_count={RelatedCappedCount} local_count={LocalCount} tracked_count={TrackedCount} out_of_scope_count={OutOfScopeCount} target_count={TargetCount}", assignedIds.Length, relatedIds.Length, candidateRelatedIds.Length - relatedIds.Length, localIds.Count, records.Count, outOfScopeCount, targetIds.Length);

        var pullRequestCache = new Dictionary<int, CloudPullRequestState?>();
        int kept = 0, patched = 0, removed = 0, deferred = 0, failed = 0, patchAttempts = 0;
        foreach (var id in targetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await statusWriter.RecordCurrentWorkItemAsync(id, cancellationToken);
                var inScope = scope.Contains(id);
                var snapshot = inScope ? snapshots.GetValueOrDefault(id) : null;
                var isRemoved = !inScope || CowSyncPolicy.IsRemoved(snapshot);
                var pullRequests = isRemoved
                    ? new Dictionary<int, CloudPullRequestState?>()
                    : await LoadPullRequestsAsync(snapshot!, pullRequestCache, cancellationToken);
                IReadOnlyList<WorkItemComment> comments = isRemoved ? [] : await cloud.GetCommentsAsync(id, cancellationToken);
                var fingerprint = isRemoved ? null : CowSyncPolicy.ComputeFingerprint(snapshot!, pullRequests, comments);
                var stored = records.GetValueOrDefault(id);
                var noteHash = await notes.TryComputeHashAsync(id, cancellationToken);
                var decision = inScope
                    ? CowSyncPolicy.Decide(isRemoved, fingerprint, pullRequests.Values.All(state => state is not null), stored, noteHash, DateTimeOffset.UtcNow, options.MaxNoteAge)
                    : CowSyncPolicy.DecideOutOfScope(stored);

                if (decision.ClearRemovedMarker && stored is not null)
                {
                    await store.UpsertAsync(stored with { RemovedAt = null }, cancellationToken);
                    logger.LogInformation(CowLogEvents.NoteRestored, "note_restored work_item_id={WorkItemId}", id);
                }

                switch (decision.Action)
                {
                    case CowSyncAction.Keep:
                        kept++;
                        logger.LogInformation(CowLogEvents.NoteKept, "note_kept work_item_id={WorkItemId} reason={Reason}", id, decision.Reason);
                        break;
                    case CowSyncAction.MarkRemoved:
                        await store.UpsertAsync(new NoteSyncRecord(
                            id, stored?.Fingerprint ?? CowSyncPolicy.RemovedFingerprint, stored?.NoteHash ?? noteHash,
                            stored?.SyncedAt ?? DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), cancellationToken);
                        removed++;
                        logger.LogInformation(CowLogEvents.NoteMarkedRemoved, "note_marked_removed work_item_id={WorkItemId} reason={Reason} note_exists={NoteExists}", id, decision.Reason, noteHash is not null);
                        break;
                    case CowSyncAction.Defer:
                        deferred++;
                        logger.LogInformation(CowLogEvents.NoteDeferred, "note_deferred work_item_id={WorkItemId} reason={Reason}", id, decision.Reason);
                        break;
                    // The cap counts attempts so repeated failures cannot spend unbounded Copilot calls per cycle.
                    case CowSyncAction.Patch when patchAttempts >= options.MaxPatchesPerRun:
                        deferred++;
                        logger.LogInformation(CowLogEvents.NoteDeferred, "note_deferred work_item_id={WorkItemId} reason=patch_limit_reached pending_reason={Reason}", id, decision.Reason);
                        break;
                    case CowSyncAction.Patch:
                        patchAttempts++;
                        if (await PatchAsync(snapshot!, pullRequests, comments, fingerprint!, decision.Reason, cancellationToken))
                        {
                            patched++;
                        }
                        else
                        {
                            failed++;
                        }

                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                failed++;
                logger.LogError(CowLogEvents.NoteFailed, "note_failed work_item_id={WorkItemId} error={Error}", id, redactor.Redact(exception.Message));
            }
        }

        return new CowCycleResult(targetIds.Length, kept, patched, removed, deferred, failed);
    }

    private async Task<Dictionary<int, CloudPullRequestState?>> LoadPullRequestsAsync(
        CloudWorkItemSnapshot snapshot,
        Dictionary<int, CloudPullRequestState?> cache,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, CloudPullRequestState?>();
        foreach (var pullRequestId in snapshot.LinkedPullRequestIds)
        {
            if (!cache.TryGetValue(pullRequestId, out var state))
            {
                state = await cloud.GetPullRequestAsync(pullRequestId, cancellationToken);
                cache[pullRequestId] = state;
            }

            result[pullRequestId] = state;
        }

        return result;
    }

    private async Task<bool> PatchAsync(
        CloudWorkItemSnapshot snapshot,
        IReadOnlyDictionary<int, CloudPullRequestState?> pullRequests,
        IReadOnlyList<WorkItemComment> comments,
        string fingerprint,
        CowSyncReason reason,
        CancellationToken cancellationToken)
    {
        var id = snapshot.WorkItem.Id;
        var request = new InvestigationRequest(
            snapshot,
            pullRequests.Values.OfType<CloudPullRequestState>().Where(state => !state.IsUnavailable).OrderBy(state => state.Id).ToArray(),
            pullRequests.Where(pair => pair.Value is null || pair.Value.IsUnavailable).Select(pair => pair.Key).Order().ToArray(),
            comments,
            options.RepositoryPath);
        var result = await author.WriteAsync(request, cancellationToken);
        if (!result.Succeeded || string.IsNullOrWhiteSpace(result.Markdown))
        {
            logger.LogWarning(CowLogEvents.NoteFailed, "note_failed work_item_id={WorkItemId} reason={Reason} error={Error}", id, reason, redactor.Redact(result.Error));
            return false;
        }

        var syncedAt = DateTimeOffset.UtcNow;
        var content = BuildNoteHeader(snapshot, fingerprint, syncedAt) + redactor.Redact(result.Markdown);
        var findings = contentScanner.Find(content);
        if (findings.Count > 0)
        {
            logger.LogWarning(CowLogEvents.NoteFailed, "note_failed work_item_id={WorkItemId} reason=sensitive_content findings={Findings}", id, string.Join(",", findings));
            return false;
        }

        var hash = await notes.WriteAtomicAsync(id, content, cancellationToken);
        await store.UpsertAsync(new NoteSyncRecord(id, fingerprint, hash, syncedAt), cancellationToken);
        logger.LogInformation(CowLogEvents.NotePatched, "note_patched work_item_id={WorkItemId} reason={Reason} revision={Revision}", id, reason, snapshot.Revision);
        return true;
    }

    internal static string BuildNoteHeader(CloudWorkItemSnapshot snapshot, string fingerprint, DateTimeOffset syncedAt) =>
        string.Create(CultureInfo.InvariantCulture,
            $"<!-- farm-sandbox-cows: generated from Azure DevOps (source of truth); local edits are overwritten. work-item={snapshot.WorkItem.Id} revision={snapshot.Revision} fingerprint={fingerprint} synced-at={syncedAt:O} -->{Environment.NewLine}");
}
