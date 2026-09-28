namespace Farm.Sandbox.Chickens;

public static class FixedDelayScheduler
{
    public static async Task RunAsync(
        Func<CancellationToken, Task> action,
        TimeSpan period,
        bool runImmediately,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period));
        }

        if (!runImmediately)
        {
            await Task.Delay(period, cancellationToken);
        }

        while (true)
        {
            await action(cancellationToken);
            await Task.Delay(period, cancellationToken);
        }
    }
}