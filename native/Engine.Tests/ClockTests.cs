using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public class ClockTests
{
    [Fact]
    public void MapsSignedTimestampDeltasRelativeToThePresentationAnchor()
    {
        const long timestampFrequency = 10_000_000;
        var clock = new AudioSampleClock(48_000, timestampFrequency);
        var anchor = new AudioClockAnchor(8_000_000_000_000_000_000, 48_000);

        Assert.Equal(47_520, clock.FrameAtTimestamp(anchor.Timestamp - 100_000, anchor));
        Assert.Equal(48_480, clock.FrameAtTimestamp(anchor.Timestamp + 100_000, anchor));
        Assert.Equal(anchor.Timestamp + 100_000, clock.TimestampAtFrame(48_480, anchor));
        Assert.Equal(48_001, clock.FrameAtTimestamp(anchor.Timestamp + 208, anchor));
    }

    [Fact]
    public void ConvertsAcrossTempoChangesOnTheSameSampleTimeline()
    {
        var map = new TempoMap(480, [new TempoChange(480, 1_000_000)]);

        Assert.Equal(24_000, map.TickToSample(480, 48_000));
        Assert.Equal(48_000, map.TickToSample(720, 48_000));
        Assert.Equal(72_000, map.TickToSample(960, 48_000));
        Assert.Equal(720, map.SampleToTick(48_000, 48_000), precision: 8);
        Assert.Equal(-48, map.SampleToTick(-2_400, 48_000), precision: 8);
    }

    [Fact]
    public void RoundsEachBeatFromItsAbsoluteTimeWithoutDrift()
    {
        const int sampleRate = 44_100;
        const int beats = 1_000;
        var map = TempoMap.Constant(123);
        var expected = (long)Math.Round(beats * map.Changes[0].MicrosecondsPerQuarterNote * (sampleRate / 1_000_000.0));

        Assert.Equal(expected, map.TickToSample(beats * map.TicksPerQuarter, sampleRate));
    }
}
