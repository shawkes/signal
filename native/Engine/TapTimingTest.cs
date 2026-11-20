namespace Signal.Native.Engine;

/// <summary>Estimates an offset from signed tap errors against the audio beat grid.</summary>
public sealed class TapTimingTest
{
    public const int PracticeTaps = 2;
    public const int MinimumTaps = 6;
    public const int TargetTaps = 12;

    private readonly double[] _errors = new double[TargetTaps];
    private long? _lastBeat;

    public int PracticeRemaining { get; private set; } = PracticeTaps;
    public int MeasuredTaps { get; private set; }
    public bool Complete => MeasuredTaps == TargetTaps;
    public double? OffsetMilliseconds { get; private set; }

    public void AddTap(long beat, double millisecondsFromBeat)
    {
        if (Complete || beat <= _lastBeat || !double.IsFinite(millisecondsFromBeat))
            return;

        _lastBeat = beat;
        if (PracticeRemaining > 0)
        {
            PracticeRemaining--;
            return;
        }

        _errors[MeasuredTaps++] = millisecondsFromBeat;
        if (MeasuredTaps >= MinimumTaps)
        {
            Span<double> sorted = stackalloc double[TargetTaps];
            var samples = sorted[..MeasuredTaps];
            _errors.AsSpan(0, MeasuredTaps).CopyTo(samples);
            OffsetMilliseconds = TimingStatistics.SortAndGetMedian(samples);
        }
    }
}
