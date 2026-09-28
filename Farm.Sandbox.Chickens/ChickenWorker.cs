using Microsoft.Extensions.Hosting;
using Farm.Core.Chickens;

namespace Farm.Sandbox.Chickens;

public sealed class ChickenWorker(
    ChickenRunner runner,
    ChickenOptions options,
    ISensitiveDataRedactor redactor,
    ChickenStatusWriter statusWriter,
    ILogger<ChickenWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation(ChickenLogEvents.Startup, "startup_configuration_summary status_path={StatusPath} artifacts_path={ArtifactsPath} schedule_seconds={ScheduleSeconds} push_enabled={PushEnabled}", options.StatusPath, options.ArtifactsPath, options.SchedulePeriod.TotalSeconds, options.EnablePush);
            await statusWriter.RecordStartupAsync(options, stoppingToken);
            await statusWriter.RecordStartedAsync(stoppingToken);
            await FixedDelayScheduler.RunAsync(
                RunSafelyAsync,
                options.SchedulePeriod,
                options.RunImmediately,
                stoppingToken);
        }
        finally
        {
            await statusWriter.RecordStoppingAsync(CancellationToken.None);
            await statusWriter.RecordStoppedAsync(CancellationToken.None);
        }
    }

    private async Task RunSafelyAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await statusWriter.RecordCycleStartedAsync(startedAt, cancellationToken);
        logger.LogInformation(ChickenLogEvents.CycleStarted, "cycle_started at={StartedAt}", startedAt);
        try
        {
            var result = await runner.RunOnceAsync(cancellationToken);
            stopwatch.Stop();
            await statusWriter.RecordCycleCompletedAsync(DateTimeOffset.UtcNow, result, cancellationToken);
            logger.LogInformation(ChickenLogEvents.CycleCompleted, "cycle_completed duration_ms={DurationMs} candidate_count={CandidateCount} completed_count={CompletedCount} deferred_count={DeferredCount} failed_count={FailedCount}", stopwatch.ElapsedMilliseconds, result.CandidateCount, result.CompletedCount, result.DeferredCount, result.FailedCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            await statusWriter.RecordCycleFailedAsync(DateTimeOffset.UtcNow, exception, CancellationToken.None);
            logger.LogError(
                ChickenLogEvents.CycleFailed,
                "cycle_failed duration_ms={DurationMs} error={Error}; next cycle remains scheduled",
                stopwatch.ElapsedMilliseconds,
                redactor.Redact(exception.ToString()));
        }
    }
}