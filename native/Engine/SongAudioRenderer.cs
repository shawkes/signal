namespace Signal.Native.Engine;

/// <summary>Renders the same scheduled MIDI events as song playback without using an audio device.</summary>
public sealed class SongAudioRenderer
{
    private readonly NotePlayback _playback;
    private readonly TempoMap _tempo;
    private long _frame;

    public SongAudioRenderer(Song song, IMonitorSynth synth, int sampleRate)
    {
        _tempo = song.TempoMap;
        _playback = new NotePlayback(sampleRate, song.MeterMap);
        long endTick = Song.IncludeTimingMarkers(song.Tracks.Select(track => track.EndTick).DefaultIfEmpty().Max(),
            _tempo, song.MeterMap);
        var plan = _playback.Prepare(song.Tracks.SelectMany(track => track.Notes),
            song.Tracks.SelectMany(track => track.PlaybackMessages), _tempo, synth, endTick,
            meterMap: song.MeterMap, allowEmpty: true)!;
        FrameCount = plan.Timeline.EndWithRelease;
        _playback.Request(plan);
    }

    public long FrameCount { get; }

    public int ReadFrames(Span<float> stereo)
    {
        int frames = (int)Math.Min(stereo.Length / 2, FrameCount - _frame);
        if (frames == 0)
            return 0;
        var output = stereo[..(frames * 2)];
        output.Clear();
        _playback.Render(output, _frame, _tempo, null);
        _frame += frames;
        return frames;
    }
}
