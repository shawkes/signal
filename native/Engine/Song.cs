namespace Signal.Native.Engine;

public readonly record struct SongNote(long StartTick, long LengthTicks, byte Channel, byte NoteNumber, byte Velocity);

public sealed class SongTrack(string name, IEnumerable<SongNote> notes)
{
    public string Name { get; } = name;
    public IReadOnlyList<SongNote> Notes { get; } = Array.AsReadOnly(notes.ToArray());
}

public sealed class Song(TempoMap tempoMap, IEnumerable<SongTrack> tracks)
{
    public TempoMap TempoMap { get; } = tempoMap;
    public IReadOnlyList<SongTrack> Tracks { get; } = Array.AsReadOnly(tracks.ToArray());
}
