using Farm.Core.Chickens;
using Microsoft.Extensions.Hosting;

namespace Farm.Sandbox.Cows;

public sealed class CowWorker(
    CowRunner runner,
    CowOptions options,
    ISensitiveDataRedactor redactor,
    CowStatusWriter statusWriter,
    ILogger<CowWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            logger.LogInformation(CowLogEvents.Startup, "startup_configuration_summary status_path={StatusPath} notes_path={NotesPath} schedule_seconds={ScheduleSeconds} max_patches_per_run={MaxPatches}", options.StatusPath, options.NotesPath, options.SchedulePeriod.TotalSeconds, options.MaxPatchesPerRun);
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
        logger.LogInformation(CowLogEvents.CycleStarted, "cycle_started at={StartedAt}", startedAt);
        try
        {
            var result = await runner.RunOnceAsync(cancellationToken);
            stopwatch.Stop();
            await statusWriter.RecordCycleCompletedAsync(DateTimeOffset.UtcNow, result, cancellationToken);
            logger.LogInformation(CowLogEvents.CycleCompleted, "cycle_completed duration_ms={DurationMs} target_count={TargetCount} kept_count={KeptCount} patched_count={PatchedCount} removed_count={RemovedCount} deferred_count={DeferredCount} failed_count={FailedCount}", stopwatch.ElapsedMilliseconds, result.TargetCount, result.KeptCount, result.PatchedCount, result.RemovedCount, result.DeferredCount, result.FailedCount);
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
                CowLogEvents.CycleFailed,
                "cycle_failed duration_ms={DurationMs} error={Error}; next cycle remains scheduled",
                stopwatch.ElapsedMilliseconds,
                redactor.Redact(exception.ToString()));
        }
    }
}
