namespace Signal.Native.Engine;

/// <summary>A MIDI channel message timestamped at receipt, in the audio clock's timestamp units.</summary>
public readonly record struct MidiMessage(long Timestamp, byte Status, byte Data1, byte Data2);

/// <summary>The destination channel and caller's track index for a live MIDI input channel.</summary>
public readonly record struct LiveMidiDestination(byte Channel, int TrackIndex);

public interface IMonitorSynth
{
    void ProcessMidi(byte status, byte data1, byte data2);

    /// <summary>Overwrites this interleaved stereo block. Called on the audio thread.</summary>
    void Render(Span<float> interleavedStereo);

    /// <summary>Silences voices and restores channel state on the audio thread before a transport jump.</summary>
    void Reset()
    {
        for (int channel = 0; channel < 16; channel++)
        {
            byte control = (byte)(0xb0 | channel);
            ProcessMidi(control, 120, 0);
            ProcessMidi(control, 121, 0);
            ProcessMidi(control, 0, 0);
            ProcessMidi(control, 32, 0);
            ProcessMidi((byte)(0xc0 | channel), 0, 0);
        }
    }
}

public sealed class SilentMonitorSynth : IMonitorSynth
{
    public static SilentMonitorSynth Instance { get; } = new();

    private SilentMonitorSynth() { }

    public void ProcessMidi(byte status, byte data1, byte data2) { }

    public void Render(Span<float> interleavedStereo) => interleavedStereo.Clear();

    public void Reset() { }
}
