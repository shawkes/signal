namespace Signal.Native.Engine;

public readonly record struct TempoChange(long Tick, int MicrosecondsPerQuarterNote);

public sealed class TempoMap
{
    public const int DefaultTicksPerQuarter = 480;
    private const int DefaultMicrosecondsPerQuarter = 500_000;
    private readonly TempoChange[] _changes;
    private readonly double[] _changeSeconds;

    public TempoMap(int ticksPerQuarter, IEnumerable<TempoChange> changes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerQuarter);
        TicksPerQuarter = ticksPerQuarter;

        var ordered = changes.OrderBy(change => change.Tick).ToList();
        if (ordered.Count == 0 || ordered[0].Tick > 0)
        {
            ordered.Insert(0, new TempoChange(0, DefaultMicrosecondsPerQuarter));
        }

        _changes = ordered.ToArray();
        _changeSeconds = new double[_changes.Length];
        for (var i = 0; i < _changes.Length; i++)
        {
            var change = _changes[i];
            if (change.Tick < 0 || change.MicrosecondsPerQuarterNote <= 0 ||
                (i > 0 && change.Tick == _changes[i - 1].Tick))
            {
                throw new ArgumentException("Tempo changes need distinct nonnegative ticks and positive tempos.", nameof(changes));
            }

            if (i > 0)
            {
                var previous = _changes[i - 1];
                _changeSeconds[i] = _changeSeconds[i - 1] + (change.Tick - previous.Tick) * SecondsPerTick(previous);
            }
        }

        Changes = Array.AsReadOnly(_changes);
    }

    public int TicksPerQuarter { get; }
    public IReadOnlyList<TempoChange> Changes { get; }

    public static TempoMap Constant(double bpm, int ticksPerQuarter = DefaultTicksPerQuarter)
    {
        var microseconds = Math.Round(60_000_000 / bpm);
        if (!double.IsFinite(microseconds) || microseconds is < 1 or > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(bpm));
        }

        return new TempoMap(ticksPerQuarter, [new TempoChange(0, (int)microseconds)]);
    }

    public double TickToSeconds(double tick)
    {
        var index = SegmentAt(tick, byTime: false);
        var change = _changes[index];
        return _changeSeconds[index] + (tick - change.Tick) * SecondsPerTick(change);
    }

    public double SecondsToTick(double seconds)
    {
        var index = SegmentAt(seconds, byTime: true);
        var change = _changes[index];
        return change.Tick + (seconds - _changeSeconds[index]) / SecondsPerTick(change);
    }

    public long TickToSample(double tick, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        return checked((long)Math.Round(TickToSeconds(tick) * sampleRate, MidpointRounding.AwayFromZero));
    }

    public double SampleToTick(long frame, int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        return SecondsToTick(frame / (double)sampleRate);
    }

    private double SecondsPerTick(TempoChange change) => change.MicrosecondsPerQuarterNote / (1_000_000.0 * TicksPerQuarter);

    internal double SecondsPerTickAt(long tick) => SecondsPerTick(_changes[SegmentAt(tick, byTime: false)]);

    private int SegmentAt(double position, bool byTime)
    {
        var low = 0;
        var high = _changes.Length;
        while (high - low > 1)
        {
            var middle = low + (high - low) / 2;
            var boundary = byTime ? _changeSeconds[middle] : _changes[middle].Tick;
            if (boundary <= position)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }
}
