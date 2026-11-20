namespace Signal.Native.Engine;

public readonly record struct SongNote(long StartTick, long LengthTicks, byte Channel, byte NoteNumber, byte Velocity);

/// <summary>A non-note MIDI channel voice message on the song's tick timeline.</summary>
public readonly record struct SongChannelMessage(long Tick, byte Status, byte Data1, byte Data2);

public sealed class SongTrack(
    string name, IEnumerable<SongNote> notes, IEnumerable<SongChannelMessage>? playbackMessages = null, long endTick = 0)
{
    public string Name { get; } = name;
    public IReadOnlyList<SongNote> Notes { get; } = Array.AsReadOnly(notes.ToArray());
    public IReadOnlyList<SongChannelMessage> PlaybackMessages { get; } =
        Array.AsReadOnly(playbackMessages?.ToArray() ?? []);
    public long EndTick { get; } = endTick >= 0 ? endTick : throw new ArgumentOutOfRangeException(nameof(endTick));
}

public sealed class Song
{
    public Song(TempoMap tempoMap, IEnumerable<SongTrack> tracks, SongMeter? openingMeter = null, MeterMap? meterMap = null)
    {
        if (meterMap is not null && meterMap.TicksPerQuarter != tempoMap.TicksPerQuarter)
            throw new ArgumentException("The meter and tempo maps must use the same tick resolution.", nameof(meterMap));
        TempoMap = tempoMap;
        MeterMap = meterMap ?? new MeterMap(tempoMap.TicksPerQuarter, [new(0, openingMeter ?? SongMeter.CommonTime)]);
        Tracks = Array.AsReadOnly(tracks.ToArray());
    }

    public TempoMap TempoMap { get; }
    public IReadOnlyList<SongTrack> Tracks { get; }
    public MeterMap MeterMap { get; }
    public SongMeter OpeningMeter => MeterMap.Changes[0].Meter;

    /// <summary>Extends a half-open song range to include any later tempo or meter marker.</summary>
    public static long IncludeTimingMarkers(long endTick, TempoMap tempoMap, MeterMap meterMap)
    {
        long lastMarker = Math.Max(tempoMap.Changes[^1].Tick, meterMap.Changes[^1].Tick);
        if (lastMarker == 0 || lastMarker < endTick)
            return endTick;
        return lastMarker == long.MaxValue ? long.MaxValue : lastMarker + 1;
    }
}
