namespace Signal.Native.Engine;

internal sealed class OutputCompressor(int sampleRate)
{
    private const float Threshold = 0.12589254f; // -18 dBFS
    private const float Ratio = 3f;
    private const float MakeupGain = 1.9952623f; // +6 dB
    private readonly float _attack = MathF.Exp(-1f / (0.004f * sampleRate));
    private readonly float _release = MathF.Exp(-1f / (0.075f * sampleRate));
    private readonly float _blendStep = 1f / (0.010f * sampleRate);
    private float _compressionGain = 1f;
    private float _blend;

    public void Apply(Span<float> stereo, bool enabled)
    {
        if (!enabled && _blend == 0f)
            return;

        // Crossfade the bypass so toggling during playback does not jump the output level.
        float step = enabled ? _blendStep : -_blendStep;
        for (int index = 0; index < stereo.Length; index += 2)
        {
            float peak = Math.Max(Math.Abs(stereo[index]), Math.Abs(stereo[index + 1]));
            float target = peak > Threshold ? MathF.Pow(Threshold / peak, 1f - 1f / Ratio) : 1f;
            float smoothing = target < _compressionGain ? _attack : _release;
            _compressionGain = target + (_compressionGain - target) * smoothing;
            _blend = Math.Clamp(_blend + step, 0f, 1f);
            float gain = 1f + (MakeupGain * _compressionGain - 1f) * _blend;
            stereo[index] *= gain;
            stereo[index + 1] *= gain;
        }

        if (_blend == 0f)
            _compressionGain = 1f;
    }
}
