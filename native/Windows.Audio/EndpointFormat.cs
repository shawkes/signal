using System.Numerics;
using System.Runtime.InteropServices;

namespace Signal.Native.Windows.Audio;

internal readonly record struct EndpointFormat(int SampleRate, int Channels, int Bits, int ValidBits, bool IsFloat, uint ChannelMask)
{
    private static readonly Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71");
    private static readonly Guid Float = new("00000003-0000-0010-8000-00AA00389B71");
    private const uint FrontLeft = 1;
    private const uint FrontRight = 2;

    internal bool IsStereoFloat => Channels == 2 && IsFloat;

    internal static EndpointFormat Read(nint pointer)
    {
        var format = Marshal.PtrToStructure<WaveFormat>(pointer);
        int validBits = format.BitsPerSample;
        uint mask = 0;
        Guid kind = format.Tag switch { 1 => Pcm, 3 => Float, _ => Guid.Empty };
        if (format.Tag == 0xFFFE && format.ExtraSize >= 22)
        {
            var extended = Marshal.PtrToStructure<WaveFormatExtensible>(pointer);
            kind = extended.SubFormat;
            validBits = extended.ValidBits > 0 ? extended.ValidBits : validBits;
            mask = extended.ChannelMask;
        }

        bool floatSupported = kind == Float && format.BitsPerSample == 32;
        bool pcmSupported = kind == Pcm && format.BitsPerSample is 8 or 16 or 24 or 32;
        if ((!floatSupported && !pcmSupported) ||
            format.Channels == 0 || format.SamplesPerSecond == 0 ||
            validBits > format.BitsPerSample || format.BlockAlign != format.Channels * format.BitsPerSample / 8)
            throw new NotSupportedException($"Unsupported WASAPI mix format ({format.Channels} channels, {format.BitsPerSample} bits, subformat {kind}).");

        return new EndpointFormat(checked((int)format.SamplesPerSecond), format.Channels, format.BitsPerSample, validBits, floatSupported, mask);
    }

    internal unsafe void Write(Span<float> stereo, nint output)
    {
        bool mono = Channels == 1;
        int frameCount = stereo.Length / 2;
        bool frontStereo = (ChannelMask & (FrontLeft | FrontRight)) == (FrontLeft | FrontRight);
        int leftChannel = frontStereo ? FindChannel(FrontLeft) : 0;
        int rightChannel = frontStereo ? FindChannel(FrontRight) : 1;
        int bytesPerSample = Bits / 8;
        int bytesPerFrame = Channels * bytesPerSample;
        var buffer = new Span<byte>((void*)output, frameCount * bytesPerFrame);

        // An unsigned 8-bit PCM sample is centered at 128. Other formats use all-zero bytes for silence.
        buffer.Fill(Bits == 8 ? (byte)128 : (byte)0);
        for (int frame = 0; frame < frameCount; frame++)
        {
            float left = stereo[frame * 2];
            float right = stereo[frame * 2 + 1];
            int offset = frame * bytesPerFrame;
            if (mono)
            {
                WriteSample(buffer[offset..], (left + right) * 0.5f);
            }
            else
            {
                WriteSample(buffer[(offset + leftChannel * bytesPerSample)..], left);
                WriteSample(buffer[(offset + rightChannel * bytesPerSample)..], right);
            }
        }
    }

    private int FindChannel(uint speaker) => BitOperations.PopCount(ChannelMask & (speaker - 1));

    private void WriteSample(Span<byte> destination, float sample)
    {
        if (IsFloat)
        {
            MemoryMarshal.Write(destination, in sample);
            return;
        }

        int peak = (int)((1L << (ValidBits - 1)) - 1);
        int pcm = (int)Math.Round(Math.Clamp((double)sample, -1, 1) * peak) << (Bits - ValidBits);
        if (Bits == 8)
        {
            destination[0] = (byte)(pcm + 128);
            return;
        }

        for (int i = 0; i < Bits / 8; i++)
            destination[i] = (byte)(pcm >> (i * 8));
    }
}
