using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class LoopCaptureTests
{
    private const int SampleRate = 1_000;
    private const long StartFrame = 123;
    private const long ClockOrigin = 5_000_000;

    [Fact]
    public void CompensatesAndQuantizesTheWrappedPositionAtAChangingTempo()
    {
        var map = new TempoMap(480, [new(0, 500_000), new(480, 1_000_000)]);
        var engine = StartLoop(map, new LoopRange(360, 960), offset: 10, division: 16);
        // The take starts at source frame 375 and wraps at 1,500. These receipts are in pass 2.
        long attack = StartFrame + 1_125 + 110;
        SendNote(engine, attack, attack + 215);

        RenderThrough(engine, attack + 215);

        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal(480, note.StartTick);
        Assert.Equal(110, note.LengthTicks);
        Assert.Equal(attack, note.ReceivedFrame);
        Assert.Equal(attack - 10, note.CompensatedFrame);
        Assert.Equal(215, note.PlayedLengthFrames);
        Assert.False(engine.TryReadNote(out _));
    }

    [Fact]
    public void SplitsAKeyHeldAcrossTheLoopAtTheActualBoundary()
    {
        var engine = StartLoop(TempoMap.Constant(120), new LoopRange(480, 960));
        SendNote(engine, StartFrame + 400, StartFrame + 600);

        RenderThrough(engine, StartFrame + 600);

        Assert.True(engine.TryReadNote(out var tail));
        Assert.Equal(new SongNote(864, 96, 0, 60, 92), tail.ToSongNote());
        Assert.Equal(100, tail.PlayedLengthFrames);
        Assert.True(engine.TryReadNote(out var continuation));
        Assert.Equal(new SongNote(480, 96, 0, 60, 92), continuation.ToSongNote());
        Assert.Equal(StartFrame + 500, continuation.ReceivedFrame);
        Assert.Equal(100, continuation.PlayedLengthFrames);
        Assert.False(engine.TryReadNote(out _));
    }

    [Theory]
    [InlineData(460, 50, 874, 86, 10)]
    [InlineData(300, -150, 912, 48, 48)]
    public void SplitsThePlacedNoteWithoutLosingTheOriginalLength(int phase, int offset,
        long expectedStart, long expectedTailLength, long expectedContinuationLength)
    {
        var engine = StartLoop(TempoMap.Constant(120), new LoopRange(480, 960), offset);
        SendNote(engine, StartFrame + phase, StartFrame + phase + 100);

        RenderThrough(engine, StartFrame + phase + 100);

        Assert.True(engine.TryReadNote(out var tail));
        Assert.Equal(new SongNote(expectedStart, expectedTailLength, 0, 60, 92), tail.ToSongNote());
        Assert.True(engine.TryReadNote(out var continuation));
        Assert.Equal(new SongNote(480, expectedContinuationLength, 0, 60, 92), continuation.ToSongNote());
        Assert.Equal(100, tail.PlayedLengthFrames + continuation.PlayedLengthFrames);
        Assert.False(engine.TryReadNote(out _));
    }

    [Fact]
    public void AStartSnappedToTheExclusiveEndWrapsToTheNextDownbeat()
    {
        var engine = StartLoop(TempoMap.Constant(120), new LoopRange(480, 960), division: 16);
        SendNote(engine, StartFrame + 475, StartFrame + 495);

        RenderThrough(engine, StartFrame + 495);

        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal(new SongNote(480, 19, 0, 60, 92), note.ToSongNote());
        Assert.Equal(20, note.PlayedLengthFrames);
        Assert.False(engine.TryReadNote(out _));
    }

    [Fact]
    public void FinishesHeldNotesOnStopWithoutRepeatingIdenticalFullCycles()
    {
        var engine = StartLoop(TempoMap.Constant(120), new LoopRange(480, 960));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(StartFrame + 400), 0x90, 60, 92)));
        var stop = StartFrame + 30 * 500 + 50;
        engine.StopSongRecording(timestamp: Timestamp(stop));

        RenderThrough(engine, stop);

        var captured = new List<SongNote>();
        while (engine.TryReadNote(out var note))
            captured.Add(note.ToSongNote());

        Assert.Equal([
            new SongNote(864, 96, 0, 60, 92),
            new SongNote(480, 480, 0, 60, 92),
            new SongNote(480, 48, 0, 60, 92)
        ], captured);
    }

    private static LatencyEngine StartLoop(TempoMap tempo, LoopRange loop, double offset = 0, int division = 0)
    {
        var song = new Song(tempo, [new SongTrack("Piano", [])]);
        var engine = new LatencyEngine(SampleRate, SilentMonitorSynth.Instance, timestampFrequency: SampleRate);
        engine.RecordSong(song, SilentMonitorSynth.Instance, countIn: false, offsetMilliseconds: offset,
            quantizationDivision: division, timestamp: Timestamp(StartFrame), loop: loop);
        return engine;
    }

    private static void SendNote(LatencyEngine engine, long attack, long release)
    {
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(attack), 0x90, 60, 92)));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(release), 0x80, 60, 0)));
    }

    private static void RenderThrough(LatencyEngine engine, long frame)
    {
        var samples = new float[checked((int)(2 * (frame - StartFrame + 1)))];
        engine.Render(samples, StartFrame, new AudioClockAnchor(Timestamp(StartFrame), StartFrame));
    }

    private static long Timestamp(long frame) => ClockOrigin + frame;
}
