using System.Runtime.InteropServices;

namespace Signal.Native.Engine;

public sealed record RecordingTestResult(
    int NoteCount,
    double MedianRawMilliseconds,
    double MedianAdjustedMilliseconds,
    double? MedianMonitorDelayMilliseconds,
    double? P95MonitorDelayMilliseconds)
{
    /// <summary>Signed total recording offset estimated from notes played one per click.</summary>
    public double? SuggestedOffsetMilliseconds { get; init; }
}

/// <summary>Collects completed placement-test notes on the UI thread for one end-of-take summary.</summary>
public sealed class RecordingTestTiming
{
    // Placement-test clicks are 500 ms apart. Within 25 ms of the midpoint the intended
    // click is ambiguous. Compare both positions because the recorder picks the click after compensation.
    private const double MaxDistanceFromClickMilliseconds = 225;
    private const double AgreementWindowMilliseconds = 40;

    private readonly List<double> _rawErrors = [];
    private readonly List<double> _adjustedErrors = [];
    private readonly List<double> _monitorDelays = [];
    private readonly Dictionary<long, double?> _singleTapByBeat = [];

    public void Add(RecordedNote note)
    {
        _rawErrors.Add(note.RawMillisecondsFromBeat);
        _adjustedErrors.Add(note.MillisecondsFromBeat);
        if (note.MonitorDelayMilliseconds is double delay && double.IsFinite(delay) && delay >= 0)
            _monitorDelays.Add(delay);

        double? tap = Math.Abs(note.RawMillisecondsFromBeat) < MaxDistanceFromClickMilliseconds &&
            Math.Abs(note.MillisecondsFromBeat) < MaxDistanceFromClickMilliseconds ? note.RawMillisecondsFromBeat : null;
        if (!_singleTapByBeat.TryAdd(note.NearestBeat, tap))
            _singleTapByBeat[note.NearestBeat] = null;
    }

    public RecordingTestResult? Summarize(bool playedWithClick = true)
    {
        if (_rawErrors.Count == 0)
            return null;

        double? medianDelay = null;
        double? p95Delay = null;
        if (_monitorDelays.Count > 0)
        {
            medianDelay = TimingStatistics.SortAndGetMedian(CollectionsMarshal.AsSpan(_monitorDelays));
            int rank = (int)Math.Ceiling(0.95 * _monitorDelays.Count);
            p95Delay = _monitorDelays[rank - 1];
        }

        return new RecordingTestResult(
            _rawErrors.Count,
            TimingStatistics.SortAndGetMedian(CollectionsMarshal.AsSpan(_rawErrors)),
            TimingStatistics.SortAndGetMedian(CollectionsMarshal.AsSpan(_adjustedErrors)),
            medianDelay,
            p95Delay)
        {
            SuggestedOffsetMilliseconds = playedWithClick ? EstimateRecordingOffset() : null
        };
    }

    private double? EstimateRecordingOffset()
    {
        if (_singleTapByBeat.Count < TapTimingTest.MinimumTaps)
            return null;

        var taps = _singleTapByBeat.Values.Where(value => value.HasValue).Select(value => value.GetValueOrDefault()).ToArray();
        if (taps.Length < TapTimingTest.MinimumTaps)
            return null;

        // Require four out of five occupied beats to be single, unambiguous taps near the
        // same offset. A chord occupies one beat regardless of how its notes are released.
        double median = TimingStatistics.SortAndGetMedian(taps);
        int agree = taps.Count(tap => Math.Abs(tap - median) <= AgreementWindowMilliseconds);
        return agree >= Math.Ceiling(_singleTapByBeat.Count * 0.8) ? median : null;
    }
}

internal static class TimingStatistics
{
    public static double SortAndGetMedian(Span<double> samples)
    {
        samples.Sort();
        return (samples[(samples.Length - 1) / 2] + samples[samples.Length / 2]) / 2;
    }
}
