using Farm.Core.Chickens;
using System.Text;
using Xunit;

namespace Farm.Core.Tests;

public sealed class ReviewWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fingerprint_is_stable_across_thread_order()
    {
        var first = Thread(2, "reviewer", Now.AddHours(-3));
        var second = Thread(1, "reviewer", Now.AddHours(-4));

        var forward = FeedbackFingerprint.Create([first, second]);
        var reverse = FeedbackFingerprint.Create([second, first]);

        Assert.Equal(forward, reverse);
    }

    [Fact]
    public void Fingerprint_does_not_collide_when_field_delimiters_move()
    {
        var first = new FeedbackThread(1, false, null,
            [new FeedbackComment(2, "a|b", "reviewer", "c", Now)]);
        var second = new FeedbackThread(1, false, null,
            [new FeedbackComment(2, "a", "reviewer", "b|c", Now)]);

        Assert.NotEqual(FeedbackFingerprint.Create([first]), FeedbackFingerprint.Create([second]));
    }

    [Fact]
    public void Eligibility_fingerprint_ignores_authors_own_comments()
    {
        var external = Thread(1, "reviewer", Now.AddHours(-2));
        var withAuthorNoise = external with
        {
            Comments = [.. external.Comments, new FeedbackComment(99, "author", "author", "follow-up", Now.AddHours(-1))]
        };

        var baseline = ReviewEligibilityEvaluator.Evaluate(Candidate([external]), "author", Now, TimeSpan.FromMinutes(30));
        var noisy = ReviewEligibilityEvaluator.Evaluate(Candidate([withAuthorNoise]), "author", Now, TimeSpan.FromMinutes(30));

        Assert.Equal(baseline.Fingerprint, noisy.Fingerprint);
    }

    [Fact]
    public void Eligibility_requires_authorship_external_feedback_and_a_quiet_period()
    {
        var candidate = Candidate([Thread(1, "reviewer", Now.AddHours(-2))]);

        var result = ReviewEligibilityEvaluator.Evaluate(candidate, "author", Now, TimeSpan.FromHours(1));

        Assert.True(result.IsEligible);
        Assert.Equal(ReviewEligibilityReason.Eligible, result.Reason);
    }

    [Fact]
    public void Eligibility_defers_recent_external_feedback()
    {
        var candidate = Candidate([Thread(1, "reviewer", Now.AddMinutes(-30))]);

        var result = ReviewEligibilityEvaluator.Evaluate(candidate, "author", Now, TimeSpan.FromHours(1));

        Assert.False(result.IsEligible);
        Assert.Equal(ReviewEligibilityReason.FeedbackStillCoolingDown, result.Reason);
    }

    [Fact]
    public void Redactor_removes_raw_encoded_authorization_and_url_userinfo_secrets()
    {
        const string secret = "sentinel:p@ssword";
        var redactor = new SensitiveDataRedactor([secret]);
        var text = string.Join('\n',
            secret,
            Uri.EscapeDataString(secret),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)),
            $"Authorization: Bearer {secret}",
            string.Concat("https://user:", secret, "@", "example.test/repo"));

        var result = redactor.Redact(text);

        Assert.DoesNotContain(secret, result);
        Assert.DoesNotContain(Uri.EscapeDataString(secret), result);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)), result);
        Assert.DoesNotContain("user:", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void Sensitive_content_scanner_detects_supported_secret_signatures()
    {
        const string configured = "configured-secret-sentinel";
        var scanner = new SensitiveContentScanner([configured]);
        string[] samples =
        [
            configured,
            "github_" + "pat_" + new string('A', 24),
            new string('A', 75) + "AZDO" + new string('B', 5),
            new string('C', 52),
            "eyJ" + new string('A', 12) + "." + new string('B', 12) + "." + new string('C', 12),
            "-----BEGIN " + "PRIVATE KEY-----",
            string.Concat("Server=db;", "Password", "=", "secret-value"),
            "api_" + "key = \"" + "secret-value" + "\"",
            "https://user:" + "secret-value" + "@example.test/repo"
        ];

        Assert.All(samples, sample => Assert.NotEmpty(scanner.Find(sample)));
    }

    [Fact]
    public void Sensitive_content_scanner_accepts_ordinary_code()
    {
        var scanner = new SensitiveContentScanner([]);

        var findings = scanner.Find("var token = tokenProvider.GetToken();\nconnection.Open();\nAssert.Equal(expected, actual);");

        Assert.Empty(findings);
    }

    [Fact]
    public void Work_item_fingerprint_is_stable_across_traversal_and_comment_order()
    {
        var first = WorkItem(1, [WorkItemNote(2, "reviewer"), WorkItemNote(1, "reviewer")]);
        var second = WorkItem(2, [WorkItemNote(3, "reviewer")]);

        var forward = WorkItemFingerprint.Create(Context([first, second]), "author");
        var reverse = WorkItemFingerprint.Create(
            Context([second, first with { Comments = [.. first.Comments.Reverse()] }]), "author");

        Assert.Equal(forward.Value, reverse.Value);
        Assert.StartsWith($"{WorkItemFingerprint.FormatVersion}:", forward.Value);
    }

    [Fact]
    public void Work_item_fingerprint_ignores_non_content_fields_and_own_comments()
    {
        var item = WorkItem(1, [WorkItemNote(1, "reviewer")]);
        var baseline = WorkItemFingerprint.Create(Context([item]), "author");

        var noisy = WorkItemFingerprint.Create(Context([item with
        {
            State = "Closed",
            ChangedAt = Now,
            Depth = 2,
            RelationPath = "elsewhere",
            Relations = [new WorkItemRelation("System.LinkTypes.Related", new Uri("https://example/wi/9"), null)],
            Comments = [.. item.Comments, WorkItemNote(2, "AUTHOR")]
        }]), "author");

        Assert.Equal(baseline.Value, noisy.Value);
    }

    [Fact]
    public void Work_item_fingerprint_changes_when_item_enters_or_leaves_the_set()
    {
        var one = WorkItemFingerprint.Create(Context([WorkItem(1, [])]));
        var two = WorkItemFingerprint.Create(Context([WorkItem(1, []), WorkItem(2, [])]));

        Assert.NotEqual(one.Value, two.Value);
        Assert.Equal([2], two.ChangedSince(one.ItemHashes));
        Assert.Equal([2], one.ChangedSince(two.ItemHashes));
    }

    [Fact]
    public void Work_item_fingerprint_keeps_baseline_hashes_for_skipped_or_missing_items()
    {
        var clean = WorkItemFingerprint.Create(Context([WorkItem(1, []), WorkItem(2, [WorkItemNote(1, "reviewer")])]));

        var missing = WorkItemFingerprint.Create(
            Context([WorkItem(1, [])], new WorkItemContextSkip(2, 1, "work_item_unavailable")), null, clean.ItemHashes);
        var partial = WorkItemFingerprint.Create(
            Context([WorkItem(1, []), WorkItem(2, [])], new WorkItemContextSkip(2, 1, "comments_unavailable")), null, clean.ItemHashes);
        var noBaseline = WorkItemFingerprint.Create(
            Context([WorkItem(1, [])], new WorkItemContextSkip(2, 1, "work_item_unavailable")));

        Assert.Equal(clean.Value, missing.Value);
        Assert.Equal(clean.Value, partial.Value);
        Assert.Equal([1], noBaseline.ItemHashes.Keys);
    }

    [Fact]
    public void Combined_fingerprint_is_hex_and_tracks_work_item_changes()
    {
        var pr = FeedbackFingerprint.Create([Thread(1, "reviewer", Now)]);
        var a = pr.WithWorkItems(WorkItemFingerprint.Create(Context([WorkItem(1, [])])));
        var b = pr.WithWorkItems(WorkItemFingerprint.Create(Context([WorkItem(1, [WorkItemNote(1, "reviewer")])])));

        Assert.Matches("^[0-9a-f]{64}$", a.Value);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Work_item_latest_activity_counts_external_comments_and_changed_item_edits_only()
    {
        var item = WorkItem(1, [WorkItemNote(1, "reviewer") with { ModifiedAt = Now.AddMinutes(-10) }, WorkItemNote(2, "author") with { PublishedAt = Now }])
            with { ChangedAt = Now.AddMinutes(-1) };

        Assert.Equal(Now.AddMinutes(-10), WorkItemFingerprint.LatestActivity(Context([item]), "author", []));
        Assert.Equal(Now.AddMinutes(-1), WorkItemFingerprint.LatestActivity(Context([item]), "author", [1]));
    }

    private static ReviewContext Context(IReadOnlyList<WorkItemContext> items, params WorkItemContextSkip[] skipped) =>
        new(Candidate([]), items, skipped);

    private static WorkItemContext WorkItem(int id, IReadOnlyList<WorkItemComment> comments) => new(
        id, $"Item {id}", "Active", "description", comments, [], [], "Bug", "repro", "criteria");

    private static WorkItemComment WorkItemNote(int id, string authorId) =>
        new(id, authorId, $"comment {id}", Now.AddHours(-3), authorId, 1);

    private static ReviewCandidate Candidate(IReadOnlyList<FeedbackThread> threads) => new(
        "org", "project", "repo-id", "repo", new Uri("https://example/repo"), 42, "PR", "refs/heads/feature",
        "refs/heads/main", "abc", "author", threads, []);

    private static FeedbackThread Thread(int id, string authorId, DateTimeOffset publishedAt) => new(
        id, false, new FeedbackAnchor("file.cs", 10, 10, "abc"),
        [new FeedbackComment(id, authorId, authorId, "feedback", publishedAt)]);
}