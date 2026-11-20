using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class CountInTransportTests
{
    private const int SampleRate = 1_000;
    private const long Origin = 10_000;
    private const float BackingLevel = 0.2f;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SongPlaybackAndRecordingAccentTheNewBarAtAMidBeatMeterChange(bool recording)
    {
        var engine = CreateEngine();
        engine.MetronomeEnabled = true;
        var song = CreateMeterChangeSong();
        if (recording)
            engine.RecordSong(song, SilentMonitorSynth.Instance, false, 0, timestamp: Origin);
        else
            Assert.True(engine.PlaySong(song, SilentMonitorSynth.Instance));

        var samples = Render(engine, Origin, 1_800);
        Assert.Equal(0.25f, samples[0]);
        Assert.Equal(0.25f, samples[250 * 2]); // The 5/8 marker falls halfway through the first quarter.
        foreach (int frame in new[] { 500, 750, 1_000, 1_375 })
            Assert.Equal(0.16f, samples[frame * 2]);
        Assert.Equal(0f, samples[1_500 * 2]);
        Assert.Equal(0.25f, samples[1_750 * 2]);
    }

    [Fact]
    public void LoopCountInUsesTheSignatureAndTempoAtItsStartAndKeepsSongBeatPhase()
    {
        var engine = CreateEngine();
        engine.MetronomeEnabled = true;
        engine.RecordSong(CreateMeterChangeSong(), SilentMonitorSynth.Instance, true, 0, timestamp: Origin,
            loop: new LoopRange(300, 480));
        Assert.Equal(5, engine.CountInBeatsRemaining);

        var count = Render(engine, Origin, 1_875);
        Assert.Equal(0.25f, count[0]);
        foreach (int frame in new[] { 375, 750, 1_125, 1_500 })
            Assert.Equal(0.16f, count[2 * frame]);
        var playback = Render(engine, Origin + 1_875, 2_250);
        Assert.Null(engine.CountInBeatsRemaining);
        for (int pass = 0; pass < 2; pass++)
        {
            int first = pass * 1_125;
            Assert.Equal(0.16f, playback[2 * first]); // The loop starts on beat five of the 5/8 bar.
            Assert.Equal(0.25f, playback[2 * (first + 375)]);
            Assert.Equal(0.16f, playback[2 * (first + 750)]);
        }
    }

    [Fact]
    public void LoopRepeatsMeterAndTempoChangesAcrossAudioBuffers()
    {
        var engine = CreateEngine();
        engine.MetronomeEnabled = true;
        Assert.True(engine.PlaySong(CreateMeterChangeSong(), SilentMonitorSynth.Instance, new LoopRange(0, 480)));

        var firstPass = Render(engine, Origin, 2_500);
        var secondPass = Render(engine, Origin + 2_500, 253)
            .Concat(Render(engine, Origin + 2_753, 1_499))
            .Concat(Render(engine, Origin + 4_252, 748));
        Assert.Equal(firstPass, secondPass);
        Assert.Equal(0.25f, firstPass[250 * 2]);
        Assert.Equal(0.16f, firstPass[1_375 * 2]);
        Assert.Equal(0.25f, firstPass[1_750 * 2]);
    }

    private static Song CreateMeterChangeSong()
    {
        var tempo = new TempoMap(120, [new(0, 500_000), new(240, 750_000)]);
        var meter = new MeterMap(120, [new(0, new(3, 4)), new(60, new(5, 8))]);
        return new Song(tempo, [new SongTrack("Backing", [new(0, 960, 0, 60, 90)])], meterMap: meter);
    }

    [Fact]
    public void ASignatureInsideThePreviousClickDecayRestartsTheAccentAtItsRoundedSample()
    {
        // Two ticks per millisecond. Tick 1 lands on frame 1 and ticks 3 and 4 share frame 2.
        var tempo = TempoMap.Constant(120, 1_000);
        var meter = new MeterMap(1_000, [
            new(0, new(3, 4)), new(1, new(4, 4)), new(3, new(7, 8)), new(4, new(2, 8))]);
        var song = new Song(tempo, [new SongTrack("Backing", [new(0, 2_000, 0, 60, 90)])], meterMap: meter);
        var engine = CreateEngine();
        engine.MetronomeEnabled = true;
        Assert.True(engine.PlaySong(song, SilentMonitorSynth.Instance));
        var audio = Render(engine, Origin, 503);
        Assert.Equal(0.25f, audio[0]);
        Assert.Equal(0.25f, audio[2]);
        Assert.Equal(0.25f, audio[4]);
        Assert.Equal(0.16f, audio[252 * 2]);
        Assert.Equal(0.25f, audio[502 * 2]);
    }

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
