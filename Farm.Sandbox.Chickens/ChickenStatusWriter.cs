using System.Text.Json;
using Farm.Core.Chickens;

namespace Farm.Sandbox.Chickens;

public sealed record ChickenCycleResult(
    int CandidateCount,
    int CompletedCount,
    int DeferredCount,
    int FailedCount);

public sealed class ChickenStatusWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string path;
    private readonly ISensitiveDataRedactor redactor;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ChickenStatusSnapshot status = new();

    public ChickenStatusWriter(string path, ISensitiveDataRedactor redactor)
    {
        this.path = Path.GetFullPath(path);
        this.redactor = redactor;
    }

    public async Task RecordStartupAsync(ChickenOptions options, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            status = ReadExistingStatus();
            var wasStale = status.ServiceStatus == "running" &&
                (!status.LastHeartbeatAt.HasValue || DateTimeOffset.UtcNow - status.LastHeartbeatAt.Value >
                    TimeSpan.FromTicks(Math.Max(options.SchedulePeriod.Ticks * 2, TimeSpan.FromMinutes(5).Ticks)));
            status.StatusPath = path;
            status.ArtifactsPath = options.ArtifactsPath;
            status.ScheduleSeconds = (int)options.SchedulePeriod.TotalSeconds;
            status.PushEnabled = options.EnablePush;
            if (wasStale)
            {
                status.ServiceStatus = "degraded";
                status.LastStaleAt = DateTimeOffset.UtcNow;
                status.ErrorSummary = "Previous running status is stale; no external supervisor can confirm a hard-killed process.";
            }
            else
            {
                status.ServiceStatus = "starting";
                status.ErrorSummary = null;
            }

            await WriteAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public Task RecordStartedAsync(CancellationToken cancellationToken = default) =>
        UpdateAsync(current =>
        {
            current.ServiceStatus = "running";
            current.ErrorSummary = null;
        }, cancellationToken);

    public Task RecordStoppingAsync(CancellationToken cancellationToken = default) =>
        UpdateAsync(current => current.ServiceStatus = "stopping", cancellationToken);

    public Task RecordStoppedAsync(CancellationToken cancellationToken = default) =>
        UpdateAsync(current =>
        {
            current.ServiceStatus = "stopped";
            current.CurrentPullRequestId = null;
            current.CurrentAttempt = null;
        }, cancellationToken);

    public Task RecordCycleStartedAsync(DateTimeOffset startedAt, CancellationToken cancellationToken = default) =>
        UpdateAsync(current =>
        {
            current.LastCycleStartedAt = startedAt;
            current.CurrentPullRequestId = null;
            current.CurrentAttempt = null;
            current.ErrorSummary = null;
        }, cancellationToken);

    public Task RecordCycleCompletedAsync(DateTimeOffset completedAt, ChickenCycleResult result, CancellationToken cancellationToken = default) =>
        UpdateAsync(current =>
        {
            current.ServiceStatus = "running";
            current.LastCycleCompletedAt = completedAt;
            current.LastSuccessAt = completedAt;
            current.CandidateCount = result.CandidateCount;
            current.CompletedCount = result.CompletedCount;
            current.DeferredCount = result.DeferredCount;
            current.FailedCount = result.FailedCount;
            current.CurrentPullRequestId = null;
            current.CurrentAttempt = null;
            current.ErrorSummary = null;
        }, cancellationToken);

    public Task RecordCycleFailedAsync(DateTimeOffset failedAt, Exception exception, CancellationToken cancellationToken = default) =>
        UpdateAsync(current =>
        {
            current.ServiceStatus = "degraded";
            current.LastFailureAt = failedAt;
            current.ErrorSummary = redactor.Redact(exception.Message);
        }, cancellationToken);

    public Task RecordCandidateAsync(int pullRequestId, string attempt, CancellationToken cancellationToken = default) =>
        UpdateAsync(current =>
        {
            current.CurrentPullRequestId = pullRequestId;
            current.CurrentAttempt = redactor.Redact(attempt);
        }, cancellationToken);

    public Task ClearCurrentCandidateAsync(CancellationToken cancellationToken = default) =>
        UpdateAsync(current =>
        {
            current.CurrentPullRequestId = null;
            current.CurrentAttempt = null;
        }, cancellationToken);

    private async Task UpdateAsync(Action<ChickenStatusSnapshot> update, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            update(status);
            await WriteAsync(cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        status.LastUpdatedAt = now;
        status.LastHeartbeatAt = now;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var json = redactor.Redact(JsonSerializer.Serialize(status, JsonOptions));
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private ChickenStatusSnapshot ReadExistingStatus()
    {
        if (!File.Exists(path))
        {
            return new ChickenStatusSnapshot();
        }

        try
        {
            return JsonSerializer.Deserialize<ChickenStatusSnapshot>(File.ReadAllText(path), JsonOptions)
                ?? new ChickenStatusSnapshot();
        }
        catch (JsonException)
        {
            return new ChickenStatusSnapshot();
        }
    }
}

public sealed class ChickenStatusSnapshot
{
    public string ServiceStatus { get; set; } = "starting";
    public string? StatusPath { get; set; }
    public string? ArtifactsPath { get; set; }
    public int ScheduleSeconds { get; set; }
    public bool PushEnabled { get; set; }
    public DateTimeOffset? LastCycleStartedAt { get; set; }
    public DateTimeOffset? LastUpdatedAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? LastCycleCompletedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public DateTimeOffset? LastStaleAt { get; set; }
    public int? CurrentPullRequestId { get; set; }
    public string? CurrentAttempt { get; set; }
    public int CandidateCount { get; set; }
    public int CompletedCount { get; set; }
    public int DeferredCount { get; set; }
    public int FailedCount { get; set; }
    public string? ErrorSummary { get; set; }
}