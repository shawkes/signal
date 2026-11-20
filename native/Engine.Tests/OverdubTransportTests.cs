using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class OverdubTransportTests
{
    private const int SampleRate = 1_000;
    private const long Origin = 10_000;
    private const float BackingLevel = 0.2f;
    private const float OverdubLevel = 0.3f;
    private static readonly LoopRange Loop = new(100, 200);

    [Fact]
    public void CountsInOnceUsingTheLoopStartTempoAndRepeatsTheBacking()
    {
        var engine = CreateEngine();
        var song = new Song(ChangingTempo(), [new SongTrack("Backing", [new(100, 20, 0, 60, 90)])],
            new SongMeter(3, 4));
        engine.RecordSong(song, new GateSynth(BackingLevel), countIn: true, offsetMilliseconds: 0,
            timestamp: Origin, loop: Loop);

        var countIn = Render(engine, Origin, 2_999);
        Assert.Equal(0.25f, countIn[0]);
        Assert.Equal(0.16f, countIn[1_000 * 2]);
        Assert.Equal(0.16f, countIn[2_000 * 2]);
        Assert.Equal(1, engine.CountInBeatsRemaining);
        Assert.Null(engine.PlaybackTick);
        Assert.Null(engine.RecordingLoopPass);

        // The backing starts at tick 100 after three one-second beats. The tempo
        // change at tick 150 makes each pass 750 samples long.
        var backing = Render(engine, Origin + 2_999, 1_551);
        Assert.Equal(0, backing[0]);
        Assert.Equal(BackingLevel, backing[1 * 2]);
        Assert.Equal(0, backing[201 * 2]);
        Assert.Equal(0, backing[750 * 2]);
        Assert.Equal(BackingLevel, backing[751 * 2]);
        Assert.Equal(BackingLevel, backing[1_501 * 2]);
        Assert.Null(engine.CountInBeatsRemaining);
        Assert.Equal(3L, engine.RecordingLoopPass);
        Assert.Equal(105L, engine.PlaybackTick);
    }

    [Fact]
    public void LoopMetronomeRepeatsItsSongBeatsAcrossTempoChangesAndSplitBuffers()
    {
        var whole = CreateEngine();
        var split = CreateEngine();
        whole.MetronomeEnabled = split.MetronomeEnabled = true;
        var song = new Song(ChangingTempo(), [], new SongMeter(3, 8));
        whole.RecordSong(song, SilentMonitorSynth.Instance, countIn: false, offsetMilliseconds: 0,
            timestamp: Origin, loop: Loop);
        split.RecordSong(song, SilentMonitorSynth.Instance, countIn: false, offsetMilliseconds: 0,
            timestamp: Origin, loop: Loop);

        var output = Render(whole, Origin, 1_501);
        var splitOutput = Render(split, Origin, 513)
            .Concat(Render(split, Origin + 513, 236))
            .Concat(Render(split, Origin + 749, 752));

        Assert.Equal(output, splitOutput);
        Assert.Equal(0.16f, output[0]);
        Assert.Equal(0.25f, output[500 * 2]);
        Assert.Equal(0, output[749 * 2]);
        Assert.Equal(0.16f, output[750 * 2]);
        Assert.Equal(0.25f, output[1_250 * 2]);
        Assert.Equal(0.16f, output[1_500 * 2]);
    }

    [Fact]
    public void PlaysCompletedNotesOnLaterPassesAndStopsTheEmptySongTake()
    {
        var engine = CreateEngine();
        var song = new Song(TempoMap.Constant(120, 100), []);
        long id = engine.RecordSong(song, SilentMonitorSynth.Instance, countIn: false, offsetMilliseconds: 0,
            timestamp: Origin, loop: Loop, overdubSynth: new GateSynth(OverdubLevel));
        Assert.True(engine.TryEnqueue(new MidiMessage(Origin + 50, 0x90, 64, 90)));
        Assert.True(engine.TryEnqueue(new MidiMessage(Origin + 125, 0x80, 64, 0)));

        var first = Render(engine, Origin, 500);
        Assert.All(first, sample => Assert.Equal(0, sample));
        Assert.Equal(100L, engine.PlaybackTick);
        Assert.Equal(2L, engine.RecordingLoopPass);
        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal(id, note.TakeId);
        Assert.Equal(110, note.StartTick);
        Assert.Equal(15, note.LengthTicks);

        var second = Render(engine, Origin + 500, 500);
        Assert.Equal(0, second[49 * 2]);
        Assert.Equal(OverdubLevel, second[50 * 2]);
        Assert.Equal(OverdubLevel, second[124 * 2]);
        Assert.Equal(0, second[125 * 2]);

        engine.StopSongRecording(timestamp: Origin + 1_100);
        var stopped = Render(engine, Origin + 1_000, 200);
        Assert.Equal(OverdubLevel, stopped[99 * 2]);
        Assert.Equal(0, stopped[100 * 2]);
        Assert.Equal(id, engine.CompletedTakeId);
        Assert.Null(engine.PlaybackTick);
        Assert.Null(engine.RecordingLoopPass);
        Assert.False(engine.TryReadNote(out _));
    }

    private static LatencyEngine CreateEngine() =>
        new(SampleRate, SilentMonitorSynth.Instance, timestampFrequency: SampleRate);

    private static TempoMap ChangingTempo() => new(100, [new(0, 500_000), new(100, 1_000_000), new(150, 500_000)]);

    private static float[] Render(LatencyEngine engine, long firstFrame, int frames)
    {
        var output = new float[frames * 2];
        engine.Render(output, firstFrame, new AudioClockAnchor(firstFrame, firstFrame));
        return output;
    }

    private sealed class GateSynth(float level) : IMonitorSynth
    {
        private bool _held;

        public void ProcessMidi(byte status, byte data1, byte data2) => _held = (status & 0xf0) == 0x90 && data2 > 0;
        public void Render(Span<float> interleavedStereo) => interleavedStereo.Fill(_held ? level : 0);
        public void Reset() => _held = false;
    }
}
