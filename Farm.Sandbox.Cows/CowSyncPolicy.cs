using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Farm.Core.Chickens;
using Farm.Core.Cows;

namespace Farm.Sandbox.Cows;

public enum CowSyncAction
{
    Keep,
    Patch,
    MarkRemoved,
    Defer
}

public enum CowSyncReason
{
    Unchanged,
    AlreadyRemoved,
    New,
    CloudChanged,
    NoteMissing,
    NoteEditedLocally,
    NoteExpired,
    RemovedFromCloud,
    OutOfScope,
    CloudPartiallyUnavailable
}

// ClearRemovedMarker: the item was marked removed/out of scope and is back; clear the marker before acting.
public sealed record CowSyncDecision(CowSyncAction Action, CowSyncReason Reason, bool ClearRemovedMarker = false);

public static class CowSyncPolicy
{
    public const string RemovedFingerprint = "removed";

    public static CowSyncDecision Decide(
        bool removedFromCloud,
        string? cloudFingerprint,
        bool cloudComplete,
        NoteSyncRecord? stored,
        string? currentNoteHash,
        DateTimeOffset now = default,
        TimeSpan maxNoteAge = default)
    {
        if (removedFromCloud)
        {
            return stored?.RemovedAt is not null
                ? new(CowSyncAction.Keep, CowSyncReason.AlreadyRemoved)
                : new(CowSyncAction.MarkRemoved, CowSyncReason.RemovedFromCloud);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(cloudFingerprint);
        var decision = DecidePresent(cloudFingerprint, cloudComplete, stored, currentNoteHash, now, maxNoteAge);
        return stored?.RemovedAt is not null ? decision with { ClearRemovedMarker = true } : decision;
    }

    private static CowSyncDecision DecidePresent(
        string cloudFingerprint,
        bool cloudComplete,
        NoteSyncRecord? stored,
        string? currentNoteHash,
        DateTimeOffset now,
        TimeSpan maxNoteAge)
    {
        // A transient PR lookup failure must not rewrite an existing (tracked or hand-written) note from a degraded snapshot.
        if (!cloudComplete && (stored is not null || currentNoteHash is not null))
        {
            return new(CowSyncAction.Defer, CowSyncReason.CloudPartiallyUnavailable);
        }

        if (stored is null)
        {
            return new(CowSyncAction.Patch, CowSyncReason.New);
        }

        if (currentNoteHash is null)
        {
            return new(CowSyncAction.Patch, CowSyncReason.NoteMissing);
        }

        if (!string.Equals(stored.Fingerprint, cloudFingerprint, StringComparison.Ordinal))
        {
            return new(CowSyncAction.Patch, CowSyncReason.CloudChanged);
        }

        if (!string.Equals(stored.NoteHash, currentNoteHash, StringComparison.OrdinalIgnoreCase))
        {
            return new(CowSyncAction.Patch, CowSyncReason.NoteEditedLocally);
        }

        // Sources without a cheap fingerprint (Miro, repository code) are refreshed by age.
        return maxNoteAge > TimeSpan.Zero && now - stored.SyncedAt >= maxNoteAge
            ? new(CowSyncAction.Patch, CowSyncReason.NoteExpired)
            : new(CowSyncAction.Keep, CowSyncReason.Unchanged);
    }

    // Tracked or locally noted items that are no longer assigned/related: record it once, keep the note.
    public static CowSyncDecision DecideOutOfScope(NoteSyncRecord? stored) =>
        stored?.RemovedAt is not null
            ? new(CowSyncAction.Keep, CowSyncReason.AlreadyRemoved)
            : new(CowSyncAction.MarkRemoved, CowSyncReason.OutOfScope);

    public static bool IsRemoved(CloudWorkItemSnapshot? snapshot) =>
        snapshot is null || string.Equals(snapshot.WorkItem.State, "Removed", StringComparison.OrdinalIgnoreCase);

    public static string ComputeFingerprint(
        CloudWorkItemSnapshot snapshot,
        IReadOnlyDictionary<int, CloudPullRequestState?> pullRequests,
        IReadOnlyList<WorkItemComment> comments)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(pullRequests);
        ArgumentNullException.ThrowIfNull(comments);
        var builder = new StringBuilder()
            .Append(snapshot.WorkItem.Id.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(snapshot.Revision.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(snapshot.WorkItem.ChangedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n')
            .Append(snapshot.WorkItem.State).Append('\n');
        foreach (var relation in snapshot.WorkItem.Relations
                     .Select(relation => $"{relation.RelationType}|{relation.Url}")
                     .Order(StringComparer.Ordinal))
        {
            builder.Append("rel:").Append(relation).Append('\n');
        }

        foreach (var id in snapshot.LinkedPullRequestIds.Order())
        {
            var state = pullRequests.GetValueOrDefault(id);
            builder.Append("pr:").Append(id.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append(state?.Status ?? "pending").Append('|')
                .Append(state?.SourceCommitId).Append('|')
                .Append(state?.ClosedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('\n');
        }

        builder.Append("comments:").Append(ComputeCommentsHash(comments)).Append('\n');
        return "cow-v2:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string ComputeCommentsHash(IReadOnlyList<WorkItemComment> comments)
    {
        var builder = new StringBuilder();
        foreach (var comment in comments.OrderBy(comment => comment.Id))
        {
            builder.Append(comment.Id.ToString(CultureInfo.InvariantCulture)).Append('|')
                .Append((comment.ModifiedAt ?? comment.PublishedAt).ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append('|')
                .Append(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(comment.Content)))).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
