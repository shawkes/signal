namespace Signal.Native.Engine;

internal sealed class OutputLimiter(int sampleRate)
{
    private const float Ceiling = 0.7943282f; // -2 dBFS sample peaks leave room for intersample peaks.
    private readonly float _gainSmoothing = MathF.Exp(-1f / (0.005f * sampleRate));
    private readonly float _release = MathF.Exp(-1f / (0.025f * sampleRate));
    private float _gain = 1f;
    private float _limiterGain = 1f;

    public void Apply(Span<float> stereo, int gainDb)
    {
        float target = MathF.Pow(10f, gainDb / 20f);
        for (int i = 0; i < stereo.Length; i += 2)
        {
            _gain = target + (_gain - target) * _gainSmoothing;
            float left = stereo[i] * _gain;
            float right = stereo[i + 1] * _gain;
            float peak = Math.Max(Math.Abs(left), Math.Abs(right));
            // Share gain across both channels and limit peaks immediately without delaying live monitoring.
            float required = peak > Ceiling ? Ceiling / peak : 1f;
            _limiterGain = Math.Min(required, 1f - (1f - _limiterGain) * _release);
            stereo[i] = left * _limiterGain;
            stereo[i + 1] = right * _limiterGain;
        }
    }
}
