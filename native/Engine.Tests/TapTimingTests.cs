using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class TapTimingTests
{
    [Fact]
    public void UsesTheMedianAfterPracticeAndAcceptsOneTapPerBeat()
    {
        var test = new TapTimingTest();
        test.AddTap(3, 120);
        test.AddTap(3, -180);
        test.AddTap(4, -120);

        double[] taps = [-160, 10, 12, 14, 16, 130];
        for (int i = 0; i < taps.Length; i++)
        {
            test.AddTap(5 + i, taps[i]);
            test.AddTap(5 + i, 200);
        }

        Assert.Equal(0, test.PracticeRemaining);
        Assert.Equal(TapTimingTest.MinimumTaps, test.MeasuredTaps);
        Assert.Equal(13, test.OffsetMilliseconds);
        for (int i = 0; i < 20; i++)
            test.AddTap(20 + i, -12);
        Assert.True(test.Complete);
        Assert.Equal(TapTimingTest.TargetTaps, test.MeasuredTaps);
    }

    [Fact]
    public void RecoversAKnownSignedOffsetFromMidiReceiptsAgainstTheAudioClock()
    {
        const int rate = 1_000;
        const int beatLength = 500;
        const int clickStart = 173;
        const int knownOffset = 12;
        var engine = new LatencyEngine(rate, SilentMonitorSynth.Instance, timestampFrequency: rate);
        long take = engine.SetRecording(true, 0, timestamp: clickStart, playTestMetronome: true);
        int totalTaps = TapTimingTest.PracticeTaps + TapTimingTest.TargetTaps;
        for (int index = 0; index < totalTaps; index++)
        {
            long tap = clickStart + beatLength * (index + 1) + knownOffset;
            Assert.True(engine.TryEnqueue(new MidiMessage(tap, 0x90, 60, 100)));
            Assert.True(engine.TryEnqueue(new MidiMessage(tap + 20, 0x80, 60, 0)));
        }

        var buffer = new float[512];
        for (long frame = 0; frame < clickStart + (totalTaps + 1) * beatLength; frame += buffer.Length / 2)
            engine.Render(buffer, frame, new AudioClockAnchor(frame, frame));

        var test = new TapTimingTest();
        while (engine.TryReadNote(out var note))
        {
            Assert.Equal(take, note.TakeId);
            test.AddTap(note.NearestBeat, note.RawMillisecondsFromBeat);
        }
        Assert.True(test.Complete);
        Assert.Equal(knownOffset, test.OffsetMilliseconds);
    }

    [Fact]
    public void RecordingTestClickStartsAndStopsInsideTheBufferAndRestartsOnItsOwnDownbeat()
    {
        const int rate = 1000;
        var engine = new LatencyEngine(rate, SilentMonitorSynth.Instance, timestampFrequency: rate);
        engine.SetRecording(true, -90, quantizationDivision: 16, timestamp: 173, playTestMetronome: true);
        Assert.True(engine.TryEnqueue(new MidiMessage(650, 0x90, 60, 100)));
        Assert.True(engine.TryEnqueue(new MidiMessage(661, 0x80, 60, 0)));
        engine.SetRecording(false, 0, timestamp: 681);
        engine.SetRecording(true, 0, timestamp: 1043, playTestMetronome: true);
        engine.SetRecording(false, 0, timestamp: 1093);
        engine.SetRecording(true, 0, timestamp: 1187);
        engine.SetRecording(false, 0, timestamp: 1900);

        var pcm = new float[4000];
        engine.Render(pcm, 0, new(0, 0));

        Assert.All(pcm[..346], sample => Assert.Equal(0f, sample));
        Assert.Equal(0.25f, pcm[173 * 2]);
        Assert.Equal(0.16f, pcm[673 * 2]);
        Assert.All(pcm[(681 * 2)..(1043 * 2)], sample => Assert.Equal(0f, sample));
        Assert.Equal(0.25f, pcm[1043 * 2]);
        Assert.All(pcm[(1093 * 2)..], sample => Assert.Equal(0f, sample));
        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal((-23d, 67d, 600L), (note.RawMillisecondsFromBeat, note.MillisecondsFromBeat, note.StartTick));
        Assert.Equal(0d, note.MonitorDelayMilliseconds);
    }
}
