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

public sealed class Song(TempoMap tempoMap, IEnumerable<SongTrack> tracks, SongMeter? openingMeter = null)
{
    public TempoMap TempoMap { get; } = tempoMap;
    public IReadOnlyList<SongTrack> Tracks { get; } = Array.AsReadOnly(tracks.ToArray());
    public SongMeter OpeningMeter { get; } = openingMeter ?? SongMeter.CommonTime;
}
