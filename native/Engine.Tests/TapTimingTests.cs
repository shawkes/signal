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
        const int firstBeat = 500;
        const int knownOffset = 12;
        var engine = new LatencyEngine(rate, SilentMonitorSynth.Instance, timestampFrequency: rate)
        {
            PreviewMetronomeEnabled = true
        };
        long take = engine.SetRecording(true, 0, timestamp: 0);
        int totalTaps = TapTimingTest.PracticeTaps + TapTimingTest.TargetTaps;
        for (int index = 0; index < totalTaps; index++)
        {
            long tap = firstBeat * (index + 1) + knownOffset;
            Assert.True(engine.TryEnqueue(new MidiMessage(tap, 0x90, 60, 100)));
            Assert.True(engine.TryEnqueue(new MidiMessage(tap + 20, 0x80, 60, 0)));
        }

        var buffer = new float[512];
        for (long frame = 0; frame < (totalTaps + 1) * firstBeat; frame += buffer.Length / 2)
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
}
