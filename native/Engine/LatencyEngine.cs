using System.Diagnostics;

namespace Signal.Native.Engine;

public readonly record struct RecordingDisplayPosition(long? Tick, int? CountInBeatsRemaining, long? LoopPass, int HeldNoteCount);

/// <summary>Audio-thread renderer with timestamped MIDI input from the device adapter.</summary>
public sealed class LatencyEngine
{
    public const int MaxHeldRecordingSegments = HeldRecordingNotes.KeyCount * 2;
    private const int EventCapacity = 2048;
    private const int RecordingCommandReserve = 32;
    private readonly object _producerLock = new();
    private readonly SingleConsumerRing<RenderEvent> _incoming = new(EventCapacity);
    private readonly PriorityQueue<RenderEvent, (long Timestamp, long Sequence)> _pending = new(EventCapacity);
    private readonly AudioSampleClock _clock;
    private readonly Recording _recording;
    private readonly NotePlayback _playback;
    private readonly MetronomeMixer _mixMetronome;
    private readonly MeterMap _defaultMeter;
    private readonly float[] _accent;
    private readonly float[] _beat;
    private IMonitorSynth _synth;
    private LiveMidiDestination?[]? _liveMidiDestinations;
    private SongTake? _songTake;
    private SongTake? _activeSongTake;
    private long? _testMetronomeStartFrame;
    private OpeningTimingBuffer? _lastOpeningTiming;
    private int _queuedEvents;
    private long _sequence;
    private long _takeId;
    private long _completedTakeId;
    private long _droppedMidiMessages;
    private long _midiResetTimestamp;
    private int _midiGeneration;
    private int _audioMidiGeneration;
    private volatile bool _metronomeEnabled;
    private volatile bool _previewMetronomeEnabled;

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
        _defaultMeter = new MeterMap(tempoMap.TicksPerQuarter, []);
        _playback = new NotePlayback(sampleRate, _defaultMeter);
        _mixMetronome = MixClicks;
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

    /// <summary>Allows a free-running click for recording tests while the song transport is idle.</summary>
    public bool PreviewMetronomeEnabled
    {
        get => _previewMetronomeEnabled;
        set => _previewMetronomeEnabled = value;
    }

    public long DroppedMidiMessages => Volatile.Read(ref _droppedMidiMessages);
    public long DroppedRecordedNotes => _recording.DroppedNotes;

    /// <summary>The most recent stopped take, published after its held notes are closed.</summary>
    public long CompletedTakeId => Volatile.Read(ref _completedTakeId);

    /// <summary>The rendered song position. Null during count-in or when transport is stopped.</summary>
    public long? PlaybackTick
    {
        get
        {
            var take = Volatile.Read(ref _songTake);
            if (take is null)
                return _playback.Tick;

            return Volatile.Read(ref take.BeatsRemaining) == 0 ? Volatile.Read(ref take.PlaybackTick) : null;
        }
    }

    /// <summary>The beats remaining in the count-in, or null outside count-in.</summary>
    public int? CountInBeatsRemaining
    {
        get
        {
            var take = Volatile.Read(ref _songTake);
            int remaining = take is null ? 0 : Volatile.Read(ref take.BeatsRemaining);
            return remaining > 0 ? remaining : null;
        }
    }

    /// <summary>The one-based pass of a loop take, or null outside active overdub playback.</summary>
    public long? RecordingLoopPass
    {
        get
        {
            var take = Volatile.Read(ref _songTake);
            if (take is null || take.Loop is null || Volatile.Read(ref take.BeatsRemaining) != 0)
                return null;

            return Volatile.Read(ref take.LoopPass);
        }
    }

    /// <summary>Starts an edited snapshot from tick zero using a separate synth owned by playback.</summary>
    public bool PlayNotes(IEnumerable<SongNote> notes, IMonitorSynth playbackSynth) =>
        Volatile.Read(ref _songTake) is null &&
        RequestPlayback(_playback.Prepare(notes, [], TempoMap, playbackSynth));

    /// <summary>Plays all tracks together on their own tempo map, without changing the recording clock.</summary>
    public bool PlaySong(Song song, IMonitorSynth playbackSynth, LoopRange? loop = null, long startTick = 0)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (Volatile.Read(ref _songTake) is not null)
            return false;

