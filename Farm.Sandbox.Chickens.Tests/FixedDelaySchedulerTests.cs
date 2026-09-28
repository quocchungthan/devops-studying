using Xunit;

namespace Farm.Sandbox.Chickens.Tests;

public sealed class FixedDelaySchedulerTests
{
    [Fact]
    public async Task Slow_cycles_do_not_overlap()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var active = 0;
        var maximumActive = 0;
        var completed = 0;

        async Task Cycle(CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, current);
            await Task.Delay(75, cancellationToken);
            Interlocked.Decrement(ref active);
            if (Interlocked.Increment(ref completed) == 2)
            {
                cancellation.Cancel();
            }
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FixedDelayScheduler.RunAsync(Cycle, TimeSpan.FromMilliseconds(10), true, cancellation.Token));

        Assert.Equal(1, maximumActive);
        Assert.Equal(2, completed);
    }

    [Fact]
    public async Task Delay_starts_after_cycle_completion()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var completions = new List<DateTimeOffset>();
        var starts = new List<DateTimeOffset>();

        async Task Cycle(CancellationToken cancellationToken)
        {
            starts.Add(DateTimeOffset.UtcNow);
            await Task.Delay(60, cancellationToken);
            completions.Add(DateTimeOffset.UtcNow);
            if (starts.Count == 2) cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FixedDelayScheduler.RunAsync(Cycle, TimeSpan.FromMilliseconds(80), true, cancellation.Token));

        Assert.True(starts[1] - completions[0] >= TimeSpan.FromMilliseconds(60));
    }

    [Fact]
    public async Task Cancellation_during_delay_prevents_another_cycle()
    {
        using var cancellation = new CancellationTokenSource();
        var cycles = 0;
        async Task Cycle(CancellationToken _)
        {
            cycles++;
            cancellation.Cancel();
            await Task.CompletedTask;
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FixedDelayScheduler.RunAsync(Cycle, TimeSpan.FromMinutes(1), true, cancellation.Token));

        Assert.Equal(1, cycles);
    }
}