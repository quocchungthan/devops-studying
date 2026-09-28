using System.Text.Json;
using Farm.Core.Chickens;
using Xunit;

namespace Farm.Sandbox.Chickens.Tests;

public sealed class ChickenStatusWriterTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"chicken-status-{Guid.NewGuid():N}");

    [Fact]
    public async Task Writes_redacted_atomic_status_content()
    {
        var path = Path.Combine(root, "state", "status.json");
        var writer = new ChickenStatusWriter(path, new SensitiveDataRedactor(["secret-token"]));
        var options = new ChickenOptions { ArtifactsPath = Path.Combine(root, "artifacts"), StatusPath = path, EnablePush = true };

        await writer.RecordStartupAsync(options);
        await writer.RecordCycleStartedAsync(DateTimeOffset.UtcNow);
        await writer.RecordCandidateAsync(42, "secret-token");
        await writer.RecordCycleFailedAsync(DateTimeOffset.UtcNow, new InvalidOperationException("secret-token failed"));

        var json = await File.ReadAllTextAsync(path);
        var status = JsonSerializer.Deserialize<ChickenStatusSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(status);
        Assert.Equal("degraded", status!.ServiceStatus);
        Assert.Equal(42, status.CurrentPullRequestId);
        Assert.Equal("[REDACTED]", status.CurrentAttempt);
        Assert.Equal("[REDACTED] failed", status.ErrorSummary);
        Assert.DoesNotContain("secret-token", json, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.NotNull(status.LastUpdatedAt);
        Assert.NotNull(status.LastHeartbeatAt);
    }

    [Fact]
    public async Task Startup_marks_stale_running_snapshot_as_degraded()
    {
        var path = Path.Combine(root, "state", "status.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ChickenStatusSnapshot
        {
            ServiceStatus = "running",
            LastHeartbeatAt = DateTimeOffset.UtcNow.AddHours(-2)
        }));
        var writer = new ChickenStatusWriter(path, SensitiveDataRedactor.Empty);
        var options = new ChickenOptions { ArtifactsPath = Path.Combine(root, "artifacts"), StatusPath = path, SchedulePeriod = TimeSpan.FromHours(1) };

        await writer.RecordStartupAsync(options);

        var status = JsonSerializer.Deserialize<ChickenStatusSnapshot>(await File.ReadAllTextAsync(path), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("degraded", status!.ServiceStatus);
        Assert.NotNull(status.LastStaleAt);
        Assert.Contains("stale", status.ErrorSummary, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}