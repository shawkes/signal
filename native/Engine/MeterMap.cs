namespace Signal.Native.Engine;

public readonly record struct MeterChange(long Tick, SongMeter Meter);

/// <summary>One-based bar and beat, with their absolute starting ticks.</summary>
public readonly record struct MeterPosition(long Bar, int Beat, long BeatTick, long BarTick);

/// <summary>Song-wide time signatures. A change begins a bar, including when it cuts the preceding bar short.</summary>
public sealed class MeterMap
{
    private readonly MeterChange[] _changes;
    private readonly Int128[] _firstBars;
    private readonly long _wholeNoteTicks;

    public MeterMap(int ticksPerQuarter, IEnumerable<MeterChange> changes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerQuarter);
        ArgumentNullException.ThrowIfNull(changes);
        TicksPerQuarter = ticksPerQuarter;
        _wholeNoteTicks = 4L * ticksPerQuarter;
        var ordered = changes.OrderBy(change => change.Tick).ToList();
        if (ordered.Count == 0 || ordered[0].Tick > 0)
            ordered.Insert(0, new MeterChange(0, SongMeter.CommonTime));
        _changes = ordered.ToArray();
        Changes = Array.AsReadOnly(_changes);
        _firstBars = new Int128[_changes.Length];
        _firstBars[0] = 1;
        for (int i = 0; i < _changes.Length; i++)
        {
            var change = _changes[i];
            if (change.Tick < 0 || change.Meter is null || (i > 0 && change.Tick == _changes[i - 1].Tick))
                throw new ArgumentException("Time signatures need distinct nonnegative ticks and a meter.", nameof(changes));
            if (i == 0)
                continue;

            var previous = _changes[i - 1];
            long length = change.Tick - previous.Tick;
            long barUnits = _wholeNoteTicks * previous.Meter.Numerator;
            int denominator = previous.Meter.Denominator;
            // Count the preceding bars, including a final partial bar. At resolutions below
            // a tick per bar several musical bars can pass between representable positions.
            var bars = barUnits >= denominator
                ? UnitAt(length - 1, barUnits, denominator) + 1
                : ((Int128)length * denominator + barUnits - 1) / barUnits;
            _firstBars[i] = _firstBars[i - 1] + bars;
        }
    }

    public int TicksPerQuarter { get; }
    public IReadOnlyList<MeterChange> Changes { get; }
    public SongMeter MeterAt(long tick) => _changes[IndexAt(tick)].Meter;

    public int IndexAt(long tick)
    {
        int low = 0, high = _changes.Length;
        while (low + 1 < high)
        {
            int middle = low + (high - low) / 2;
            if (_changes[middle].Tick <= tick)
                low = middle;
            else
                high = middle;
        }
        return low;
    }

    public int IndexAt(double tick) => IndexAt(long.CreateSaturating(Math.Floor(tick)));

    public MeterPosition PositionAt(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        int index = IndexAt(tick);
        var change = _changes[index];
        var meter = change.Meter;
        long elapsed = tick - change.Tick;
        long barUnits = _wholeNoteTicks * meter.Numerator;
        Int128 bar = UnitAt(elapsed, barUnits, meter.Denominator);
        Int128 firstBeat = bar * meter.Numerator;
        Int128 beat = Int128.Clamp(UnitAt(elapsed, _wholeNoteTicks, meter.Denominator), firstBeat, firstBeat + meter.Numerator - 1);
        return new MeterPosition(long.CreateSaturating(_firstBars[index] + bar), (int)(beat - firstBeat) + 1,
            BoundaryTick(change, beat, _wholeNoteTicks), BoundaryTick(change, bar, barUnits));
    }

    public long TickAtBar(long bar)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bar);
        int low = 0, high = _changes.Length;
        while (low + 1 < high)
        {
            int middle = low + (high - low) / 2;
            if (_firstBars[middle] <= bar)
                low = middle;
            else
                high = middle;
        }
        var change = _changes[low];
        return BoundaryTick(change, bar - _firstBars[low], _wholeNoteTicks * change.Meter.Numerator);
    }

    /// <summary>First beat boundary strictly after tick, saturated at the end of the tick range.</summary>
    public long NextBeatTick(long tick) => NextBoundary(tick, bar: false);

    public long NextBarTick(long tick) => NextBoundary(tick, bar: true);

    private long NextBoundary(long tick, bool bar)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        int index = IndexAt(tick);
        var change = _changes[index];
        long units = bar ? _wholeNoteTicks * change.Meter.Numerator : _wholeNoteTicks;
        // For sub-tick beats every later integer tick already represents another beat.
        long next = units < change.Meter.Denominator ? long.CreateSaturating((Int128)tick + 1) :
            BoundaryTick(change, UnitAt(tick - change.Tick, units, change.Meter.Denominator) + 1, units);
        return index + 1 < _changes.Length ? Math.Min(next, _changes[index + 1].Tick) : next;
    }

    // Normal MIDI resolutions round each boundary to the nearest tick, always measured
    // from the segment origin. With multiple beats per tick, use the beat in effect at
    // that exact instant; its first representable tick is the ceiling of its ideal start.
    private Int128 UnitAt(long elapsed, long units, int denominator) =>
        _wholeNoteTicks < denominator ? (Int128)elapsed * denominator / units :
            (((Int128)2 * elapsed + 1) * denominator - 1) / (2 * units);

    private long BoundaryTick(MeterChange change, Int128 index, long units)
    {
        int denominator = change.Meter.Denominator;
        int rounding = _wholeNoteTicks < denominator ? denominator - 1 : denominator / 2;
        return long.CreateSaturating(change.Tick + (index * units + rounding) / denominator);
    }
}
