namespace Signal.Native.Engine;

/// <summary>A still-held key on the song timeline. A note wrapping the loop has a second segment.</summary>
public readonly record struct HeldRecordingNote(byte Channel, byte NoteNumber, int Segment, long StartTick, long LengthTicks)
{
    public int DestinationTrack { get; init; } = -1;
}

internal readonly record struct RecordingPlacement(TempoMap Tempo, int SampleRate, long OffsetFrames, int Division)
{
    private const int UnquantizedPickupMilliseconds = 25;
    private readonly double _gridTicks = Division == 0 ? 0 : Math.Max(1, 4.0 * Tempo.TicksPerQuarter / Division);

    public long EarliestDownbeatReceipt(long downbeatFrame, long startTick)
    {
        // A hit up to half a grid cell early still snaps to the downbeat.
        long earlyFrames = Division == 0
            ? (long)SampleRate * UnquantizedPickupMilliseconds / 1000
            : (long)Math.Floor(Math.BitIncrement(_gridTicks * 0.5 * Tempo.SecondsPerTickAt(startTick) * SampleRate));
        // Always accept attacks on or after the downbeat, whatever the compensation setting.
        return long.CreateSaturating(Int128.Min(downbeatFrame, (Int128)downbeatFrame + OffsetFrames - earlyFrames));
    }

    public long TickAt(long frame) =>
        (long)Math.Round(Tempo.SampleToTick(frame, SampleRate), MidpointRounding.AwayFromZero);

    public long StartTick(long sourceFrame) => SnapTick(Tempo.SampleToTick(sourceFrame - OffsetFrames, SampleRate));

    public long SnapTick(double tick)
    {
        if (_gridTicks > 0)
            tick = Math.Floor(tick / _gridTicks + 0.5) * _gridTicks;
        long start = (long)Math.Round(tick, MidpointRounding.AwayFromZero);
        return Math.Max(0, start);
    }
}

internal readonly record struct RecordingLoop(LoopRange Range, long StartFrame, long EndFrame)
{
    public long FrameCount => EndFrame - StartFrame;
    public long TickCount => Range.EndTick - Range.StartTick;

    public (long Tick, long CompensatedSource, long Pass) Place(RecordingPlacement placement, long elapsed)
    {
        Int128 compensated = (Int128)elapsed - placement.OffsetFrames;
        if (compensated < 0)
            return (Range.StartTick, long.CreateSaturating(StartFrame + compensated), 0);

        long pass = long.CreateSaturating(compensated / FrameCount);
        long phase = (long)(compensated % FrameCount);
        long source = StartFrame + phase;
        double tick = phase == 0 ? Range.StartTick : placement.Tempo.SampleToTick(source, placement.SampleRate);
        long start = placement.SnapTick(tick);
        return start >= Range.EndTick
            ? (Range.StartTick, source, long.CreateSaturating((Int128)pass + 1))
            : (Math.Max(Range.StartTick, start), source, pass);
    }

    public (long SourceAttack, long RawAttackTick, Int128 Duration) Measure(
        RecordingPlacement placement, long elapsedStart, long elapsedEnd)
    {
        // The count-in runs linearly at the loop's opening tempo. Its last few milliseconds
        // can supply the first recorded attack when negative compensation moves it into the loop.
        long start = StartFrame + (elapsedStart < 0 ? elapsedStart : elapsedStart % FrameCount);
        long startTick = TickAtElapsed(placement, elapsedStart);
        long passes = Math.Max(0, elapsedEnd) / FrameCount - Math.Max(0, elapsedStart) / FrameCount;
        Int128 duration = (Int128)passes * TickCount + TickAtElapsed(placement, elapsedEnd) - startTick;
        return (start, startTick, Int128.Max(1, duration));
    }

    public double CountInFramesPerTick(RecordingPlacement placement) =>
        placement.Tempo.SecondsPerTickAt(Range.StartTick) * placement.SampleRate;

    private long TickAtElapsed(RecordingPlacement placement, long elapsed)
    {
        if (elapsed < 0)
            return Range.StartTick + (long)Math.Round(elapsed / CountInFramesPerTick(placement), MidpointRounding.AwayFromZero);
        long phase = elapsed % FrameCount;
        return phase == 0 ? Range.StartTick : Math.Clamp(placement.TickAt(StartFrame + phase), Range.StartTick, Range.EndTick);
    }
}

/// <summary>Preview data written by the audio thread. Geometry is calculated on the reader's thread.</summary>
internal sealed class HeldRecordingNotes(RecordingPlacement placement, RecordingLoop? loop)
{
    internal const int KeyCount = 16 * 128;
    private readonly long[] _attacks = EmptyKeys();
    private readonly int[] _destinationTracks = new int[KeyCount];
    public RecordingPlacement Placement { get; } = placement;
    public RecordingLoop? Loop { get; } = loop;

    private static long[] EmptyKeys()
    {
        var keys = new long[KeyCount];
        Array.Fill(keys, long.MinValue);
        return keys;
    }

    public void Press(int key, long elapsedFrame, int destinationTrack)
    {
        Volatile.Write(ref _destinationTracks[key], destinationTrack);
        Volatile.Write(ref _attacks[key], elapsedFrame);
    }
    public void Release(int key) => Volatile.Write(ref _attacks[key], long.MinValue);

    public int CopyTo(Span<HeldRecordingNote> destination, long elapsedFrame)
    {
        int count = 0;
        for (int key = 0; key < KeyCount && count < destination.Length; key++)
        {
            long attack = Volatile.Read(ref _attacks[key]);
            if (attack == long.MinValue)
                continue;
            int track = Volatile.Read(ref _destinationTracks[key]);
            if (attack != Volatile.Read(ref _attacks[key]))
                continue;
            byte channel = (byte)(key / 128);
            byte pitch = (byte)(key % 128);
            long end = Math.Max(attack, elapsedFrame);
            if (Loop is { } cycle)
            {
                var (_, _, duration) = cycle.Measure(Placement, attack, end);
                if (duration >= cycle.TickCount)
                {
                    destination[count++] = new(channel, pitch, 0, cycle.Range.StartTick, cycle.TickCount) { DestinationTrack = track };
                    continue;
                }

                long start = cycle.Place(Placement, attack).Tick;
                long length = (long)duration;
                long first = Math.Min(length, cycle.Range.EndTick - start);
                destination[count++] = new(channel, pitch, 0, start, first) { DestinationTrack = track };
                if (first < length && count < destination.Length)
                    destination[count++] = new(channel, pitch, 1, cycle.Range.StartTick, length - first) { DestinationTrack = track };
            }
            else
            {
                long length = Placement.TickAt(end) - Placement.TickAt(attack);
                destination[count++] = new(channel, pitch, 0, Placement.StartTick(attack), length) { DestinationTrack = track };
            }
        }
        return count;
    }
}
