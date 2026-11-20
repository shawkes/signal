using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class CountInTransportTests
{
    private const int SampleRate = 1_000;
    private const long Origin = 10_000;
    private const float BackingLevel = 0.2f;

    [Fact]
    public void ThreeBeatCountInStartsBackingAtBarOneEvenWithMetronomeOff()
    {
        var engine = CreateEngine();
        var song = new Song(new TempoMap(480, [new(0, 500_000), new(480, 1_000_000)]),
            [new SongTrack("Backing", [new(0, 960, 0, 60, 90)])], new SongMeter(3, 4));
        engine.RecordSong(song, new GateSynth(), countIn: true, offsetMilliseconds: 0, timestamp: Origin + 13);
        Assert.Equal(3, engine.CountInBeatsRemaining);
        Assert.Null(engine.PlaybackTick);

        var first = Render(engine, Origin, 813);
        Assert.Equal(0, first[0]);
        Assert.Equal(0.25f, first[13 * 2]);
        Assert.Equal(0.16f, first[513 * 2]);
        Assert.Equal(2, engine.CountInBeatsRemaining);
        Assert.Null(engine.PlaybackTick);

        var second = Render(engine, Origin + 813, 694);
        Assert.Equal(0.16f, second[(1_013 - 813) * 2]);
        Assert.Equal(0, second[^1]);
        Assert.Equal(1, engine.CountInBeatsRemaining);
        Assert.Null(engine.PlaybackTick);

        var third = Render(engine, Origin + 1_507, 9);
        Assert.Equal(0, third[5 * 2]);
        Assert.Equal(BackingLevel, third[6 * 2]);
        Assert.Null(engine.CountInBeatsRemaining);
        Assert.Equal(2, engine.PlaybackTick);
    }

    [Fact]
    public void EighthNoteMeasureAndSongMetronomeStayAlignedAcrossSplitBuffers()
    {
        var whole = CreateEngine();
        var split = CreateEngine();
        whole.MetronomeEnabled = split.MetronomeEnabled = true;
        var song = new Song(TempoMap.Constant(120, 101),
            [new SongTrack("Backing", [new(0, 303, 0, 60, 90)])], new SongMeter(6, 8));
        whole.RecordSong(song, new GateSynth(), countIn: true, offsetMilliseconds: 0, timestamp: Origin);
        split.RecordSong(song, new GateSynth(), countIn: true, offsetMilliseconds: 0, timestamp: Origin);

        var expected = Render(whole, Origin, 1_801);
        var actual = Render(split, Origin, 493)
            .Concat(Render(split, Origin + 493, 1_011))
            .Concat(Render(split, Origin + 1_504, 297));
        Assert.Equal(expected, actual);

        Assert.Equal(0.25f, expected[0]);
        for (int beat = 1; beat < 6; beat++)
            Assert.Equal(0.16f, expected[beat * 250 * 2]);
        Assert.Equal(0, expected[1_499 * 2]);
        Assert.Equal(BackingLevel + 0.25f, expected[1_500 * 2]);
        Assert.Equal(BackingLevel + 0.16f, expected[1_750 * 2]);
    }

    [Fact]
    public void StopsAnInProgressCountInAndAcknowledgesTheEmptyTake()
    {
        var engine = CreateEngine();
        var song = new Song(TempoMap.Constant(120), [new SongTrack("Backing", [new(0, 480, 0, 60, 90)])]);
        long id = engine.RecordSong(song, new GateSynth(), countIn: true, offsetMilliseconds: 0, timestamp: Origin);
        engine.TryEnqueue(new MidiMessage(Origin + 500, 0x90, 64, 90));
        engine.StopSongRecording(timestamp: Origin + 650);

        var output = Render(engine, Origin, 2_100);
        Assert.Equal(0.25f, output[0]);
        Assert.Equal(0.16f, output[500 * 2]);
        Assert.Equal(0, output[1_000 * 2]);
        Assert.Equal(0, output[2_000 * 2]);
        Assert.Equal(id, engine.CompletedTakeId);
        Assert.Null(engine.CountInBeatsRemaining);
        Assert.Null(engine.PlaybackTick);
        Assert.False(engine.TryReadNote(out _));
    }

    [Fact]
    public void CanCancelBeforeTheFirstWritableFrameAndImmediatelyArmAnotherTake()
    {
        var engine = CreateEngine();
        var song = new Song(TempoMap.Constant(120), []);
        long id = engine.RecordSong(song, SilentMonitorSynth.Instance, countIn: true, offsetMilliseconds: 0,
            timestamp: Origin - 20);
        engine.StopSongRecording(timestamp: Origin - 5);
        Assert.All(Render(engine, Origin, 128), sample => Assert.Equal(0, sample));
        Assert.Equal(id, engine.CompletedTakeId);
        Assert.Null(engine.CountInBeatsRemaining);

        long nextTake = engine.RecordSong(song, SilentMonitorSynth.Instance, countIn: false,
            offsetMilliseconds: 0, timestamp: Origin + 128);
        Assert.True(nextTake > id);
        Render(engine, Origin + 128, 128);
        Assert.NotNull(engine.PlaybackTick);
        Assert.Null(engine.CountInBeatsRemaining);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeepsRecordingPastTheBackingAndClosesHeldNotesOnStop(bool hasBacking)
    {
        var engine = CreateEngine();
        var song = new Song(TempoMap.Constant(120),
            [new SongTrack("Backing", hasBacking ? [new(0, 48, 0, 60, 90)] : [])]);
        long id = engine.RecordSong(song, new GateSynth(), countIn: false, offsetMilliseconds: 0, timestamp: Origin);
        engine.TryEnqueue(new MidiMessage(Origin + 1_075, 0x90, 64, 90));
        var first = Render(engine, Origin, 1_120);
        Assert.Equal(hasBacking ? BackingLevel : 0, first[0]);
        Assert.NotNull(engine.PlaybackTick);
        Assert.False(engine.TryReadNote(out _));

        engine.StopSongRecording(timestamp: Origin + 1_125);
        Render(engine, Origin + 1_120, 100);
        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal(id, note.TakeId);
        Assert.Equal(1_032, note.StartTick);
        Assert.Equal(50, note.PlayedLengthFrames);
        Assert.Equal(id, engine.CompletedTakeId);
        Assert.Null(engine.PlaybackTick);
    }

    [Fact]
    public void CountInAndSongRecordingRenderWithoutManagedAllocationsAfterWarmup()
    {
        var engine = CreateEngine();
        var song = new Song(TempoMap.Constant(120), [new SongTrack("Backing", [new(0, 480, 0, 60, 90)])]);
        var output = new float[64];
        long frame = Origin;
        engine.RecordSong(song, new GateSynth(), countIn: true, offsetMilliseconds: 0, timestamp: frame);
        RenderBlocks(130);
        engine.StopSongRecording(timestamp: frame);
        RenderBlocks(1);

        engine.RecordSong(song, new GateSynth(), countIn: true, offsetMilliseconds: 0, timestamp: frame);
        long before = GC.GetAllocatedBytesForCurrentThread();
        RenderBlocks(130);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void RenderBlocks(int count)
        {
            for (int i = 0; i < count; i++, frame += output.Length / 2)
                engine.Render(output, frame, new AudioClockAnchor(frame, frame));
        }
    }

    private static LatencyEngine CreateEngine() =>
        new(SampleRate, SilentMonitorSynth.Instance, timestampFrequency: SampleRate);

    private static float[] Render(LatencyEngine engine, long firstFrame, int frames)
    {
        var output = new float[frames * 2];
        engine.Render(output, firstFrame, new AudioClockAnchor(firstFrame, firstFrame));
        return output;
    }

    private sealed class GateSynth : IMonitorSynth
    {
        private bool _held;

        public void ProcessMidi(byte status, byte data1, byte data2) => _held = (status & 0xf0) == 0x90 && data2 > 0;
        public void Render(Span<float> interleavedStereo) => interleavedStereo.Fill(_held ? BackingLevel : 0);
    }
}
