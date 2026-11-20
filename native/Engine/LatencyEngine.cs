using System.Diagnostics;

namespace Signal.Native.Engine;

/// <summary>Audio-thread renderer with timestamped MIDI input from the device adapter.</summary>
public sealed class LatencyEngine
{
    private const int EventCapacity = 2048;
    private const int RecordingCommandReserve = 32;
    private readonly object _producerLock = new();
    private readonly SingleConsumerRing<RenderEvent> _incoming = new(EventCapacity);
    private readonly PriorityQueue<RenderEvent, (long Timestamp, long Sequence)> _pending = new(EventCapacity);
    private readonly AudioSampleClock _clock;
    private readonly Recording _recording;
    private readonly NotePlayback _playback;
    private readonly float[] _accent;
    private readonly float[] _beat;
    private IMonitorSynth _synth;
    private int _queuedEvents;
    private long _sequence;
    private long _takeId;
    private long _completedTakeId;
    private long _droppedMidiMessages;
    private long _midiResetTimestamp;
    private int _midiGeneration;
    private int _audioMidiGeneration;
    private volatile bool _metronomeEnabled;

    public LatencyEngine(int sampleRate, IMonitorSynth synth, double bpm = 120, long? timestampFrequency = null)
        : this(sampleRate, synth, TempoMap.Constant(bpm), timestampFrequency)
    {
    }

    public LatencyEngine(int sampleRate, IMonitorSynth synth, TempoMap tempoMap, long? timestampFrequency = null)
    {
        ArgumentNullException.ThrowIfNull(synth);
        ArgumentNullException.ThrowIfNull(tempoMap);
        _clock = new AudioSampleClock(sampleRate, timestampFrequency ?? Stopwatch.Frequency);
        SampleRate = sampleRate;
        TempoMap = tempoMap;
        _synth = synth;
        _recording = new Recording(tempoMap, sampleRate);
        _playback = new NotePlayback(tempoMap, sampleRate);
        _accent = BuildClick(sampleRate, 1600, 0.25f);
        _beat = BuildClick(sampleRate, 1100, 0.16f);
    }

    public int SampleRate { get; }
    public TempoMap TempoMap { get; }

    public bool MetronomeEnabled
    {
        get => _metronomeEnabled;
        set => _metronomeEnabled = value;
    }

    public long DroppedMidiMessages => Volatile.Read(ref _droppedMidiMessages);
    public long DroppedRecordedNotes => _recording.DroppedNotes;

    /// <summary>The most recent stopped take, published after its held notes are closed.</summary>
    public long CompletedTakeId => Volatile.Read(ref _completedTakeId);

    /// <summary>The rendered song position, zero while starting and null after the last note-off or stop.</summary>
    public long? PlaybackTick => _playback.Tick;

    /// <summary>Starts an edited snapshot from tick zero using a separate synth owned by playback.</summary>
    public bool PlayNotes(IEnumerable<SongNote> notes, IMonitorSynth playbackSynth) => _playback.Start(notes, playbackSynth);

    public void StopPlayback() => _playback.Stop();

    /// <summary>Publishes a prepared synth for the next render block.</summary>
    public void SetSynth(IMonitorSynth synth)
    {
        ArgumentNullException.ThrowIfNull(synth);
        Volatile.Write(ref _synth, synth);
    }

    public bool TryEnqueue(MidiMessage message)
    {
        if (message.Status is < 0x80 or > 0xef || message.Data1 > 127 || message.Data2 > 127)
        {
            return false;
        }

        lock (_producerLock)
        {
            if (Volatile.Read(ref _queuedEvents) < EventCapacity - RecordingCommandReserve)
            {
                Enqueue(new RenderEvent(message.Timestamp, message, _midiGeneration));
                return true;
            }

            Interlocked.Increment(ref _droppedMidiMessages);
            ResetMidiGeneration();
            return false;
        }
    }

    /// <summary>Releases held notes and drops input queued before a device reset.</summary>
    public void RequestMidiReset()
    {
        lock (_producerLock)
        {
            ResetMidiGeneration();
        }
    }

    /// <summary>Starts or stops a take. A division of zero records without quantization.</summary>
    public long SetRecording(bool enabled, double offsetMilliseconds, int quantizationDivision = 0, long? timestamp = null)
    {
        var offsetFrames = checked((long)Math.Round(offsetMilliseconds * SampleRate / 1000, MidpointRounding.AwayFromZero));
        if (quantizationDivision < 0 || quantizationDivision > 4L * TempoMap.TicksPerQuarter)
        {
            throw new ArgumentOutOfRangeException(nameof(quantizationDivision));
        }

        lock (_producerLock)
        {
            if (Volatile.Read(ref _queuedEvents) >= EventCapacity)
            {
                throw new InvalidOperationException("The audio event queue is full.");
            }

            if (enabled)
            {
                _takeId++;
            }

            Enqueue(new RenderEvent(timestamp ?? Stopwatch.GetTimestamp(), TakeId: _takeId,
                OffsetFrames: offsetFrames, QuantizationDivision: quantizationDivision,
                Kind: enabled ? EventKind.StartRecording : EventKind.StopRecording));
            return _takeId;
        }
    }

    public bool TryReadNote(out RecordedNote note) => _recording.TryReadNote(out note);