        return RequestPlayback(_playback.Prepare(
            song.Tracks.SelectMany(track => track.Notes),
            song.Tracks.SelectMany(track => track.PlaybackMessages),
            song.TempoMap, playbackSynth, SongEndTick(song), loop, song.MeterMap, startTick: startTick));
    }

    /// <summary>Records from bar one or the selected loop, optionally counting in once.</summary>
    public long RecordSong(Song song, IMonitorSynth playbackSynth, bool countIn,
        double offsetMilliseconds, int quantizationDivision = 0, long? timestamp = null,
        LoopRange? loop = null, IMonitorSynth? overdubSynth = null, IReadOnlyList<SongChannelMessage>? overdubSetup = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        long offsetFrames = RecordingOffsetFrames(offsetMilliseconds, quantizationDivision);
        var backing = _playback.Prepare(
            song.Tracks.SelectMany(track => track.Notes),
            song.Tracks.SelectMany(track => track.PlaybackMessages),
            song.TempoMap, playbackSynth, SongEndTick(song), loop, song.MeterMap, allowEmpty: loop.HasValue);
        var preparedLoop = backing?.RequestedLoop;
        OverdubReplay? replay = null;
        if (backing is not null && preparedLoop is { } range && overdubSynth is not null)
        {
            replay = new OverdubReplay(song.TempoMap, SampleRate, range.StartFrame, range.EndFrame, overdubSynth,
                overdubSetup ?? []);
            backing.Overdub = replay;
        }
        long firstTick = loop?.StartTick ?? 0;
        var countInFrames = countIn ? CreateCountIn(song, firstTick) : [];
        var held = new HeldRecordingNotes(new(song.TempoMap, SampleRate, offsetFrames, quantizationDivision, countIn),
            preparedLoop is { } cycle ? new RecordingLoop(cycle.Range, cycle.StartFrame, cycle.EndFrame) : null);
        long lastTimingTick = song.MeterMap.NextBarTick(song.MeterMap.NextBarTick(firstTick));
        double timingSeconds = song.TempoMap.TickToSeconds(lastTimingTick) - song.TempoMap.TickToSeconds(firstTick);
        var openingTiming = new OpeningTimingBuffer(held.Placement, long.CreateSaturating(timingSeconds * SampleRate),
            firstTick, countIn);

        lock (_producerLock)
        {
            if (Volatile.Read(ref _songTake) is not null)
                throw new InvalidOperationException("A song take is already active.");

            EnsureEventCapacity();
            long startTimestamp = timestamp ?? Stopwatch.GetTimestamp();
            openingTiming.TakeId = ++_takeId;
            var take = new SongTake(openingTiming.TakeId, startTimestamp, held, song.TempoMap, song.MeterMap,
                backing, preparedLoop, replay, countInFrames, openingTiming);
            Volatile.Write(ref _lastOpeningTiming, openingTiming);
            Volatile.Write(ref _songTake, take);
            Enqueue(new RenderEvent(startTimestamp, TakeId: take.Id, Kind: EventKind.StartSongTake));
            return take.Id;
        }
    }

    /// <summary>Finishes a song take, or cancels it if the countdown is still in progress.</summary>
    public void StopSongRecording(long? timestamp = null)
    {
        lock (_producerLock)
        {
            var take = Volatile.Read(ref _songTake);
            if (take is null || take.StopRequested)
                return;

            EnsureEventCapacity();
            long stopTimestamp = Math.Max(take.StartTimestamp, timestamp ?? Stopwatch.GetTimestamp());
            Enqueue(new RenderEvent(stopTimestamp, TakeId: take.Id, Kind: EventKind.StopSongTake));
            take.StopRequested = true;
        }
    }

    /// <summary>Updates the current pass's loop. Null lets playback continue to the song end.</summary>
    public void SetPlaybackLoop(LoopRange? loop)
    {
        if (Volatile.Read(ref _songTake) is null)
            _playback.SetLoop(loop);
    }

    public void StopPlayback()
    {
        lock (_producerLock)
        {
            if (Volatile.Read(ref _songTake) is null)
                _playback.Stop();
        }
    }

    /// <summary>Publishes a prepared synth for the next render block.</summary>
    public void SetSynth(IMonitorSynth synth)
    {
        ArgumentNullException.ThrowIfNull(synth);
        Volatile.Write(ref _synth, synth);
    }

    /// <summary>Changes live routing at the given timestamp and releases notes held on the old routing.</summary>
    public void ConfigureLiveMidi(ReadOnlySpan<LiveMidiDestination?> destinations, IMonitorSynth? synth = null,
        long? timestamp = null)
    {
        if (destinations.Length != 16)
            throw new ArgumentException("Specify a destination for each of the 16 MIDI input channels.", nameof(destinations));
        foreach (var destination in destinations)
        {
            if (destination is { Channel: >= 16 } or { TrackIndex: < 0 })
                throw new ArgumentOutOfRangeException(nameof(destinations));
        }
        var configuration = new LiveMidiConfiguration(destinations.ToArray(), synth);
        lock (_producerLock)
        {
            EnsureEventCapacity();
            Enqueue(new RenderEvent(timestamp ?? Stopwatch.GetTimestamp(), Kind: EventKind.LiveMidiConfiguration,
                LiveMidi: configuration));
        }
    }

    public bool TryEnqueue(MidiMessage message) => TryEnqueueMidi(message, EventKind.Midi);

    public bool TryEnqueueLiveMidi(MidiMessage message) => TryEnqueueMidi(message, EventKind.LiveMidi);

    private bool TryEnqueueMidi(MidiMessage message, EventKind kind)
    {
        if (message.Status is < 0x80 or > 0xef || message.Data1 > 127 || message.Data2 > 127)
        {
            return false;
        }

        lock (_producerLock)
        {
            if (Volatile.Read(ref _queuedEvents) < EventCapacity - RecordingCommandReserve)
            {
                Enqueue(new RenderEvent(message.Timestamp, message, _midiGeneration, Kind: kind));
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
    public long SetRecording(bool enabled, double offsetMilliseconds, int quantizationDivision = 0,
        long? timestamp = null, bool playTestMetronome = false)
    {
        long offsetFrames = RecordingOffsetFrames(offsetMilliseconds, quantizationDivision);

        lock (_producerLock)
        {
            if (Volatile.Read(ref _songTake) is not null)
                throw new InvalidOperationException("Stop the song take before using the recording test.");

            EnsureEventCapacity();
            if (enabled)
            {
                _takeId++;
            }

            Enqueue(new RenderEvent(timestamp ?? Stopwatch.GetTimestamp(), TakeId: _takeId,
                OffsetFrames: offsetFrames, QuantizationDivision: quantizationDivision,
                Kind: enabled ? EventKind.StartRecording : EventKind.StopRecording, PlayTestMetronome: playTestMetronome));
            return _takeId;
        }
    }

    public bool TryReadNote(out RecordedNote note) => _recording.TryReadNote(out note);

    public OpeningRecordingTiming? ReadOpeningRecordingTiming(long takeId)
    {
        var timing = Volatile.Read(ref _lastOpeningTiming);
        return timing?.TakeId == takeId && CompletedTakeId == takeId ? timing.Snapshot() : null;
    }

    internal bool HasUnreadRecordedNotes => _recording.HasUnreadNotes;

    /// <summary>Copies the currently held portions of a song take for display. The reader supplies storage.</summary>
    public int CopyHeldRecordingNotes(long takeId, Span<HeldRecordingNote> destination)
    {
        var take = Volatile.Read(ref _songTake);
        if (take is null || take.Id != takeId || Volatile.Read(ref take.BeatsRemaining) != 0)
            return 0;
        return take.Held.CopyTo(destination, Volatile.Read(ref take.ElapsedFrames));
    }

    /// <summary>Samples the song at the display timestamp on the endpoint presentation clock.</summary>
    public RecordingDisplayPosition ReadRecordingDisplay(long takeId, long timestamp, Span<HeldRecordingNote> destination)
    {
        var take = Volatile.Read(ref _songTake);
        if (take is null || take.Id != takeId)
            return default;

        var origin = Volatile.Read(ref take.PresentationOriginTimestamp);
        int beats = take.CountInOnsets.Length - 1;
        if (origin == long.MinValue)
            return new(null, beats > 0 ? beats : null, null, 0);

        // The endpoint can have multiple buffers queued. Interpolate only as far as the
        // last submitted block so a stalled render thread also stops the visual transport.
        long elapsed = Math.Min(_clock.FrameAtTimestamp(timestamp, new(origin, 0)),
            Volatile.Read(ref take.ElapsedFrames));
        if (elapsed < 0)
        {
            if (beats <= 0)
                return default;
            long countFrame = take.CountInOnsets[^1] + elapsed;
            int beat = beats - 1;
            while (beat > 0 && take.CountInOnsets[beat] > countFrame)
                beat--;
            return new(null, beats - beat, null, 0);
        }

        int held = take.Held.CopyTo(destination, elapsed);
        long? pass = take.Loop is { } loop ? elapsed / loop.Length + 1 : null;
        return new(take.TickAt(elapsed, SampleRate), null, pass, held);
    }

    /// <summary>Renders stereo frames using the presentation coordinates supplied by the audio adapter.
    /// Queued outputs supply their callback-entry timestamp to distinguish presented audio from writable audio.</summary>
    public void Render(Span<float> interleavedStereo, long firstFrame, AudioClockAnchor clock, long? callbackTimestamp = null)
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
            ReleaseLiveNotes(synth, _clock.FrameAtTimestamp(Volatile.Read(ref _midiResetTimestamp), clock));
            _audioMidiGeneration = midiGeneration;
        }

        var endFrame = firstFrame + interleavedStereo.Length / 2;
        var currentFrame = firstFrame;
        var mixedFrame = firstFrame;
        while (_pending.TryPeek(out var next, out _))
        {
            var stale = (next.Kind is EventKind.Midi or EventKind.LiveMidi) && next.MidiGeneration != _audioMidiGeneration;
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

            if ((next.Kind is EventKind.StartSongTake or EventKind.StopSongTake or
                EventKind.StartRecording or EventKind.StopRecording) && mixedFrame < eventFrame)
            {
                MixTransport(interleavedStereo[(int)((mixedFrame - firstFrame) * 2)..(int)((eventFrame - firstFrame) * 2)],
                    mixedFrame);
                mixedFrame = eventFrame;
            }

            ProcessEvent(next, receivedFrame, eventFrame, ref synth);
        }

        if (currentFrame < endFrame)
        {
            synth.Render(interleavedStereo[(int)((currentFrame - firstFrame) * 2)..]);
        }

        // Commands received by callback entry have been consumed. A count-in can still be
        // canceled until its downbeat is presented, even if that audio is already queued.
        if (_activeSongTake is { } opening &&
            (callbackTimestamp.HasValue ? _clock.FrameAtTimestamp(callbackTimestamp.Value, clock) : firstFrame) >= opening.SongStartFrame)
            _recording.ReachDownbeat();
        if (mixedFrame < endFrame)
            MixTransport(interleavedStereo[(int)((mixedFrame - firstFrame) * 2)..], mixedFrame);
        if (_activeSongTake is { } take)
        {
            Volatile.Write(ref take.ElapsedFrames, endFrame - take.SongStartFrame);
            // Publish one time origin, refreshed from each audio-clock observation. The UI
            // can sample between callbacks without running an independent transport clock.
            Volatile.Write(ref take.PresentationOriginTimestamp, _clock.TimestampAtFrame(take.SongStartFrame, clock));
        }
    }

    private long RecordingOffsetFrames(double offsetMilliseconds, int quantizationDivision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(quantizationDivision);

        return checked((long)Math.Round(offsetMilliseconds * SampleRate / 1000, MidpointRounding.AwayFromZero));
    }

    private void EnsureEventCapacity()
    {
        if (Volatile.Read(ref _queuedEvents) >= EventCapacity)
            throw new InvalidOperationException("The audio event queue is full.");
    }

    private static long SongEndTick(Song song) => Song.IncludeTimingMarkers(
        song.Tracks.Select(track => track.EndTick).DefaultIfEmpty().Max(), song.TempoMap, song.MeterMap);

    private bool RequestPlayback(NotePlayback.PlaybackPlan? plan)
    {
        if (plan is null)
            return false;

        lock (_producerLock)
        {
            if (Volatile.Read(ref _songTake) is not null)
                return false;

            _playback.Request(plan);
            return true;
        }
    }

    private long[] CreateCountIn(Song song, long startTick)
    {
        // The final entry is the take's first frame. Round each onset from its unrounded absolute time.
        var meter = song.MeterMap.MeterAt(startTick);
        int beats = meter.Numerator;
        var tempoAtStart = song.TempoMap.Changes.Last(change => change.Tick <= startTick);
        double framesPerBeat = tempoAtStart.MicrosecondsPerQuarterNote * (double)SampleRate
            * 4 / (meter.Denominator * 1_000_000.0);
        var onsets = new long[beats + 1];
        for (int beat = 1; beat <= beats; beat++)
            onsets[beat] = (long)Math.Round(beat * framesPerBeat, MidpointRounding.AwayFromZero);
        return onsets;
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

    private void ReleaseLiveNotes(IMonitorSynth synth, long frame)
    {
        for (byte channel = 0; channel < 16; channel++)
        {
            synth.ProcessMidi((byte)(0xb0 | channel), 120, 0);
            synth.ProcessMidi((byte)(0xb0 | channel), 121, 0);
        }
        _recording.FinishAll(frame);
    }

    private void ProcessEvent(RenderEvent next, long receivedFrame, long eventFrame, ref IMonitorSynth synth)
    {
        switch (next.Kind)
        {
            case EventKind.Midi:
                var midi = next.Midi;
                synth.ProcessMidi(midi.Status, midi.Data1, midi.Data2);
                _recording.ProcessMidi(midi, receivedFrame, eventFrame);
                break;
            case EventKind.LiveMidi:
                var input = next.Midi;
                if (_liveMidiDestinations?[input.Status & 0x0F] is { } destination)
                {
                    int command = input.Status & 0xF0;
                    // The track owns its bank and program. Controller input still carries expression and pedals.
                    if (command == 0xC0 || (command == 0xB0 && input.Data1 is 0 or 32))
                        break;
                    var routed = input with { Status = (byte)(command | destination.Channel) };
                    synth.ProcessMidi(routed.Status, routed.Data1, routed.Data2);
                    _recording.ProcessMidi(routed, receivedFrame, eventFrame, destination.TrackIndex);
                }
                break;
            case EventKind.LiveMidiConfiguration:
                var configuration = next.LiveMidi!;
                ReleaseLiveNotes(synth, receivedFrame);
                _liveMidiDestinations = configuration.Destinations;
                if (configuration.Synth is { } replacement)
                {
                    synth = replacement;
                    Volatile.Write(ref _synth, replacement);
                }
                break;
            case EventKind.StartRecording:
                _testMetronomeStartFrame = next.PlayTestMetronome ? eventFrame : null;
                _recording.Start(receivedFrame, next.TakeId, next.OffsetFrames, next.QuantizationDivision,
                    _testMetronomeStartFrame);
                break;
            case EventKind.StopRecording:
                _testMetronomeStartFrame = null;
                _recording.Stop(receivedFrame);
                Volatile.Write(ref _completedTakeId, next.TakeId);
                break;
            case EventKind.StartSongTake:
                if (Volatile.Read(ref _songTake) is { } take && take.Id == next.TakeId)
                {
                    _recording.Stop(receivedFrame);
                    _testMetronomeStartFrame = null;
                    _activeSongTake = take;
                    take.CountInStartFrame = eventFrame;
                    take.SongStartFrame = eventFrame + (take.CountInOnsets.Length == 0 ? 0 : take.CountInOnsets[^1]);
                    _recording.Start(take.SongStartFrame, receivedFrame, take.CountInOnsets.Length != 0,
                        take.Id, take.Held, take.NoteReady, take.OpeningTiming);
                    _playback.Request(take.Backing, take.SongStartFrame);
                }
                break;
            case EventKind.StopSongTake:
                if (_activeSongTake is { } stopped && stopped.Id == next.TakeId)
                {
                    _recording.Stop(receivedFrame);
                    stopped.Backing?.Overdub?.Stop();
                    _playback.Stop();
                    _activeSongTake = null;
                    Volatile.Write(ref _songTake, null);
                    Volatile.Write(ref _completedTakeId, stopped.Id);
                }
                break;
        }
    }

    private void MixTransport(Span<float> output, long firstFrame)
    {
        if (_activeSongTake is not { } take)
        {
            if (_testMetronomeStartFrame is long testStart)
            {
                _playback.Render(output, firstFrame, TempoMap, null);
                MixClicks(output, firstFrame - testStart, TempoMap, _defaultMeter, 0);
            }
            else
            {
                _playback.Render(output, firstFrame, TempoMap, MetronomeEnabled ? _mixMetronome : null,
                    PreviewMetronomeEnabled ? _mixMetronome : null);
            }
            return;
        }

        if (firstFrame < take.SongStartFrame)
        {
            // A block may straddle the last count beat and bar one.
            int countFrames = (int)Math.Min(output.Length / 2, take.SongStartFrame - firstFrame);
            Span<float> countOutput = output[..(2 * countFrames)];
            _playback.Render(countOutput, firstFrame, TempoMap, null);
            MixCountIn(countOutput, firstFrame, take);
            firstFrame += countFrames;
            output = output[(2 * countFrames)..];
            if (firstFrame < take.SongStartFrame)
                return;
        }

        long songFrame = firstFrame - take.SongStartFrame;
        long renderedEnd = songFrame + output.Length / 2;
        if (take.Loop is { } loop)
        {
            if (!output.IsEmpty)
                _playback.Render(output, firstFrame, TempoMap, MetronomeEnabled ? _mixMetronome : null);

            Volatile.Write(ref take.LoopPass, renderedEnd / loop.Length + 1);
        }
        else
        {
            if (!output.IsEmpty)
            {
                _playback.Render(output, firstFrame, TempoMap, null);
                if (MetronomeEnabled)
                    MixClicks(output, songFrame, take.Tempo, take.MeterMap, 0);
            }
        }

        Volatile.Write(ref take.PlaybackTick, take.TickAt(renderedEnd, SampleRate));
        Volatile.Write(ref take.BeatsRemaining, 0);
    }

    private void MixCountIn(Span<float> output, long firstFrame, SongTake take)
    {
        long endFrame = firstFrame + output.Length / 2;
        var offsets = take.CountInOnsets;
        int beats = offsets.Length - 1;
        int remaining = beats;
        for (int index = 0; index < beats; index++)
        {
            long onset = take.CountInStartFrame + offsets[index];
            if (onset >= endFrame)
                break;

            remaining = beats - index;
            long nextPulse = take.CountInStartFrame + offsets[index + 1];
            MixClick(output, firstFrame, onset, Math.Min(nextPulse, endFrame), index == 0 ? _accent : _beat);
        }
        Volatile.Write(ref take.BeatsRemaining, remaining);
    }

    private void MixClicks(Span<float> output, long firstFrame, TempoMap tempoMap, MeterMap meterMap, long firstAllowedOnset)
    {
        if (output.IsEmpty)
            return;

        long endFrame = firstFrame + output.Length / 2;
        int index = MeterIndexAtFrame(firstFrame);
        int lastIndex = MeterIndexAtFrame(endFrame - 1);
        while (!output.IsEmpty)
        {
            var change = meterMap.Changes[index];
            long segmentEnd = index == lastIndex ? endFrame : tempoMap.TickToSample(meterMap.Changes[index + 1].Tick, SampleRate);
            int samples = checked((int)(2 * (segmentEnd - firstFrame)));
            long meterStart = tempoMap.TickToSample(change.Tick, SampleRate);
            MixMeterSection(output[..samples], firstFrame, tempoMap, change, Math.Max(firstAllowedOnset, meterStart));
            output = output[samples..];
            firstFrame = segmentEnd;
            if (!output.IsEmpty)
                index = Math.Max(index + 1, MeterIndexAtFrame(firstFrame));
        }

        // Several markers can round onto one sample. Find the last one active on that sample
        // without walking each event. A marker exactly half a sample later rounds up.
        int MeterIndexAtFrame(long frame)
        {
            double upperTick = tempoMap.SecondsToTick((frame + 0.5) / SampleRate);
            int active = meterMap.IndexAt(upperTick);
            if (active > 0 && tempoMap.TickToSample(meterMap.Changes[active].Tick, SampleRate) > frame)
                active--;
            return active;
        }
    }

    private void MixMeterSection(Span<float> output, long firstFrame, TempoMap tempoMap, MeterChange change, long firstAllowedOnset)
    {
        int clickLength = _beat.Length;
        long endFrame = firstFrame + output.Length / 2;
        long earliest = Math.Max(firstAllowedOnset, firstFrame - clickLength + 1);
        long bucket = earliest - earliest % clickLength;
        double ticksPerBeat = 4.0 * tempoMap.TicksPerQuarter / change.Meter.Denominator;
        while (bucket < endFrame)
        {
            // Keep one click per click-length interval even if the MIDI tempo packs many beats
            // into one sample. The half-frame offset matches TickToSample's nearest rounding.
            long firstOnset = Math.Max(bucket, firstAllowedOnset);
            double bucketTick = tempoMap.SecondsToTick((firstOnset - 0.5) / SampleRate);
            long beat = Math.Max(0, (long)Math.Floor((bucketTick - change.Tick) / ticksPerBeat));
            long clickFrame = tempoMap.TickToSample(change.Tick + beat * ticksPerBeat, SampleRate);
            if (clickFrame < firstOnset)
                clickFrame = tempoMap.TickToSample(change.Tick + ++beat * ticksPerBeat, SampleRate);
            if (clickFrame >= endFrame)
                break;

            MixClick(output, firstFrame, clickFrame, endFrame, beat % change.Meter.Numerator == 0 ? _accent : _beat);

            long clickBucket = clickFrame - clickFrame % clickLength;
            bucket = Math.Max(bucket, clickBucket) + clickLength;
        }
    }

    private static void MixClick(Span<float> output, long firstFrame, long onset, long endFrame, float[] click)
    {
        long start = Math.Max(onset, firstFrame);
        long end = Math.Min(onset + click.Length, endFrame);
        for (long frame = start; frame < end; frame++)
        {
            int sample = (int)((frame - firstFrame) * 2);
            float value = click[(int)(frame - onset)];
            output[sample] = Math.Clamp(output[sample] + value, -1, 1);
            output[sample + 1] = Math.Clamp(output[sample + 1] + value, -1, 1);
        }
    }

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

    private enum EventKind { Midi, LiveMidi, LiveMidiConfiguration, StartRecording, StopRecording, StartSongTake, StopSongTake }

    private sealed record LiveMidiConfiguration(LiveMidiDestination?[] Destinations, IMonitorSynth? Synth);

    private sealed class SongTake(long id, long startTimestamp, HeldRecordingNotes held,
        TempoMap tempo, MeterMap meterMap, NotePlayback.PlaybackPlan? backing, NotePlayback.PlaybackLoop? loop,
        OverdubReplay? replay, long[] countInOnsets, OpeningTimingBuffer openingTiming)
    {
        public long Id { get; } = id;
        public long StartTimestamp { get; } = startTimestamp;
        public HeldRecordingNotes Held { get; } = held;
        public TempoMap Tempo { get; } = tempo;
        public MeterMap MeterMap { get; } = meterMap;
        public NotePlayback.PlaybackPlan? Backing { get; } = backing;
        public NotePlayback.PlaybackLoop? Loop { get; } = loop;
        public OverdubNoteReady? NoteReady { get; } = replay is null ? null : replay.AddNote;
        public long[] CountInOnsets { get; } = countInOnsets;
        public OpeningTimingBuffer OpeningTiming { get; } = openingTiming;
        public bool StopRequested;
        public long CountInStartFrame;
        public long SongStartFrame;
        public long PresentationOriginTimestamp = long.MinValue;
        public long ElapsedFrames;
        public long PlaybackTick = loop?.Range.StartTick ?? 0;
        public long LoopPass = 1;
        public int BeatsRemaining = countInOnsets.Length == 0 ? 0 : countInOnsets.Length - 1;

        public long TickAt(long elapsed, int sampleRate)
        {
            if (Loop is not { } loop)
                return (long)Tempo.SampleToTick(elapsed, sampleRate);
            long position = loop.PositionAt(elapsed);
            return position == loop.StartFrame ? loop.Range.StartTick :
                Math.Clamp((long)Tempo.SampleToTick(position, sampleRate), loop.Range.StartTick, loop.Range.EndTick - 1);
        }
    }

    private readonly record struct RenderEvent(
        long Timestamp,
        MidiMessage Midi = default,
        int MidiGeneration = 0,
        long Sequence = 0,
        long TakeId = 0,
        long OffsetFrames = 0,
        int QuantizationDivision = 0,
        EventKind Kind = EventKind.Midi,
        LiveMidiConfiguration? LiveMidi = null,
        bool PlayTestMetronome = false);
}
