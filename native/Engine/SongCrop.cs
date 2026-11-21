namespace Signal.Native.Engine;

public static class SongCrop
{
    /// <summary>Keeps the half-open tick range and moves its start to tick zero.</summary>
    public static Song Keep(Song song, long start, long end)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        if (end <= start)
            throw new ArgumentOutOfRangeException(nameof(end));

        int resolution = song.TempoMap.TicksPerQuarter;
        var tempos = song.TempoMap.Changes;
        var tempo = new TempoMap(resolution, tempos.Where(change => change.Tick > start && change.Tick < end)
            .Select(change => change with { Tick = change.Tick - start })
            .Prepend(tempos.Last(change => change.Tick <= start) with { Tick = 0 }));
        var meter = new MeterMap(resolution, song.MeterMap.Changes.Where(change => change.Tick > start && change.Tick < end)
            .Select(change => change with { Tick = change.Tick - start })
            .Prepend(new MeterChange(0, song.MeterMap.MeterAt(start))));

        var tracks = song.Tracks.Select(track => new SongTrack(track.Name,
            track.Notes.Where(note => note.StartTick < end &&
                (note.StartTick >= start || note.StartTick + note.LengthTicks > start))
            .Select(note =>
            {
                long onset = Math.Max(note.StartTick, start);
                return note with
                {
                    StartTick = onset - start,
                    LengthTicks = Math.Min(end, note.StartTick + note.LengthTicks) - onset
                };
            }),
            track.PlaybackMessages.Where(message => message.Tick < end &&
                (message.Tick >= start || (message.Status & 0xf0) != 0xa0))
            .OrderBy(message => message.Tick)
            .Select(message => message with { Tick = Math.Max(0, message.Tick - start) }),
            end - start));
        return new Song(tempo, tracks, meterMap: meter);
    }
}
