namespace Signal.Native.Engine;

/// <summary>A MIDI channel message timestamped at receipt, in the audio clock's timestamp units.</summary>
public readonly record struct MidiMessage(long Timestamp, byte Status, byte Data1, byte Data2);

public interface IMonitorSynth
{
    void ProcessMidi(byte status, byte data1, byte data2);

    /// <summary>Overwrites this interleaved stereo block. Called on the audio thread.</summary>
    void Render(Span<float> interleavedStereo);
}

public sealed class SilentMonitorSynth : IMonitorSynth
{
    public static SilentMonitorSynth Instance { get; } = new();

    private SilentMonitorSynth() { }

    public void ProcessMidi(byte status, byte data1, byte data2) { }

    public void Render(Span<float> interleavedStereo) => interleavedStereo.Clear();
}
