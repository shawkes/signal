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

    [Fact]
    public void NegativeOffsetCarriesLateAttacksIntoTheNextPassAtTheWrappedTempo()
    {
        var engine = CreateEngine();
        var song = new Song(ChangingTempo(), []);
        long take = engine.RecordSong(song, SilentMonitorSynth.Instance, false, -112,
            timestamp: Origin, loop: Loop, overdubSynth: new GateSynth(OverdubLevel));
        engine.TryEnqueue(new MidiMessage(Origin + 718, 0x90, 64, 90));
        var first = Render(engine, Origin, 724);
        Assert.All(first, sample => Assert.Equal(0, sample));
        var preview = new HeldRecordingNote[1];
        Assert.Equal(1, engine.CopyHeldRecordingNotes(take, preview));
        Assert.Equal(new HeldRecordingNote(0, 64, 0, 108, 1), preview[0]);
        engine.TryEnqueue(new MidiMessage(Origin + 728, 0x80, 64, 0));
        Render(engine, Origin + 724, 26);
        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal((108L, 2L, 10L), (note.StartTick, note.LengthTicks, note.PlayedLengthFrames));

        var second = Render(engine, Origin + 750, 125);
        Assert.Equal(0, second[0]);
        Assert.Equal(0, second[79 * 2]);
        Assert.Equal(OverdubLevel, second[80 * 2]);
        Assert.Equal(OverdubLevel, second[99 * 2]);
        Assert.Equal(0, second[100 * 2]);
    }

    [Fact]
    public void OffsetLongerThanALoopDoesNotReplayTheNoteBeforeItsPlacedPass()
    {
        var engine = CreateEngine();
        var song = new Song(TempoMap.Constant(120, 500), []);
        engine.RecordSong(song, SilentMonitorSynth.Instance, false, -112, timestamp: Origin,
            loop: new LoopRange(500, 550), overdubSynth: new GateSynth(OverdubLevel));
        engine.TryEnqueue(new MidiMessage(Origin + 20, 0x90, 64, 90));
        engine.TryEnqueue(new MidiMessage(Origin + 45, 0x80, 64, 0));
        var audio = Render(engine, Origin, 200);
        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal((532L, 18L), (note.StartTick, note.LengthTicks));
        Assert.True(engine.TryReadNote(out var continuation));
        Assert.Equal((500L, 7L), (continuation.StartTick, continuation.LengthTicks));
        Assert.All(audio[..(132 * 2)], sample => Assert.Equal(0, sample));
        Assert.Equal(OverdubLevel, audio[132 * 2]);
        Assert.Equal(OverdubLevel, audio[150 * 2]);
        Assert.Equal(0, audio[157 * 2]);
        Assert.Equal(OverdubLevel, audio[182 * 2]);
    }

    [Fact]
    public void RepeatedBoundaryHitsKeepTheSameCompensatedPlacementAndDoNotReplayLateAfterAGap()
    {
        const int loopFrames = 750;
        const int passes = 64;
        var engine = CreateEngine();
        var song = new Song(ChangingTempo(), []);
        engine.RecordSong(song, SilentMonitorSynth.Instance, false, -10, timestamp: Origin,
            loop: Loop, overdubSynth: new GateSynth(OverdubLevel));

        for (int pass = 0; pass < passes; pass++)
        {
            long frame = Origin + pass * loopFrames;
            Assert.True(engine.TryEnqueue(new MidiMessage(frame + 2, 0x99, 42, 90)));
            Assert.True(engine.TryEnqueue(new MidiMessage(frame + 80, 0x89, 42, 0)));
            var audio = Render(engine, frame, loopFrames);
            Assert.True(engine.TryReadNote(out var note));
            Assert.Equal((101L, 8L, 78L), (note.StartTick, note.LengthTicks, note.PlayedLengthFrames));
            if (pass > 0)
            {
                Assert.Equal(0, audio[9 * 2]);
                Assert.Equal(OverdubLevel, audio[10 * 2]);
            }
        }

        // Missing the first 25 frames loses the attack. The remaining duration
        // of that drum note must never trigger a replacement attack at frame 25.
        var afterGap = Render(engine, Origin + passes * loopFrames + 25, 40);
        Assert.All(afterGap, sample => Assert.Equal(0, sample));
        Assert.Equal(0, engine.DroppedMidiMessages);
        Assert.Equal(0, engine.DroppedRecordedNotes);
    }

    [Fact]
    public void LoopCaptureAndReplayRenderWithoutManagedAllocationsAfterWarmup()
    {
        var engine = CreateEngine();
        var song = new Song(TempoMap.Constant(120, 100), []);
        var output = new float[64];
        long frame = Origin;
        ArmTake();
        RenderBlocks();

        ArmTake();
        long before = GC.GetAllocatedBytesForCurrentThread();
        RenderBlocks();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

        void ArmTake()
        {
            engine.RecordSong(song, SilentMonitorSynth.Instance, countIn: false, offsetMilliseconds: 0,
                timestamp: frame, loop: Loop, overdubSynth: new GateSynth(OverdubLevel));
            engine.TryEnqueue(new MidiMessage(frame + 20, 0x90, 64, 90));
            engine.TryEnqueue(new MidiMessage(frame + 120, 0x80, 64, 0));
            engine.TryEnqueue(new MidiMessage(frame + 450, 0x90, 65, 90));
            engine.TryEnqueue(new MidiMessage(frame + 550, 0x80, 65, 0));
            engine.TryEnqueue(new MidiMessage(frame + 990, 0x90, 66, 90));
            engine.StopSongRecording(timestamp: frame + 1_050);
        }

        void RenderBlocks()
        {
            for (int block = 0; block < 40; block++, frame += output.Length / 2)
                engine.Render(output, frame, new AudioClockAnchor(frame, frame));
        }
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