    /// <summary>Renders stereo frames using the presentation coordinates supplied by the audio adapter.</summary>
    public void Render(Span<float> interleavedStereo, long firstFrame, AudioClockAnchor clock)
    {
        if ((interleavedStereo.Length & 1) != 0)
        {
            throw new ArgumentException("Stereo render buffers must contain complete frames.", nameof(interleavedStereo));
        }

        var synth = Volatile.Read(ref _synth);
        while (_incoming.TryRead(out var incoming))
        {
            _pending.Enqueue(incoming, (incoming.Timestamp, incoming.Sequence));
        }

        var midiGeneration = Volatile.Read(ref _midiGeneration);
        if (midiGeneration != _audioMidiGeneration)
        {
            for (var channel = 0; channel < 16; channel++)
            {
                synth.ProcessMidi((byte)(0xb0 + channel), 120, 0);
                synth.ProcessMidi((byte)(0xb0 + channel), 121, 0);
            }

            _recording.FinishAll(_clock.FrameAtTimestamp(Volatile.Read(ref _midiResetTimestamp), clock));
            _audioMidiGeneration = midiGeneration;
        }

        var endFrame = firstFrame + interleavedStereo.Length / 2;
        var currentFrame = firstFrame;
        while (_pending.TryPeek(out var next, out _))
        {
            var stale = next.Kind == EventKind.Midi && next.MidiGeneration != _audioMidiGeneration;
            var receivedFrame = _clock.FrameAtTimestamp(next.Timestamp, clock);
            if (!stale && receivedFrame >= endFrame)
            {
                break;
            }

            _pending.Dequeue();
            Interlocked.Decrement(ref _queuedEvents);
            if (stale)
            {
                continue;
            }

            var eventFrame = Math.Max(receivedFrame, currentFrame);
            if (eventFrame > currentFrame)
            {
                synth.Render(interleavedStereo[(int)((currentFrame - firstFrame) * 2)..(int)((eventFrame - firstFrame) * 2)]);
                currentFrame = eventFrame;
            }

            ProcessEvent(next, receivedFrame, synth);
        }

        if (currentFrame < endFrame)
        {
            synth.Render(interleavedStereo[(int)((currentFrame - firstFrame) * 2)..]);
        }

        long clickOrigin = _playback.Render(interleavedStereo, firstFrame) ?? 0;
        if (MetronomeEnabled)
        {
            MixClicks(interleavedStereo, firstFrame - clickOrigin, endFrame - clickOrigin);
        }
    }

    private void Enqueue(RenderEvent input)
    {
        Interlocked.Increment(ref _queuedEvents);
        if (!_incoming.TryWrite(input with { Sequence = _sequence++ }))
        {
            Interlocked.Decrement(ref _queuedEvents);
            throw new InvalidOperationException("The audio event queue is full.");
        }
    }

    private void ResetMidiGeneration()
    {
        Volatile.Write(ref _midiResetTimestamp, Stopwatch.GetTimestamp());
        Volatile.Write(ref _midiGeneration, unchecked(_midiGeneration + 1));
    }

    private void ProcessEvent(RenderEvent next, long receivedFrame, IMonitorSynth synth)
    {
        switch (next.Kind)
        {
            case EventKind.Midi:
                var midi = next.Midi;
                synth.ProcessMidi(midi.Status, midi.Data1, midi.Data2);
                _recording.ProcessMidi(midi, receivedFrame);
                break;
            case EventKind.StartRecording:
                _recording.Start(receivedFrame, next.TakeId, next.OffsetFrames, next.QuantizationDivision);
                break;
            case EventKind.StopRecording:
                _recording.Stop(receivedFrame);
                Volatile.Write(ref _completedTakeId, next.TakeId);
                break;
        }
    }

    private void MixClicks(Span<float> output, long firstFrame, long endFrame)
    {
        var earliest = TempoMap.SampleToTick(firstFrame - _beat.Length + 1, SampleRate);
        var beat = Math.Max(0, (long)Math.Floor(earliest / TempoMap.TicksPerQuarter));
        var clickFrame = BeatFrame(beat);
        while (clickFrame < endFrame)
        {
            var click = beat % 4 == 0 ? _accent : _beat;
            var start = Math.Max(clickFrame, firstFrame);
            var end = Math.Min(clickFrame + click.Length, endFrame);
            for (var frame = start; frame < end; frame++)
            {
                var sample = (int)((frame - firstFrame) * 2);
                var value = click[(int)(frame - clickFrame)];
                output[sample] = Math.Clamp(output[sample] + value, -1, 1);
                output[sample + 1] = Math.Clamp(output[sample + 1] + value, -1, 1);
            }

            clickFrame = BeatFrame(++beat);
        }
    }

    private long BeatFrame(long beat) => TempoMap.TickToSample((double)beat * TempoMap.TicksPerQuarter, SampleRate);

    private static float[] BuildClick(int sampleRate, double frequency, float gain)
    {
        var click = new float[Math.Max(1, sampleRate / 50)];
        for (var i = 0; i < click.Length; i++)
        {
            var fade = 1 - i / (double)click.Length;
            click[i] = gain * (float)(Math.Cos(2 * Math.PI * frequency * i / sampleRate) * fade * fade * fade * fade);
        }

        return click;
    }

    private enum EventKind { Midi, StartRecording, StopRecording }

    private readonly record struct RenderEvent(
        long Timestamp,
        MidiMessage Midi = default,
        int MidiGeneration = 0,
        long Sequence = 0,
        long TakeId = 0,
        long OffsetFrames = 0,
        int QuantizationDivision = 0,
        EventKind Kind = EventKind.Midi);
}
