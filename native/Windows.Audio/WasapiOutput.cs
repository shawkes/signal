using System.Diagnostics;
using System.Runtime.InteropServices;
using static Signal.Native.Windows.Audio.WasapiInterop;

namespace Signal.Native.Windows.Audio;

public sealed record AudioOutputDevice(string Id, string Name);

public enum AudioPeriodPreference { Minimum, Conservative, Default }

public readonly record struct AudioDiagnostics(
    long Underruns,
    long RenderPasses,
    long ClockFailures,
    double MaxRenderMilliseconds,
    bool MultimediaSchedulingEnabled,
    Exception? Failure);

/// <summary>
/// Renders stereo frames measured from stream start. The timestamp is the projected playout time of
/// the first frame in Stopwatch ticks. Fill the entire span without blocking or retaining it.
/// </summary>
public delegate void AudioRenderer(Span<float> interleavedSamples, long firstFrame, long firstFrameTimestamp);

public sealed class WasapiOutput : IDisposable
{
    private static readonly PropertyKey FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private const double HundredNsPerSecond = 10_000_000.0;
    private const int EventStallMilliseconds = 2000;

    private readonly object _lifecycle = new();
    private readonly string? _deviceId;
    private readonly AudioRenderer _render;
    private readonly AudioPeriodPreference _preference;
    private readonly Thread _worker;
    private readonly ManualResetEvent _stop = new(false);
    private readonly AutoResetEvent _start = new(false);
    private readonly AutoResetEvent _audioReady = new(false);
    private readonly ManualResetEventSlim _configured = new();
    private readonly ManualResetEventSlim _started = new();
    private long _underruns;
    private long _renderPasses;
    private long _clockFailures;
    private long _maxRenderTicks;
    private bool _multimediaScheduling;
    private Exception? _failure;
    private bool _disposed;

    public WasapiOutput(string? deviceId, AudioRenderer render, AudioPeriodPreference periodPreference = AudioPeriodPreference.Minimum)
    {
        _deviceId = deviceId;
        _render = render ?? throw new ArgumentNullException(nameof(render));
        _preference = periodPreference;
        _worker = new Thread(Run) { IsBackground = true, Name = "Signal WASAPI render" };
        _worker.Start();
        _configured.Wait();
        if (_failure is { } error)
        {
            Dispose();
            throw new InvalidOperationException($"Unable to open WASAPI output: {error.Message}", error);
        }
    }

    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public int PeriodFrames { get; private set; }
    public int BufferFrames { get; private set; }
    public int MinimumPeriodFrames { get; private set; }
    public int DefaultPeriodFrames { get; private set; }
    public string Mode { get; private set; } = string.Empty;

    public AudioDiagnostics Diagnostics => new(
        Volatile.Read(ref _underruns), Volatile.Read(ref _renderPasses), Volatile.Read(ref _clockFailures),
        Volatile.Read(ref _maxRenderTicks) * 1000.0 / Stopwatch.Frequency,
        Volatile.Read(ref _multimediaScheduling), Volatile.Read(ref _failure));

    public static IReadOnlyList<AudioOutputDevice> EnumerateDevices()
    {
        bool apartment = EnterApartment();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? devices = null;
        try
        {
            enumerator = CreateEnumerator();
            Check(enumerator.EnumAudioEndpoints(Render, Active, out nint devicesPointer));
            devices = Adopt<IMMDeviceCollection>(devicesPointer);
            Check(devices.GetCount(out uint count));
            var result = new List<AudioOutputDevice>((int)count);
            for (uint index = 0; index < count; index++)
            {
                Check(devices.Item(index, out nint devicePointer));
                var device = Adopt<IMMDevice>(devicePointer);
                try
                {
                    Check(device.GetId(out nint idPointer));
                    string id = Marshal.PtrToStringUni(idPointer)!;
                    Marshal.FreeCoTaskMem(idPointer);
                    result.Add(new AudioOutputDevice(id, GetName(device) ?? id));
                }
                finally
                {
                    Release(device);
                }
            }

            return result;
        }
        finally
        {
            Release(devices);
            Release(enumerator);
            if (apartment)
                CoUninitialize();
        }
    }

