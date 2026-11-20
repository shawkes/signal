namespace Signal.Native.Engine;

/// <summary>The beats per bar and note value of each beat in a time signature.</summary>
public sealed record SongMeter
{
    public static SongMeter CommonTime { get; } = new(4, 4);

    public SongMeter(int numerator, int denominator)
    {
        if (numerator is < 1 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(numerator));
        if (denominator <= 0 || (denominator & (denominator - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(denominator), "The beat unit must be a positive power of two.");

        Numerator = numerator;
        Denominator = denominator;
    }

    public int Numerator { get; }
    public int Denominator { get; }
}
