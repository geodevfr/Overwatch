namespace Overwatch.Watchdog;

public readonly record struct WatchdogSnapshot(
    long Samples,
    long OverThreshold,
    TimeSpan Last,
    TimeSpan MaxSincePreviousSnapshot,
    string Offender);

/// <summary>
/// Mesure le temps passé dans le décodeur, hors du thread qui relaie les sockets.
/// </summary>
public sealed class DecoderWatchdog
{
    private readonly object _gate = new();
    private readonly long _thresholdTicks;
    private long _lastTicks;
    private long _maxTicks;
    private long _samples;
    private long _overThreshold;
    private string _offender = "";

    public DecoderWatchdog(TimeSpan threshold)
    {
        if (threshold <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(threshold));
        _thresholdTicks = threshold.Ticks;
    }

    public void Record(TimeSpan elapsed, string packetId = "inconnu")
    {
        var ticks = elapsed.Ticks;
        lock (_gate)
        {
            _samples++;
            _lastTicks = ticks;
            if (ticks > _thresholdTicks)
                _overThreshold++;
            if (ticks >= _maxTicks)
            {
                _maxTicks = ticks;
                if (ticks > _thresholdTicks)
                    _offender = string.IsNullOrWhiteSpace(packetId) ? "inconnu" : packetId;
            }
        }
    }

    public WatchdogSnapshot SnapshotAndResetMax()
    {
        lock (_gate)
        {
            var snapshot = new WatchdogSnapshot(
                _samples,
                _overThreshold,
                TimeSpan.FromTicks(_lastTicks),
                TimeSpan.FromTicks(_maxTicks),
                _offender);
            _maxTicks = 0;
            _offender = "";
            return snapshot;
        }
    }
}
