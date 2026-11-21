namespace Signal.Native.Engine;

/// <summary>A note-on observed during count-in or the first two bars of a song take.</summary>
public readonly record struct OpeningNoteTiming(byte Channel, byte Pitch, byte Velocity,
    double RawMilliseconds, double AdjustedMilliseconds, long? PlacedTick);

public sealed record OpeningRecordingTiming(
    double OffsetMilliseconds, int QuantizationDivision, int TicksPerQuarter, bool CountIn,
    long FirstTick, IReadOnlyList<OpeningNoteTiming> Notes, int Omitted);

internal sealed class OpeningTimingBuffer(RecordingPlacement placement, long durationFrames, long firstTick, bool countIn)
{
    private readonly OpeningNoteTiming[] _notes = new OpeningNoteTiming[128];
    private int _count;
    private int _omitted;
    public long TakeId { get; set; }

    public bool Includes(long elapsedFrame) => elapsedFrame < durationFrames;

    public void Add(MidiMessage message, long elapsedFrame, long? placedTick)
    {
        if (_count == _notes.Length)
        {
            _omitted++;
            return;
        }
        _notes[_count++] = new((byte)(message.Status & 0x0f), message.Data1, message.Data2,
            elapsedFrame * 1000.0 / placement.SampleRate,
            ((double)elapsedFrame - placement.OffsetFrames) * 1000.0 / placement.SampleRate, placedTick);
    }

    public OpeningRecordingTiming Snapshot() => new(
        placement.OffsetFrames * 1000.0 / placement.SampleRate, placement.Division, placement.Tempo.TicksPerQuarter,
        countIn, firstTick, _notes.AsSpan(0, _count).ToArray(), _omitted);
}
