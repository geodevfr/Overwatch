namespace GameDiag.Watchdog;

public readonly record struct WatchdogSnapshot(
    long Samples,
    long OverThreshold,
    TimeSpan Last,
    TimeSpan MaxSincePreviousSnapshot);

/// <summary>
/// Mesure le temps passé dans le décodeur, hors du thread qui relaie les sockets.
/// </summary>
public sealed class DecoderWatchdog
{
    private readonly long _thresholdTicks;
    private long _lastTicks;
    private long _maxTicks;
    private long _samples;
    private long _overThreshold;

    public DecoderWatchdog(TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        _thresholdTicks = threshold.Ticks;
    }

    public void Record(TimeSpan elapsed)
    {
        var ticks = elapsed.Ticks;
        Interlocked.Exchange(ref _lastTicks, ticks);
        UpdateMax(ticks);
        Interlocked.Increment(ref _samples);
        if (ticks > _thresholdTicks)
            Interlocked.Increment(ref _overThreshold);
    }

    public WatchdogSnapshot SnapshotAndResetMax()
    {
        var max = Interlocked.Exchange(ref _maxTicks, 0);
        return new WatchdogSnapshot(
            Interlocked.Read(ref _samples),
            Interlocked.Read(ref _overThreshold),
            TimeSpan.FromTicks(Interlocked.Read(ref _lastTicks)),
            TimeSpan.FromTicks(max));
    }

    private void UpdateMax(long ticks)
    {
        while (true)
        {
            var current = Interlocked.Read(ref _maxTicks);
            if (ticks <= current)
                return;
            if (Interlocked.CompareExchange(ref _maxTicks, ticks, current) == current)
                return;
        }
    }
}
