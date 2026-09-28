using System.Text.Json;
using Farm.Core.Chickens;

namespace Farm.Sandbox.Cows;

public sealed record CowCycleResult(
    int TargetCount,
    int KeptCount,
    int PatchedCount,
    int RemovedCount,
    int DeferredCount,
    int FailedCount);

public sealed class CowStatusWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string path;
    private readonly ISensitiveDataRedactor redactor;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CowStatusSnapshot status = new();

    public CowStatusWriter(string path, ISensitiveDataRedactor redactor)
    {
        this.path = Path.GetFullPath(path);
        this.redactor = redactor;
    }

    public async Task RecordStartupAsync(CowOptions options, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            status = ReadExistingStatus();
            var wasStale = status.ServiceStatus == "running" &&
                (!status.LastHeartbeatAt.HasValue || DateTimeOffset.UtcNow - status.LastHeartbeatAt.Value >
                    TimeSpan.FromTicks(Math.Max(options.SchedulePeriod.Ticks * 2, TimeSpan.FromMinutes(5).Ticks)));
            status.StatusPath = path;
            status.NotesPath = options.NotesPath;
            status.ScheduleSeconds = (int)options.SchedulePeriod.TotalSeconds;
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
            current.CurrentWorkItemId = null;
        }, cancellationToken);

    public Task RecordCycleStartedAsync(DateTimeOffset startedAt, CancellationToken cancellationToken = default) =>
        UpdateAsync(current =>
        {
            current.LastCycleStartedAt = startedAt;
            current.CurrentWorkItemId = null;
            current.ErrorSummary = null;
        }, cancellationToken);

    public Task RecordCycleCompletedAsync(DateTimeOffset completedAt, CowCycleResult result, CancellationToken cancellationToken = default) =>
        UpdateAsync(current =>
        {
            current.ServiceStatus = "running";
            current.LastCycleCompletedAt = completedAt;
            current.LastSuccessAt = completedAt;
            current.TargetCount = result.TargetCount;
            current.KeptCount = result.KeptCount;
            current.PatchedCount = result.PatchedCount;
            current.RemovedCount = result.RemovedCount;
            current.DeferredCount = result.DeferredCount;
            current.FailedCount = result.FailedCount;
            current.CurrentWorkItemId = null;
            current.ErrorSummary = null;
        }, cancellationToken);

    public Task RecordCycleFailedAsync(DateTimeOffset failedAt, Exception exception, CancellationToken cancellationToken = default) =>
        UpdateAsync(current =>
        {
            current.ServiceStatus = "degraded";
            current.LastFailureAt = failedAt;
            current.CurrentWorkItemId = null;
            current.ErrorSummary = redactor.Redact(exception.Message);
        }, cancellationToken);

    public Task RecordCurrentWorkItemAsync(int? workItemId, CancellationToken cancellationToken = default) =>
        UpdateAsync(current => current.CurrentWorkItemId = workItemId, cancellationToken);

    private async Task UpdateAsync(Action<CowStatusSnapshot> update, CancellationToken cancellationToken)
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

    private CowStatusSnapshot ReadExistingStatus()
    {
        if (!File.Exists(path))
        {
            return new CowStatusSnapshot();
        }

        try
        {
            return JsonSerializer.Deserialize<CowStatusSnapshot>(File.ReadAllText(path), JsonOptions)
                ?? new CowStatusSnapshot();
        }
        catch (JsonException)
        {
            return new CowStatusSnapshot();
        }
    }
}

public sealed class CowStatusSnapshot
{
    public string ServiceStatus { get; set; } = "starting";
    public string? StatusPath { get; set; }
    public string? NotesPath { get; set; }
    public int ScheduleSeconds { get; set; }
    public DateTimeOffset? LastCycleStartedAt { get; set; }
    public DateTimeOffset? LastUpdatedAt { get; set; }
    public DateTimeOffset? LastHeartbeatAt { get; set; }
    public DateTimeOffset? LastCycleCompletedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public DateTimeOffset? LastStaleAt { get; set; }
    public int? CurrentWorkItemId { get; set; }
    public int TargetCount { get; set; }
    public int KeptCount { get; set; }
    public int PatchedCount { get; set; }
    public int RemovedCount { get; set; }
    public int DeferredCount { get; set; }
    public int FailedCount { get; set; }
    public string? ErrorSummary { get; set; }
}
