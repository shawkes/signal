using System.Diagnostics;
using System.Globalization;
using PortAudioSharp;
using AudioStream = PortAudioSharp.Stream;

namespace Signal.Native.Desktop.Devices;

public enum AudioPeriodPreference { Minimum, Conservative, Default }
public sealed record AudioOutputDevice(string Id, string Name);
public readonly record struct AudioDiagnostics(long Underruns, long RenderPasses, long ClockFailures, Exception? Failure);
public delegate void AudioRenderer(Span<float> stereo, long firstFrame, long timestamp);

public sealed class PortAudioOutput : IDisposable
{
    private readonly AudioStream _stream;
    private readonly AudioRenderer _render;
    private long _frames;
    private long _underruns;
    private long _passes;
    private long _clockFailures;
    private Exception? _failure;
    private bool _disposed;

    public PortAudioOutput(string? deviceId, AudioRenderer render, AudioPeriodPreference preference)
    {
        _render = render;
        PortAudio.Initialize();
        try
        {
            int device = deviceId is null ? PortAudio.DefaultOutputDevice : int.Parse(deviceId, CultureInfo.InvariantCulture);
            if (device < 0 || device >= PortAudio.DeviceCount)
                throw new InvalidOperationException("No audio output device is available.");

            var info = PortAudio.GetDeviceInfo(device);
            if (info.maxOutputChannels < Channels)
                throw new InvalidOperationException("The audio output needs two channels.");
            SampleRate = (int)Math.Round(info.defaultSampleRate);
            double latency = preference == AudioPeriodPreference.Default
                ? info.defaultHighOutputLatency : info.defaultLowOutputLatency;
            PeriodFrames = preference switch
            {
                AudioPeriodPreference.Minimum => 128,
                AudioPeriodPreference.Conservative => 256,
                _ => 512
            };
            BufferFrames = Math.Max(PeriodFrames, (int)Math.Round(latency * SampleRate));
            var parameters = new StreamParameters
            {
                device = device,
                channelCount = Channels,
                sampleFormat = SampleFormat.Float32,
                suggestedLatency = Math.Max(latency, PeriodFrames / (double)SampleRate)
            };
            _stream = new AudioStream(null, parameters, SampleRate, (uint)PeriodFrames, StreamFlags.NoFlag, Render, this);
        }
        catch
        {
            PortAudio.Terminate();
            throw;
        }
    }

    public int SampleRate { get; }
    public int Channels => 2;
    public int PeriodFrames { get; }
    public int BufferFrames { get; }
    public string Mode => "PortAudio";
    public AudioDiagnostics Diagnostics => new(Volatile.Read(ref _underruns), Volatile.Read(ref _passes),
        Volatile.Read(ref _clockFailures), Volatile.Read(ref _failure));

    public static IReadOnlyList<AudioOutputDevice> EnumerateDevices()
    {
        try
        {
            PortAudio.Initialize();
            try
            {
                var devices = new List<AudioOutputDevice>();
                for (int index = 0; index < PortAudio.DeviceCount; index++)
                {
                    var device = PortAudio.GetDeviceInfo(index);
                    if (device.maxOutputChannels >= 2)
                        devices.Add(new(index.ToString(CultureInfo.InvariantCulture), device.name));
                }
                return devices;
            }
            finally
            {
                PortAudio.Terminate();
            }
        }
        catch (Exception error) when (error is PortAudioException or DllNotFoundException or TypeInitializationException)
        {
            throw new InvalidOperationException(error.Message, error);
        }
    }

    public void Start() => _stream.Start();

    private unsafe StreamCallbackResult Render(nint input, nint output, uint frameCount,
        ref StreamCallbackTimeInfo time, StreamCallbackFlags flags, nint userData)
    {
        long now = Stopwatch.GetTimestamp();
        var stereo = new Span<float>((void*)output, checked((int)frameCount * Channels));
        if ((flags & StreamCallbackFlags.OutputUnderflow) != 0)
            Interlocked.Increment(ref _underruns);
        double untilPlayout = time.outputBufferDacTime - time.currentTime;
        if (!double.IsFinite(untilPlayout) || time.outputBufferDacTime == 0)
        {
            Interlocked.Increment(ref _clockFailures);
            untilPlayout = BufferFrames / (double)SampleRate;
        }

        try
        {
            long outputTimestamp = now + (long)Math.Round(untilPlayout * Stopwatch.Frequency);
            _render(stereo, _frames, outputTimestamp);
            _frames += frameCount;
            Interlocked.Increment(ref _passes);
            return StreamCallbackResult.Continue;
        }
        catch (Exception error)
        {
            stereo.Clear();
            Volatile.Write(ref _failure, error);
            return StreamCallbackResult.Abort;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _stream.Dispose();
        }
        finally
        {
            PortAudio.Terminate();
        }
    }
}
