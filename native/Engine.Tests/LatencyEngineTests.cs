using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public class LatencyEngineTests
{
    private const int SampleRate = 48_000;
    private const long ClockOrigin = 360_000_000;

    [Theory]
    [InlineData(10, 24_000, 480, 0)]
    [InlineData(0, 24_480, 490, 10)]
    [InlineData(-10, 24_960, 499, 20)]
    public void AppliesSignedOffsetToTheReceiptFrameEvenWhenMonitoringIsLate(
        double offsetMs, long compensatedFrame, long startTick, double msFromBeat)
    {
        var engine = NewEngine(SilentMonitorSynth.Instance);
        var take = engine.SetRecording(true, offsetMs, timestamp: Timestamp(24_000));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(24_480), 0x91, 64, 87)));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(28_080), 0x91, 64, 0)));
        Render(engine, firstFrame: 28_500, frames: 128);

        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal(take, note.TakeId);
        Assert.Equal((byte)1, note.Channel);
        Assert.Equal((byte)64, note.NoteNumber);
        Assert.Equal((byte)87, note.Velocity);
        Assert.Equal(24_480, note.ReceivedFrame);
        Assert.Equal(compensatedFrame, note.CompensatedFrame);
        Assert.Equal(3_600, note.PlayedLengthFrames);
        Assert.Equal(startTick, note.StartTick);
        Assert.Equal(72, note.LengthTicks);
        Assert.Equal(1, note.NearestBeat);
        Assert.Equal(10, note.RawMillisecondsFromBeat);
        Assert.Equal(msFromBeat, note.MillisecondsFromBeat);
    }

    [Theory]
    [InlineData(8, 18_240, 240)]
    [InlineData(32, 4_950, 60)]
    [InlineData(64, 4_950, 90)]
    public void QuantizesAfterCompensationAndPreservesPlayedLength(int division, long attack, long expectedTick)
    {
        var engine = NewEngine(SilentMonitorSynth.Instance);
        engine.SetRecording(true, 10, quantizationDivision: division, timestamp: Timestamp(0));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(attack), 0x90, 60, 100)));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(attack + 3_600), 0x80, 60, 0)));
        Render(engine, firstFrame: 23_000, frames: 128);

        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal(attack - 480, note.CompensatedFrame);
        Assert.Equal(expectedTick, note.StartTick);
        Assert.Equal(72, note.LengthTicks);
        Assert.Equal(3_600, note.PlayedLengthFrames);
    }

    [Theory]
    [InlineData(0, 0, 470)]
    [InlineData(10, 8, 480)]
    public void MeasuresLiveAttackDelayIndependentlyOfBeatOffsetAndQuantization(
        double offsetMs, int quantization, long expectedStartTick)
    {
        var engine = NewEngine(new GateSynth());
        engine.SetRecording(true, offsetMs, quantization, Timestamp(0));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(23_520), 0x90, 60, 100)));
        // Receipt falls 10 ms before the beat. The next writable sample is 3 ms after receipt.
        Assert.All(Render(engine, firstFrame: 23_664, frames: 64), sample => Assert.Equal(0.1f, sample));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(24_000), 0x80, 60, 0)));
        // The note-off waits 12.5 ms for this callback. The reading still belongs to the attack.
        Assert.All(Render(engine, firstFrame: 24_600, frames: 64), sample => Assert.Equal(0f, sample));

        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal(3.0, note.MonitorDelayMilliseconds);
        Assert.Equal(-10.0, note.RawMillisecondsFromBeat);
        Assert.Equal(-10.0 - offsetMs, note.MillisecondsFromBeat);
        Assert.Equal(expectedStartTick, note.StartTick);
    }

    [Fact]
    public void SynthAndClickStartAtTheSameSampleWhenMidiIsOnTheBeat()
    {
        var engine = NewEngine(new GateSynth());
        engine.PreviewMetronomeEnabled = true;
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(24_000), 0x90, 60, 100)));

        var output = Render(engine, firstFrame: 23_998, frames: 4);

        Assert.Equal(new float[] { 0, 0, 0, 0 }, output[..4]);
        Assert.Equal(0.26f, output[4], precision: 5);
        Assert.Equal(output[4], output[5]);
    }

    [Fact]
    public void HoldsFutureMidiAndRendersEarlierTimestampsFirst()
    {
        var engine = NewEngine(new GateSynth());
        engine.SetRecording(true, 0, timestamp: Timestamp(0));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(24_002), 0x80, 60, 0)));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(24_000), 0x90, 60, 100)));

        Assert.Equal(new float[] { 0, 0, 0.1f, 0.1f }, Render(engine, firstFrame: 23_999, frames: 2));
        Assert.Equal(new float[] { 0.1f, 0.1f, 0, 0 }, Render(engine, firstFrame: 24_001, frames: 2));
        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal(0.0, note.MonitorDelayMilliseconds);
    }

    [Fact]
    public void StopsHeldNotesAtTheStopTimestampAndKeepsTakeIdsDistinct()
    {
        var engine = NewEngine(SilentMonitorSynth.Instance);
        var firstTake = engine.SetRecording(true, 0, timestamp: Timestamp(0));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(100), 0x90, 60, 100)));
        engine.SetRecording(false, 0, timestamp: Timestamp(700));
        var secondTake = engine.SetRecording(true, 0, timestamp: Timestamp(900));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(1_000), 0x90, 62, 98)));
        Assert.True(engine.TryEnqueue(new MidiMessage(Timestamp(1_700), 0x80, 62, 0)));
        Render(engine, firstFrame: 1_800, frames: 256);

        Assert.True(engine.TryReadNote(out var first));
        Assert.Equal(firstTake, first.TakeId);
        Assert.Equal(600, first.PlayedLengthFrames);
        Assert.True(engine.TryReadNote(out var second));
        Assert.Equal(secondTake, second.TakeId);
        Assert.Equal(700, second.PlayedLengthFrames);
        Assert.False(engine.TryReadNote(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllocatesNoManagedMemoryDuringWarmRendering(bool compressorEnabled)
    {
        var engine = NewEngine(new GateSynth());
        engine.CompressorEnabled = compressorEnabled;
        engine.PreviewMetronomeEnabled = true;
        engine.SetRecording(true, 10, timestamp: Timestamp(0));
        var output = new float[256];
        for (var i = 0; i < 300; i++)
        {
            var frame = i * 128;
            engine.TryEnqueue(new MidiMessage(Timestamp(frame + 1), 0x90, 60, 100));
            engine.TryEnqueue(new MidiMessage(Timestamp(frame + 47), 0x80, 60, 0));
            engine.Render(output, frame, new AudioClockAnchor(Timestamp(frame), frame));
            engine.TryReadNote(out _);
        }

        long allocatedBytes = 0;
        for (var i = 300; i < 600; i++)
        {
            var frame = i * 128;
            engine.TryEnqueue(new MidiMessage(Timestamp(frame + 1), 0x90, 60, 100));
            engine.TryEnqueue(new MidiMessage(Timestamp(frame + 47), 0x80, 60, 0));
            var anchor = new AudioClockAnchor(Timestamp(frame), frame);
            var before = GC.GetAllocatedBytesForCurrentThread();
            engine.Render(output, frame, anchor);
            allocatedBytes += GC.GetAllocatedBytesForCurrentThread() - before;
            engine.TryReadNote(out _);
        }

        Assert.Equal(0, allocatedBytes);
    }

    [Fact]
    public void OutputGainChangesDuringPlaybackWithoutAnAbruptStep()
    {
        var engine = NewEngine(new StereoSynth(0.1f, -0.05f));
        Assert.Equal(0.1f, Render(engine, firstFrame: 0, frames: 1)[0]);

        engine.OutputGainDb = 6;
        var louder = Render(engine, firstFrame: 1, frames: 2_400);

        Assert.InRange(louder[0], 0.1f, 0.101f);
        Assert.InRange(louder[^2], 0.199f, 0.200f);
        Assert.Equal(-0.5f * louder[^2], louder[^1]);
    }

    [Fact]
    public void LimitsTheMixedSignalWithoutChangingStereoBalance()
    {
        var engine = NewEngine(new StereoSynth(0.75f, -0.375f));
        var song = new Song(engine.TempoMap, [new SongTrack("Test", [new(0, 480, 0, 60, 127)])]);
        Assert.True(engine.PlaySong(song, new StereoSynth(0.75f, -0.375f)));

        var mixed = Render(engine, firstFrame: 0, frames: 2);

        Assert.Equal(0.79433f, mixed[0], precision: 5);
        Assert.Equal(-0.5f * mixed[0], mixed[1]);
    }

    [Fact]
    public void CompressorToggleCrossfadesAndReturnsToBypass()
    {
        var engine = NewEngine(new StereoSynth(0.05f, -0.025f));
        Assert.Equal(0.05f, Render(engine, firstFrame: 0, frames: 1)[0]);

        engine.CompressorEnabled = true;
        var enabled = Render(engine, firstFrame: 1, frames: 1_024);
        Assert.InRange(enabled[0], 0.05f, 0.0502f);
        Assert.InRange(enabled[^2], 0.099f, 0.100f);

        engine.CompressorEnabled = false;
        var bypassed = Render(engine, firstFrame: 1_025, frames: 1_024);
        Assert.InRange(bypassed[0], enabled[^2] - 0.0002f, enabled[^2]);
        Assert.Equal(0.05f, bypassed[^2]);
    }

    [Fact]
    public void CompressorReducesLoudSustainedSignalWithoutChangingStereoBalance()
    {
        var engine = NewEngine(new StereoSynth(0.5f, -0.25f));
        engine.CompressorEnabled = true;

        var compressed = Render(engine, firstFrame: 0, frames: 2_400);

        Assert.InRange(compressed[^2], 0.395f, 0.4f);
        Assert.Equal(-0.5f * compressed[^2], compressed[^1]);
    }

    private static LatencyEngine NewEngine(IMonitorSynth synth) => new(SampleRate, synth, timestampFrequency: SampleRate);

    private static long Timestamp(long frame) => ClockOrigin + frame;

    private static float[] Render(LatencyEngine engine, long firstFrame, int frames)
    {
        var output = new float[frames * 2];
        const int padding = 144;
        engine.Render(output, firstFrame, new AudioClockAnchor(Timestamp(firstFrame - padding), firstFrame - padding));
        return output;
    }

    private sealed class GateSynth : IMonitorSynth
    {
        private bool _gate;

        public void ProcessMidi(byte status, byte data1, byte data2) => _gate = status == 0x90 && data2 != 0;

        public void Render(Span<float> interleavedStereo) => interleavedStereo.Fill(_gate ? 0.1f : 0);
    }

    private sealed class StereoSynth(float left, float right) : IMonitorSynth
    {
        public void ProcessMidi(byte status, byte data1, byte data2) { }

        public void Render(Span<float> stereo)
        {
            for (int i = 0; i < stereo.Length; i += 2)
            {
                stereo[i] = left;
                stereo[i + 1] = right;
            }
        }
    }
}
