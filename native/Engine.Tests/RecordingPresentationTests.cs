using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class RecordingPresentationTests
{
    // One frame and one song tick per millisecond, with ten timestamp ticks per frame.
    private const long Origin = 50_000;
    private const int Rate = 1000;
    private readonly LatencyEngine _engine = new(Rate, SilentMonitorSynth.Instance, timestampFrequency: Rate * 10);
    private readonly Song _song = new(TempoMap.Constant(120, 500), [], new SongMeter(1, 4));
    private readonly HeldRecordingNote[] _held = new HeldRecordingNote[1];

    [Fact]
    public void CursorAndHeldLengthFollowPresentedAudioBetweenRenderCallbacks()
    {
        long take = _engine.RecordSong(_song, SilentMonitorSynth.Instance, false, 0, timestamp: Origin);
        Assert.True(_engine.TryEnqueue(new MidiMessage(Origin + 100, 0x90, 60, 100)));
        Render(0, 150);

        Assert.Equal(new RecordingDisplayPosition(20, null, null, 1), Read(take, 20));
        Assert.Equal(new HeldRecordingNote(0, 60, 0, 10, 10), _held[0]);
        Assert.Equal(new RecordingDisplayPosition(40, null, null, 1), Read(take, 40));
        Assert.Equal(30L, _held[0].LengthTicks);
        // While the device stalls, the cursor stops at the last available frame.
        Assert.Equal(150L, Read(take, 500).Tick);
        Assert.Equal(default, Read(take + 1, 40));
    }

    [Fact]
    public void CountdownAndLoopPassChangeWhenTheirFirstFrameReachesTheEndpoint()
    {
        long take = _engine.RecordSong(_song, SilentMonitorSynth.Instance, true, 0,
            timestamp: Origin, loop: new LoopRange(250, 750));
        Render(0, 505);
        Assert.Equal(new RecordingDisplayPosition(null, 1, null, 0), Read(take, 490));
        Assert.Equal(new RecordingDisplayPosition(250, null, 1, 0), Read(take, 500));

        Render(505, 515);
        Assert.Equal(new RecordingDisplayPosition(749, null, 1, 0), Read(take, 999));
        Assert.Equal(new RecordingDisplayPosition(250, null, 2, 0), Read(take, 1000));
        _engine.StopSongRecording(Origin + 1020 * 10);
        Render(1020, 10);
        Assert.Equal(default, Read(take, 1030));
    }

    private RecordingDisplayPosition Read(long take, int elapsedMilliseconds) =>
        _engine.ReadRecordingDisplay(take, Origin + elapsedMilliseconds * 10, _held);

    private void Render(long firstFrame, int frames) =>
        _engine.Render(new float[2 * frames], firstFrame, new AudioClockAnchor(Origin + firstFrame * 10, firstFrame));

    [Fact]
    public void TenMinuteUnquantizedTakeKeepsItsOpeningBeatAndIndividualTimingWithoutDisplayUpdates()
    {
        const int sampleRate = 2000;
        const int framesPerBeat = 1000;
        const int countInFrames = 4 * framesPerBeat;
        const int beats = 1200;
        const int lengthFrames = 125;
        const int inputLeadFrames = 224; // 112 ms
        const int outputLeadFrames = 30;
        const long frequency = 10_000_003;
        var song = new Song(TempoMap.Constant(120, 480), [], new SongMeter(4, 4));
        var engine = new LatencyEngine(sampleRate, SilentMonitorSynth.Instance, timestampFrequency: frequency);
        long take = engine.RecordSong(song, SilentMonitorSynth.Instance, true, -112, 0, timestamp: Origin);
        int[] jitter = [0, -25, 25, 0]; // +/-12 song ticks. Detect accidental snapping as well as drift.
        int[] blockSizes = [17, 37, 53, 41];
        var buffer = new float[2 * blockSizes.Max()];
        int nextEvent = 0;
        int block = 0;
        int end = countInFrames + beats * framesPerBeat;
        for (int frame = 0; frame < end;)
        {
            while (nextEvent < beats * 2)
            {
                int beat = nextEvent / 2;
                bool release = (nextEvent & 1) != 0;
                long receipt = countInFrames + beat * framesPerBeat + jitter[beat % jitter.Length] -
                    inputLeadFrames + (release ? lengthFrames : 0);
                if (receipt > frame - outputLeadFrames)
                    break;
                Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(receipt),
                    release ? (byte)0x80 : (byte)0x90, 60, 100)));
                nextEvent++;
            }
            int frames = blockSizes[block++ % blockSizes.Length];
            engine.Render(buffer.AsSpan(0, frames * 2), frame, new(Timestamp(frame), frame));
            frame += frames;
        }

        // The simulated UI does not wake until the take is over.
        Assert.Equal(beats * 2, nextEvent);
        Assert.Equal((beats - 1) * 480L, engine.ReadRecordingDisplay(take,
            Timestamp(countInFrames + (beats - 1) * framesPerBeat), []).Tick);
        for (int beat = 0; beat < beats; beat++)
        {
            Assert.True(engine.TryReadNote(out var note));
            Assert.Equal(take, note.TakeId);
            Assert.Equal(beat * 480L + jitter[beat % jitter.Length] * 480L / framesPerBeat, note.StartTick);
            Assert.Equal(60L, note.LengthTicks);
        }
        Assert.False(engine.TryReadNote(out _));
        Assert.Equal(0L, engine.DroppedMidiMessages);
        Assert.Equal(0L, engine.DroppedRecordedNotes);

        // The endpoint takes 100 ppm longer per sample than the nominal rate. A free-running
        // UI or recording clock accumulates a 60 ms discrepancy over this take.
        static long Timestamp(long frame) =>
            Origin + (long)Math.Round(frame * (frequency / (double)sampleRate) * 1.0001);
    }
}
