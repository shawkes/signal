using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class RecordingTestTimingTests
{
    [Fact]
    public void ReportsPlacementForEveryNoteEvenWithoutMonitorEstimates()
    {
        var timing = new RecordingTestTiming();
        Assert.Null(timing.Summarize());
        foreach (double? delay in new double?[] { null, -1, double.NaN, double.PositiveInfinity })
            timing.Add(Note(-80, -90, delay));
        timing.Add(Note(-68, -78, null));
        timing.Add(Note(220, 210, null));

        Assert.Equal(new RecordingTestResult(6, -80, -90, null, null), timing.Summarize());
    }

    [Fact]
    public void AggregatesTheWholeTakeAndUsesNearestRankForAvailableMonitorEstimates()
    {
        var timing = new RecordingTestTiming();
        // The first 80 note placements should contribute even if a UI displays only the latest 64.
        // In reverse order the 20 valid monitor values are [19..0]; the 95th percentile is 18.
        for (int i = 99; i >= 0; i--)
            timing.Add(Note(i, i - 10, i < 20 ? i : null));

        Assert.Equal(new RecordingTestResult(100, 49.5, 39.5, 9.5, 18), timing.Summarize());
    }

    [Theory]
    [InlineData(-77, 10)]
    [InlineData(24, -16)]
    [InlineData(0, 14)]
    public void ConsistentTapsSuggestTheTotalRawOffsetAcrossReleaseOrder(int raw, int currentOffset)
    {
        var timing = new RecordingTestTiming();
        foreach (int beat in new[] { 2, 0, 5, 4, 1 })
            timing.Add(Note(raw + beat - 2.5, raw + beat - 2.5 - currentOffset, 12) with { NearestBeat = beat });
        Assert.Null(timing.Summarize()!.SuggestedOffsetMilliseconds);
        timing.Add(Note(raw + 0.5, raw + 0.5 - currentOffset, 12) with { NearestBeat = 3 });

        var result = timing.Summarize()!;
        Assert.Equal((double)raw, result.SuggestedOffsetMilliseconds);
        Assert.Equal((double)(raw - currentOffset), result.MedianAdjustedMilliseconds);
        Assert.Null(timing.Summarize(playedWithClick: false)!.SuggestedOffsetMilliseconds);
    }

    [Fact]
    public void ChordBeatsDoNotWeightTheRecommendationButStillCountInTheTakeSummary()
    {
        var timing = new RecordingTestTiming();
        for (int i = 0; i < 12; i++)
            timing.Add(Note(-80, -90, 12) with { NearestBeat = 99 });
        Assert.Null(timing.Summarize()!.SuggestedOffsetMilliseconds);
        for (int beat = 0; beat < 6; beat++)
            timing.Add(Note(20, 10, 12) with { NearestBeat = beat });

        var result = timing.Summarize()!;
        Assert.Equal(18, result.NoteCount);
        Assert.Equal(-80, result.MedianRawMilliseconds);
        Assert.Equal(20d, result.SuggestedOffsetMilliseconds);
    }

    [Theory]
    [InlineData(240, 220)]
    [InlineData(-215, -240)]
    [InlineData(490, -10)]
    public void AmbiguousMidpointsOrLargeOffsetsThatSelectAnotherClickSuppressTheSuggestion(int raw, int adjusted)
    {
        var timing = new RecordingTestTiming();
        for (int beat = 0; beat < 8; beat++)
            timing.Add(Note(raw, adjusted, 8) with { NearestBeat = beat });
        var result = timing.Summarize()!;
        Assert.Null(result.SuggestedOffsetMilliseconds);
        Assert.Equal((double)raw, result.MedianRawMilliseconds);
        Assert.Equal(8d, result.MedianMonitorDelayMilliseconds);
    }

    [Fact]
    public void OneOutlierIsToleratedButSplitEarlyAndLateGroupsDoNotGiveACalibration()
    {
        var timing = new RecordingTestTiming();
        for (int beat = 0; beat < 7; beat++)
            timing.Add(Note(beat == 0 ? -150 : 25, beat == 0 ? -160 : 15, 0) with { NearestBeat = beat });
        Assert.Equal(25d, timing.Summarize()!.SuggestedOffsetMilliseconds);
        for (int beat = 7; beat < 13; beat++)
            timing.Add(Note(-150, -160, 0) with { NearestBeat = beat });
        Assert.Null(timing.Summarize()!.SuggestedOffsetMilliseconds);
    }

    private static RecordedNote Note(double raw, double adjusted, double? monitorDelay) =>
        new RecordedNote(1, 0, 0, 0, 60, 100, 0, 0, 0, 0, adjusted, raw) { MonitorDelayMilliseconds = monitorDelay };
}
