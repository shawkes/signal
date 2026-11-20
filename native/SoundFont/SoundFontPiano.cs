using MeltySynth;
using Signal.Native.Engine;

namespace Signal.Native.SoundFont;

public sealed class SoundFontPiano : IMonitorSynth
{
    private readonly Synthesizer _synth;
    private readonly bool _generalMidi;
    private readonly float[] _left = new float[256];
    private readonly float[] _right = new float[256];

    public SoundFontPiano(MeltySynth.SoundFont bank, int sampleRate, bool generalMidi = false)
    {
        _generalMidi = generalMidi;
        _synth = new Synthesizer(bank, new SynthesizerSettings(sampleRate)
        {
            // At 48 kHz these blocks span 0.33 ms. Events affect the next internal block.
            BlockSize = 16,
            EnableReverbAndChorus = false
        });
    }

    public void ProcessMidi(byte status, byte data1, byte data2)
    {
        int command = status & 0xF0;
        int channel = status & 0x0F;
        // Live monitoring keeps its piano preset. Song playback uses the synth's General MIDI messages.
        if (_generalMidi || command is 0x80 or 0x90 or 0xE0 || (command == 0xB0 && data1 != 0 && data1 != 32))
            _synth.ProcessMidiMessage(channel, command, data1, data2);
    }

    public void Render(Span<float> interleavedStereo)
    {
        while (interleavedStereo.Length >= 2)
        {
            int frames = Math.Min(_left.Length, interleavedStereo.Length / 2);
            _synth.Render(_left.AsSpan(0, frames), _right.AsSpan(0, frames));
            for (int frame = 0; frame < frames; frame++)
            {
                interleavedStereo[2 * frame] = _left[frame];
                interleavedStereo[2 * frame + 1] = _right[frame];
            }
            interleavedStereo = interleavedStereo[(2 * frames)..];
        }
    }

    public void Reset() => _synth.Reset();
}
