using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class SongRecordingTests
{
    private const int SampleRate = 48_000;
    private const long ClockOrigin = 360_000_000;
    private const long FirstFrame = 12_345;

    [Fact]
    public void RejectsCountInNotesButKeepsCompensatedFirstBeatNotesAtTickZero()
    {
        var song = new Song(TempoMap.Constant(120), [new SongTrack("Piano", [])]);
        var engine = NewEngine();
        long take = engine.RecordSong(song, SilentMonitorSynth.Instance, countIn: true,
            offsetMilliseconds: 10, timestamp: Timestamp(FirstFrame));
        long songStart = FirstFrame + 2 * SampleRate;
        EnqueueNote(engine, 55, songStart - 1, songStart + 480);
        EnqueueNote(engine, 60, songStart + 240, songStart + 5_040);

        RenderThrough(engine, songStart + 5_040);

        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal(take, note.TakeId);
        Assert.Equal((byte)60, note.NoteNumber);
        Assert.Equal(songStart + 240, note.ReceivedFrame);
        Assert.Equal(songStart - 240, note.CompensatedFrame);
        Assert.Equal(0, note.StartTick);
        Assert.Equal(96, note.LengthTicks);
        Assert.Equal(4_800, note.PlayedLengthFrames);
        Assert.False(engine.TryReadNote(out _));
    }

    [Fact]
    public void QuantizesSongPositionWithoutChangingThePlayedTickLengthNearATempoChange()
    {
        var tempoMap = new TempoMap(480, [new(0, 500_000), new(480, 1_000_000)]);
        var song = new Song(tempoMap, [new SongTrack("Piano", [])]);
        var engine = NewEngine();
        engine.RecordSong(song, SilentMonitorSynth.Instance, countIn: false,
            offsetMilliseconds: 10, quantizationDivision: 4, timestamp: Timestamp(FirstFrame));
        // The attack falls on the first slower beat. Compensation moves it back into the faster beat.
        EnqueueNote(engine, 60, FirstFrame + 24_000, FirstFrame + 33_600);

        RenderThrough(engine, FirstFrame + 33_600);

        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal(FirstFrame + 23_520, note.CompensatedFrame);
        Assert.Equal(480, note.StartTick);
        Assert.Equal(96, note.LengthTicks);
        Assert.Equal(9_600, note.PlayedLengthFrames);
    }

    private static LatencyEngine NewEngine() =>
        new(SampleRate, SilentMonitorSynth.Instance, timestampFrequency: SampleRate);

    private static void EnqueueNote(LatencyEngine engine, byte pitch, long start, long end)
    {
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(start), 0x90, pitch, 100)));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(end), 0x80, pitch, 0)));
    }

    private static void RenderThrough(LatencyEngine engine, long finalFrame)
    {
        var output = new float[checked((int)(2 * (finalFrame - FirstFrame + 1)))];
        engine.Render(output, FirstFrame, new AudioClockAnchor(Timestamp(FirstFrame), FirstFrame));
    }

    private static long Timestamp(long frame) => ClockOrigin + frame;
}
