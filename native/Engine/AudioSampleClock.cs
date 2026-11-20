namespace Signal.Native.Engine;

/// <summary>Timestamp and stream frame at the same presentation point.</summary>
public readonly record struct AudioClockAnchor(long Timestamp, long SampleFrame);

public readonly struct AudioSampleClock
{
    private readonly double _framesPerTimestampTick;
    private readonly double _timestampTicksPerFrame;

    public AudioSampleClock(int sampleRate, long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timestampFrequency);
        _framesPerTimestampTick = sampleRate / (double)timestampFrequency;
        _timestampTicksPerFrame = timestampFrequency / (double)sampleRate;
    }

    public long FrameAtTimestamp(long timestamp, AudioClockAnchor anchor)
    {
        var ticksSinceAnchor = (double)((Int128)timestamp - anchor.Timestamp);
        return checked(anchor.SampleFrame + (long)Math.Round(ticksSinceAnchor * _framesPerTimestampTick, MidpointRounding.AwayFromZero));
    }

    public long TimestampAtFrame(long frame, AudioClockAnchor anchor)
    {
        var framesSinceAnchor = (double)((Int128)frame - anchor.SampleFrame);
        return checked(anchor.Timestamp + (long)Math.Round(framesSinceAnchor * _timestampTicksPerFrame, MidpointRounding.AwayFromZero));
    }
}
