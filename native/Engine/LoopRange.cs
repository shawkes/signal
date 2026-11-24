namespace Signal.Native.Engine;

/// <summary>A song loop in ticks, including the start and excluding the end.</summary>
public readonly record struct LoopRange(long StartTick, long EndTick)
{
    /// <summary>Loops from bar one through the last measure covered by the song's exclusive end tick.</summary>
    public static LoopRange WholeSong(long endTick, MeterMap meterMap) =>
        new(0, meterMap.NextBarTick(endTick > 0 ? endTick - 1 : 0));
}
