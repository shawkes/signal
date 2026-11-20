namespace Signal.Native.Engine;

/// <summary>A song loop in ticks, including the start and excluding the end.</summary>
public readonly record struct LoopRange(long StartTick, long EndTick);