    public void Start()
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _start.Set();
            _started.Wait();
            if (Volatile.Read(ref _failure) is { } error)
                throw new InvalidOperationException($"Unable to start WASAPI output: {error.Message}", error);
        }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed)
                return;
            _disposed = true;
            _stop.Set();
            _worker.Join();
            _started.Dispose();
            _configured.Dispose();
            _audioReady.Dispose();
            _start.Dispose();
            _stop.Dispose();
        }
    }

    private static string? GetName(IMMDevice device)
    {
        if (device.OpenPropertyStore(0, out nint pointer) < 0)
            return null;

        var store = Adopt<IPropertyStore>(pointer);
        try
        {
            int result = store.GetValue(in FriendlyName, out PropertyVariant value);
            try
            {
                return result >= 0 && value.Type == 31 ? Marshal.PtrToStringUni(value.Pointer) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(store);
        }
    }

    private void Run()
    {
        bool apartment = false;
        IMMDevice? endpoint = null;
        IAudioClient? client = null;
        IAudioRenderClient? renderClient = null;
        IAudioClock? clock = null;
        nint mmcss = 0;
        bool playing = false;
        try
        {
            apartment = EnterApartment();
            endpoint = OpenEndpoint();
            client = CreateInitializedClient(endpoint, out EndpointFormat format);

            Check(client.GetBufferSize(out uint bufferFrames));
            BufferFrames = checked((int)bufferFrames);
            Check(client.SetEventHandle(_audioReady.SafeWaitHandle.DangerousGetHandle()));
            renderClient = GetService<IAudioRenderClient>(client);
            clock = GetService<IAudioClock>(client);
            Check(clock.GetFrequency(out ulong clockFrequency));
            if (clockFrequency == 0)
                throw new InvalidOperationException("The WASAPI endpoint returned an invalid audio clock frequency.");
            var scratch = format.IsStereoFloat ? Array.Empty<float>() : new float[BufferFrames * 2];
            WaitHandle[] renderSignals = [_stop, _audioReady];

            // Warm up COM calls and prefill silence so playback starts with a valid buffer.
            Check(client.GetCurrentPadding(out _));
            Check(renderClient.GetBuffer(bufferFrames, out _));
            Check(renderClient.ReleaseBuffer(bufferFrames, Silent));
            Check(clock.GetPosition(out _, out _));
            _configured.Set();

            if (WaitHandle.WaitAny([_stop, _start]) == 0)
                return;

            uint task = 0;
            mmcss = AvSetMmThreadCharacteristics("Pro Audio", ref task);
            Volatile.Write(ref _multimediaScheduling, mmcss != 0);
            Check(client.Start());
            playing = true;
            _started.Set();
            RenderStream(client, renderClient, clock, clockFrequency, format, scratch, renderSignals);
        }
        catch (Exception error)
        {
            Volatile.Write(ref _failure, error);
        }
        finally
        {
            _configured.Set();
            _started.Set();
            if (playing)
                client!.Stop();
            if (mmcss != 0)
                AvRevertMmThreadCharacteristics(mmcss);
            Release(clock);
            Release(renderClient);
            Release(client);
            Release(endpoint);
            if (apartment)
                CoUninitialize();
        }
    }

    private IMMDevice OpenEndpoint()
    {
        var enumerator = CreateEnumerator();
        try
        {
            nint pointer;
            int result = _deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(Render, Multimedia, out pointer)
                : enumerator.GetDevice(_deviceId, out pointer);
            Check(result);
            return Adopt<IMMDevice>(pointer);
        }
        finally
        {
            Release(enumerator);
        }
    }

    private static IAudioClient CreateClient(IMMDevice endpoint)
    {
        Guid iid = typeof(IAudioClient).GUID;
        Check(endpoint.Activate(in iid, InProcess, 0, out nint pointer));
        return Adopt<IAudioClient>(pointer);
    }

    private IAudioClient CreateInitializedClient(IMMDevice endpoint, out EndpointFormat format)
    {
        IAudioClient client = CreateClient(endpoint);
        nint pointer = 0;
        try
        {
            Check(client.GetMixFormat(out pointer));
            format = EndpointFormat.Read(pointer);
            SampleRate = format.SampleRate;
            Channels = format.Channels;
            Initialize(endpoint, ref client, pointer);
            return client;
        }
        catch
        {
            Release(client);
            throw;
        }
        finally
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }

    private static T GetService<T>(IAudioClient client) where T : class
    {
        Guid id = typeof(T).GUID;
        Check(client.GetService(in id, out nint service));
        return Adopt<T>(service);
    }

    private void Initialize(IMMDevice endpoint, ref IAudioClient client, nint format)
    {
        if (client is IAudioClient3 lowLatency &&
            lowLatency.GetSharedModeEnginePeriod(format, out uint defaultPeriod, out uint step, out uint minimum, out uint maximum) >= 0 &&
            step > 0 && minimum > 0 && defaultPeriod >= minimum && maximum >= defaultPeriod)
        {
            MinimumPeriodFrames = (int)minimum;
            DefaultPeriodFrames = (int)defaultPeriod;
            uint conservative = Math.Clamp((2 * minimum + step - 1) / step * step, minimum, maximum);
            uint requested = _preference switch
            {
                AudioPeriodPreference.Conservative => conservative,
                AudioPeriodPreference.Default => defaultPeriod,
                _ => minimum,
            };
            int initialized = lowLatency.InitializeSharedAudioStream(EventCallback, requested, format, 0);
            if (initialized >= 0)
            {
                ConfigurePeriod((int)requested, "IAudioClient3 shared");
                return;
            }

            if (initialized == EnginePeriodLocked &&
                TryCurrentPeriod(lowLatency, out uint current) && current != requested &&
                TryRetryPeriod(endpoint, ref client, current, format))
            {
                ConfigurePeriod((int)current, "IAudioClient3 shared (active engine period)");
                return;
            }

            if (requested < conservative && conservative < defaultPeriod &&
                TryRetryPeriod(endpoint, ref client, conservative, format))
            {
                ConfigurePeriod((int)conservative, "IAudioClient3 shared (larger period)");
                return;
            }

            if (requested != defaultPeriod && TryRetryPeriod(endpoint, ref client, defaultPeriod, format))
            {
                ConfigurePeriod((int)defaultPeriod, "IAudioClient3 shared (default period)");
                return;
            }

            ReplaceClient(endpoint, ref client);
        }

        Check(client.GetDevicePeriod(out long defaultIn100Ns, out _));
        if (DefaultPeriodFrames == 0)
            DefaultPeriodFrames = (int)Math.Round(defaultIn100Ns * SampleRate / HundredNsPerSecond);
        Check(client.Initialize(Shared, EventCallback, 0, 0, format, 0));
        int actualPeriod = client is IAudioClient3 legacyWithPeriod && TryCurrentPeriod(legacyWithPeriod, out uint currentPeriod)
            ? (int)currentPeriod
            : DefaultPeriodFrames;
        ConfigurePeriod(actualPeriod, "IAudioClient shared (compatibility)");
    }

    private static bool TryRetryPeriod(IMMDevice endpoint, ref IAudioClient client, uint period, nint format)
    {
        ReplaceClient(endpoint, ref client);
        return client is IAudioClient3 audioClient &&
            audioClient.InitializeSharedAudioStream(EventCallback, period, format, 0) >= 0;
    }

    private static void ReplaceClient(IMMDevice endpoint, ref IAudioClient client)
    {
        // After an initialization failure, some clients refuse further Initialize calls.
        IAudioClient replacement = CreateClient(endpoint);
        Release(client);
        client = replacement;
    }

    private static bool TryCurrentPeriod(IAudioClient3 client, out uint period)
    {
        int result = client.GetCurrentSharedModeEnginePeriod(out nint format, out period);
        Marshal.FreeCoTaskMem(format);
        return result >= 0 && period > 0;
    }

    private void ConfigurePeriod(int period, string mode)
    {
        PeriodFrames = period;
        Mode = mode;
    }

    private unsafe void RenderStream(IAudioClient client, IAudioRenderClient renderClient, IAudioClock clock, ulong clockFrequency,
        EndpointFormat format, float[] scratch, WaitHandle[] signals)
    {
        long nextFrame = BufferFrames;
        long lastAnchorFrame = 0;
        long lastAnchorTimestamp = 0;
        long previousWriteCompleted = 0;
        double ticksPerFrame = (double)Stopwatch.Frequency / SampleRate;
        double framesPerClockUnit = (double)SampleRate / clockFrequency;
        double ticksPerHundredNs = Stopwatch.Frequency / HundredNsPerSecond;
        double queueDeadlineTicks = (BufferFrames + PeriodFrames * 0.5) * ticksPerFrame;

        for (;;)
        {
            int signal = WaitHandle.WaitAny(signals, EventStallMilliseconds);
            if (signal == 0)
                return;
            if (signal == WaitHandle.WaitTimeout)
            {
                Check(client.GetCurrentPadding(out _));
                throw new TimeoutException("The WASAPI endpoint stopped signaling its render event.");
            }

            Check(client.GetCurrentPadding(out uint padding));
            uint frames = (uint)BufferFrames - padding;
            if (frames == 0)
                continue;

            bool estimatedUnderrun = false;
            int clockStatus = clock.GetPosition(out ulong position, out ulong qpcInHundredNs);
            if (clockStatus == 0 && qpcInHundredNs > 0)
            {
                double playbackFrame = position * framesPerClockUnit;
                // Zero padding alone is normal for one-period buffers. Count a starvation only when
                // the stream clock has advanced materially beyond every submitted sample.
                if (padding == 0 && playbackFrame > nextFrame + PeriodFrames * 0.5)
                {
                    estimatedUnderrun = true;
                    nextFrame = (long)Math.Ceiling(playbackFrame);
                }

                lastAnchorFrame = nextFrame;
                lastAnchorTimestamp = (long)Math.Round(qpcInHundredNs * ticksPerHundredNs + (nextFrame - playbackFrame) * ticksPerFrame);
            }
            else
            {
                Check(clockStatus);
                Interlocked.Increment(ref _clockFailures);
                if (lastAnchorTimestamp == 0)
                {
                    lastAnchorFrame = nextFrame;
                    lastAnchorTimestamp = Stopwatch.GetTimestamp() + (long)Math.Round(padding * ticksPerFrame);
                }
            }

            long timestamp = lastAnchorTimestamp + (long)Math.Round((nextFrame - lastAnchorFrame) * ticksPerFrame);
            long renderStarted = Stopwatch.GetTimestamp();
            Check(renderClient.GetBuffer(frames, out nint data));
            uint flags = Silent;
            try
            {
                int length = (int)frames * 2;
                Span<float> stereo = format.IsStereoFloat ? new Span<float>((void*)data, length) : scratch.AsSpan(0, length);
                _render(stereo, nextFrame, timestamp);
                if (!format.IsStereoFloat)
                    format.Write(stereo, data);
                flags = 0;
            }
            finally
            {
                Check(renderClient.ReleaseBuffer(frames, flags));
            }

            long writeCompleted = Stopwatch.GetTimestamp();
            // Some devices pause the stream clock during starvation. A gap between writes longer
            // than the whole queue, plus half a period for wakeup jitter, also estimates a miss.
            if (estimatedUnderrun || (previousWriteCompleted != 0 && writeCompleted - previousWriteCompleted > queueDeadlineTicks))
                Interlocked.Increment(ref _underruns);
            previousWriteCompleted = writeCompleted;

            long elapsed = writeCompleted - renderStarted;
            if (elapsed > _maxRenderTicks)
                Volatile.Write(ref _maxRenderTicks, elapsed);
            Interlocked.Increment(ref _renderPasses);
            nextFrame += frames;
        }
    }
}
