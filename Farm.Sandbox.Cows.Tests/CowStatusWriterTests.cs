using System.Text.Json;
using Farm.Core.Chickens;
using Xunit;

namespace Farm.Sandbox.Cows.Tests;

public sealed class CowStatusWriterTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), $"cow-status-{Guid.NewGuid():N}");

    [Fact]
    public async Task Writes_redacted_atomic_status_with_cycle_counts()
    {
        var path = Path.Combine(root, "state", "status.json");
        var writer = new CowStatusWriter(path, new SensitiveDataRedactor(["secret-token"]));
        var options = new CowOptions { NotesPath = Path.Combine(root, "notes"), StatusPath = path };

        await writer.RecordStartupAsync(options);
        await writer.RecordCycleStartedAsync(DateTimeOffset.UtcNow);
        await writer.RecordCycleCompletedAsync(DateTimeOffset.UtcNow, new CowCycleResult(6, 2, 1, 1, 1, 1));
        await writer.RecordCycleFailedAsync(DateTimeOffset.UtcNow, new InvalidOperationException("secret-token failed"));

        var json = await File.ReadAllTextAsync(path);
        var status = JsonSerializer.Deserialize<CowStatusSnapshot>(json, JsonOptions)!;
        Assert.Equal("degraded", status.ServiceStatus);
        Assert.Equal(7200, status.ScheduleSeconds);
        Assert.Equal((6, 2, 1, 1, 1, 1), (status.TargetCount, status.KeptCount, status.PatchedCount, status.RemovedCount, status.DeferredCount, status.FailedCount));
        Assert.Equal("[REDACTED] failed", status.ErrorSummary);
        Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
    }

    [Fact]
    public async Task Startup_marks_stale_running_snapshot_as_degraded()
    {
        var path = Path.Combine(root, "state", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new CowStatusSnapshot
        {
            ServiceStatus = "running",
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-5)
        }, JsonOptions));
        var writer = new CowStatusWriter(path, SensitiveDataRedactor.Empty);

        await writer.RecordStartupAsync(new CowOptions { StatusPath = path });

        var status = JsonSerializer.Deserialize<CowStatusSnapshot>(await File.ReadAllTextAsync(path), JsonOptions)!;
        Assert.Equal("degraded", status.ServiceStatus);
        Assert.NotNull(status.LastStaleAt);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
