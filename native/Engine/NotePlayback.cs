namespace Signal.Native.Engine;

internal delegate void MetronomeMixer(
    Span<float> output, long firstFrame, TempoMap tempo, MeterMap meterMap, long firstAllowedOnset);

/// <summary>Plays a prepared snapshot on the audio thread without changing live MIDI state.</summary>
internal sealed class NotePlayback(int sampleRate, MeterMap liveMeter)
{
    private const long Finished = -1;
    private const int Channels = 16;
    private const int Keys = 128;
    private readonly float[] _scratch = new float[512];
    private readonly int _fadeFrames = Math.Max(1, sampleRate / 100);
    private readonly int _loopFadeFrames = Math.Max(1, sampleRate / 500);
    private readonly PlaybackEvent?[] _loopRecoveryHeld = new PlaybackEvent?[Channels * Keys];
    private readonly bool[] _heldKeys = new bool[Channels * Keys];
    private PlaybackPlan? _requested;
    private PlaybackPlan? _active;
    private PlaybackTimeline? _renderedTimeline;
    private PlaybackLoop? _loop;
    private IMonitorSynth? _fadingSynth;
    private int _fadeRemaining;
    private int _nextEvent;
    private long _startFrame;
    private long _firstAllowedOnset;
    private long? _nextLoopFrame;

    public long? Tick
    {
        get
        {
            var plan = Volatile.Read(ref _requested);
            if (plan is null)
                return null;
            long tick = Volatile.Read(ref plan.Tick);
            return tick == Finished ? null : tick;
        }
    }

    public PlaybackPlan? Prepare(IEnumerable<SongNote> notes, IEnumerable<SongChannelMessage> messages,
        TempoMap tempo, IMonitorSynth synth, long endTick = 0, LoopRange? loop = null, MeterMap? meterMap = null,
        bool allowEmpty = false, long startTick = 0)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(synth);
        ArgumentOutOfRangeException.ThrowIfNegative(startTick);
        ValidateRange(loop);
        var events = PrepareEvents(notes, messages, tempo);
        if (events.Length == 0 && !allowEmpty)
            return null;

        var timeline = PrepareTimeline(events, tempo, endTick, loop);
        var plan = new PlaybackPlan(synth, tempo, meterMap ?? liveMeter, timeline)
        {
            Tick = loop?.StartTick ?? 0
        };
        long position = tempo.TickToSample(startTick, sampleRate);
        if (timeline.Loop is { } cycle
            ? position > cycle.StartFrame && position < cycle.EndFrame
            : position > 0 && position < timeline.EndFrame)
        {
            plan.Timeline = timeline with { Seek = new PlaybackSeek(position, PrepareSeekChase(events, position)) };
            plan.Tick = startTick;
        }
        return plan;
    }

    private PlaybackEvent[] PrepareEvents(IEnumerable<SongNote> notes, IEnumerable<SongChannelMessage> messages,
        TempoMap tempo)
    {
        var events = new List<PlaybackEvent>();
        foreach (var note in notes)
        {
            if (note.StartTick < 0 || note.LengthTicks < 0 || note.Channel >= Channels ||
                note.NoteNumber >= Keys || note.Velocity is 0 or >= 128)
            {
                throw new ArgumentOutOfRangeException(nameof(notes), "Notes require valid MIDI data and nonnegative time.");
            }

            long start = tempo.TickToSample(note.StartTick, sampleRate);
            long endTickOfNote = checked(note.StartTick + note.LengthTicks);
            long end = tempo.TickToSample(endTickOfNote, sampleRate);
            events.Add(new PlaybackEvent(start, note.StartTick, (byte)(0x90 | note.Channel),
                note.NoteNumber, note.Velocity, events.Count));
            events.Add(new PlaybackEvent(Math.Max(checked(start + 1), end), endTickOfNote, (byte)(0x80 | note.Channel),
                note.NoteNumber, 0, events.Count));
        }
        foreach (var message in messages)
        {
            if (message.Tick < 0 || message.Status is < 0xa0 or > 0xef || message.Data1 > 127 || message.Data2 > 127)
                throw new ArgumentOutOfRangeException(nameof(messages), "Messages require valid non-note MIDI channel data and nonnegative time.");

            long frame = tempo.TickToSample(message.Tick, sampleRate);
            events.Add(new PlaybackEvent(frame, message.Tick, message.Status, message.Data1, message.Data2, events.Count));
        }
        events.Sort(static (left, right) =>
        {
            int order = left.Frame.CompareTo(right.Frame);
            if (order == 0)
                order = left.Tick.CompareTo(right.Tick);
            // Release first, then update channel state before attacking at the same tick.
            if (order == 0)
                order = left.Priority.CompareTo(right.Priority);
            return order != 0 ? order : left.Order.CompareTo(right.Order);
        });
        MergeOverlappingReleases(events);
        return events.ToArray();
    }

    private PlaybackTimeline PrepareTimeline(PlaybackEvent[] events, TempoMap tempo, long endTick, LoopRange? loop)
    {
        long endFrame = Math.Max(events.Length == 0 ? 0 : events[^1].Frame, tempo.TickToSample(endTick, sampleRate));
        var cycle = loop is { } range ? PrepareLoop(events, tempo, range) : null;
        return new PlaybackTimeline(events, endFrame, checked(endFrame + sampleRate), cycle, GetOpeningSounds(events));
    }

    public void Request(PlaybackPlan? plan, long? originFrame = null)
    {
        if (plan is not null)
            plan.OriginFrame = originFrame;
        Volatile.Write(ref _requested, plan);
    }

    public void SetLoop(LoopRange? loop)
    {
        ValidateRange(loop);
        var plan = Volatile.Read(ref _requested);
        if (plan is null || plan.OriginFrame is not null || Volatile.Read(ref plan.Tick) == Finished)
            return;

        var timeline = Volatile.Read(ref plan.Timeline);
        if (timeline.Loop?.Range == loop)
            return;
        var prepared = loop is { } range ? PrepareLoop(timeline.Events, plan.TempoMap, range) : null;
        Interlocked.CompareExchange(ref plan.Timeline, timeline with { Loop = prepared }, timeline);
    }

    public bool UpdateTimeline(IEnumerable<SongNote> notes, IEnumerable<SongChannelMessage> messages,
        long endTick, LoopRange? loop)
    {
        ValidateRange(loop);
        var plan = Volatile.Read(ref _requested);
        if (plan is null || plan.OriginFrame is not null || Volatile.Read(ref plan.Tick) == Finished)
            return false;

        var previous = Volatile.Read(ref plan.Timeline);
        var events = PrepareEvents(notes, messages, plan.TempoMap);
        var timeline = PrepareTimeline(events, plan.TempoMap, endTick, loop) with
        {
            KeyEvents = events.Where(message => message.Command is 0x80 or 0x90)
                .GroupBy(KeyIndex).ToDictionary(group => group.Key, group => group.ToArray()),
            Seek = previous.Seek is { Frame: var frame } ? new PlaybackSeek(frame, PrepareSeekChase(events, frame)) : null
        };
        if (!ReferenceEquals(Volatile.Read(ref _requested), plan) || Volatile.Read(ref plan.Tick) == Finished)
            return false;
        return ReferenceEquals(Interlocked.CompareExchange(ref plan.Timeline, timeline, previous), previous);
    }

    public void Stop() => Volatile.Write(ref _requested, null);

    public void Render(Span<float> output, long firstFrame, TempoMap liveTempo, MetronomeMixer? mixMetronome,
        MetronomeMixer? mixPreview = null)
    {
        var requested = Volatile.Read(ref _requested);
        var timeline = requested is null ? null : Volatile.Read(ref requested.Timeline);
        var previousTimeline = _renderedTimeline;
        bool restarting = !ReferenceEquals(requested, _active);
        bool edited = !restarting && !ReferenceEquals(previousTimeline?.Events, timeline?.Events);
        _renderedTimeline = timeline;
        if (restarting)
        {
            if (_active is { } previous)
            {
                _fadingSynth = previous.Synth;
                _fadeRemaining = _fadeFrames;
            }
            _active = requested;
            _loop = null;
            _nextLoopFrame = null;
            _startFrame = requested?.OriginFrame ?? firstFrame;
            _firstAllowedOnset = 0;
            _nextEvent = 0;
            Array.Clear(_heldKeys);
            if (requested is not null && timeline?.Seek is { } seek)
            {
                foreach (var message in seek.Chase)
                    PlayMessage(requested.Synth, message);
                _startFrame = firstFrame - seek.Frame;
                _firstAllowedOnset = seek.Frame;
                _nextEvent = FirstAtOrAfter(timeline.Events, seek.Frame);
            }
        }

        if (_fadingSynth is { } fading)
        {
            int frames = Math.Min(_fadeRemaining, output.Length / 2);
            Mix(fading, output[..(2 * frames)], _fadeRemaining, _fadeFrames);
            _fadeRemaining -= frames;
            if (_fadeRemaining == 0)
                _fadingSynth = null;
        }

        if (_active is not { } active || timeline is null)
        {
            mixPreview?.Invoke(output, firstFrame, liveTempo, liveMeter, 0);
            return;
        }

        if (firstFrame < _startFrame)
            return;

        ApplyLoopChange(active, timeline, firstFrame, restarting);
        if (edited)
        {
            long position = firstFrame - _startFrame;
            _nextEvent = FirstAtOrAfter(_loop?.Events ?? timeline.Events, position);
            if (_loop is not { } cycle || position < cycle.EndFrame)
            {
                ReleaseMissingNotes(active.Synth, timeline.KeyEvents!, position);
                ApplyOpeningSoundEdits(active.Synth, previousTimeline!.OpeningSounds, timeline.OpeningSounds);
            }
        }
        if (_loop is { } loop)
            RenderLoop(output, firstFrame, active, loop, mixMetronome);
        else
            RenderSong(output, firstFrame, active, timeline, mixMetronome);
    }

    private void ApplyLoopChange(PlaybackPlan active, PlaybackTimeline timeline, long firstFrame, bool restarting)
    {
        var next = timeline.Loop;
        if (ReferenceEquals(next, _loop))
            return;

        bool sameRange = next?.Range == _loop?.Range;
        _loop = next;
        if (sameRange)
            return;

        _nextLoopFrame = null;
        long position = firstFrame - _startFrame;
        if (next is { } anchoredLoop && active.OriginFrame is long origin)
        {
            // Recording and playback must keep the same cycle phase if the endpoint skips frames.
            SeekLoop(active, anchoredLoop, firstFrame, anchoredLoop.PositionAt(firstFrame - origin));
        }
        else if (next is { } loop &&
            ((restarting && timeline.Seek is null) || position < loop.StartFrame || position >= loop.EndFrame))
        {
            SeekLoop(active, loop, firstFrame, loop.StartFrame);
        }
        else
        {
            _nextEvent = FirstAtOrAfter(next?.Events ?? timeline.Events, position);
        }
    }

    private void RenderLoop(Span<float> output, long firstFrame, PlaybackPlan active, PlaybackLoop loop,
        MetronomeMixer? mixMetronome)
    {
        long position = firstFrame - _startFrame;
        if (_nextLoopFrame is { } expected && firstFrame > expected && position < loop.EndFrame)
            SeekLoop(active, loop, firstFrame, position);
        _nextLoopFrame = firstFrame + output.Length / 2;
        int fade = (int)Math.Min(_loopFadeFrames, Math.Max(1, loop.Length / 2));
        while (!output.IsEmpty)
        {
            if (position >= loop.EndFrame)
            {
                // A device underrun may advance by more than a single loop.
                position = loop.StartFrame + (position - loop.EndFrame) % loop.Length;
                SeekLoop(active, loop, firstFrame, position);
            }

            int frames = (int)Math.Min(output.Length / 2, loop.EndFrame - position);
            var segment = output[..(2 * frames)];
            RenderEvents(active.Synth, loop.Events, segment, position, loop.EndFrame, fade);
            if (active.Overdub is { } overdub && active.OriginFrame is long origin)
                overdub.Render(segment, (firstFrame - origin) / loop.Length, position);
            mixMetronome?.Invoke(segment, position, active.TempoMap, active.MeterMap,
                Math.Max(loop.StartFrame, _firstAllowedOnset));

            firstFrame += frames;
            position += frames;
            output = output[(2 * frames)..];
        }
        long tick = position == loop.EndFrame ? loop.Range.StartTick :
            Math.Clamp((long)active.TempoMap.SampleToTick(position, sampleRate), loop.Range.StartTick, loop.Range.EndTick - 1);
        Volatile.Write(ref active.Tick, tick);
    }

    private void SeekLoop(PlaybackPlan active, PlaybackLoop loop, long firstFrame, long position)
    {
        active.Synth.Reset();
        Array.Clear(_heldKeys);
        _startFrame = firstFrame - position;
        _firstAllowedOnset = loop.StartFrame;
        if (position == loop.StartFrame)
        {
            foreach (var message in loop.Chase)
                PlayMessage(active.Synth, message);
            _nextEvent = 0;
            return;
        }

        // A skipped buffer can land in the middle of a pass. Recover the state at
        // that position without bunching every missed attack at the first writable frame.
        Array.Clear(_loopRecoveryHeld);
        foreach (var message in loop.Chase)
            RecoverMessage(message);
        _nextEvent = FirstAtOrAfter(loop.Events, position);
        for (int i = 0; i < _nextEvent; i++)
            RecoverMessage(loop.Events[i]);
        for (int i = _nextEvent; i < loop.Events.Length && loop.Events[i].Frame == position; i++)
            ClearHeldAtBoundary(loop.Events[i], _loopRecoveryHeld);
        foreach (var held in _loopRecoveryHeld)
        {
            if (held is { } message)
                PlayMessage(active.Synth, message);
        }

        void RecoverMessage(PlaybackEvent message)
        {
            if (message.Command == 0x90)
            {
                int channel = message.Status & 0x0f;
                // Percussion attacks have already passed, even if a key is still held.
                if (channel != SongTrackEditor.DrumChannel)
                    _loopRecoveryHeld[channel * Keys + message.Data1] = message;
            }
            else
            {
                ClearHeldAtBoundary(message, _loopRecoveryHeld);
                if (message.Command != 0x80)
                    PlayMessage(active.Synth, message);
            }
        }
    }

    private void RenderSong(Span<float> output, long firstFrame, PlaybackPlan active, PlaybackTimeline timeline,
        MetronomeMixer? mixMetronome)
    {
        long relativeFirst = firstFrame - _startFrame;
        long relativeEnd = Math.Min(relativeFirst + output.Length / 2, timeline.EndWithRelease);
        if (relativeFirst < relativeEnd)
        {
            int length = (int)((relativeEnd - relativeFirst) * 2);
            RenderEvents(active.Synth, timeline.Events, output[..length], relativeFirst, timeline.EndWithRelease, _fadeFrames);
        }
        if (mixMetronome is not null && relativeFirst < timeline.EndFrame)
        {
            int clickFrames = (int)Math.Min(output.Length / 2, timeline.EndFrame - relativeFirst);
            mixMetronome(output[..(2 * clickFrames)], relativeFirst, active.TempoMap, active.MeterMap, _firstAllowedOnset);
        }

        long tick = _nextEvent == timeline.Events.Length && relativeEnd >= timeline.EndFrame
            ? Finished
            : (long)active.TempoMap.SampleToTick(relativeEnd, sampleRate);
        Volatile.Write(ref active.Tick, tick);
        if (relativeEnd >= timeline.EndWithRelease)
        {
            Interlocked.CompareExchange(ref _requested, null, active);
            _active = null;
        }
    }

    private void RenderEvents(IMonitorSynth synth, PlaybackEvent[] events, Span<float> output,
        long firstFrame, long fadeEnd, int fadeFrames)
    {
        long endFrame = firstFrame + output.Length / 2;
        long cursor = firstFrame;
        while (_nextEvent < events.Length && events[_nextEvent].Frame < endFrame)
        {
            var message = events[_nextEvent++];
            if (message.Frame > cursor)
            {
                Mix(synth, output[BufferIndex(cursor)..BufferIndex(message.Frame)], fadeEnd - cursor, fadeFrames);
                cursor = message.Frame;
            }
            PlayMessage(synth, message);
        }
        if (cursor < endFrame)
            Mix(synth, output[BufferIndex(cursor)..], fadeEnd - cursor, fadeFrames);

        int BufferIndex(long frame) => (int)((frame - firstFrame) * 2);
    }

    private static int KeyIndex(PlaybackEvent message) => (message.Status & 0x0f) * Keys + message.Data1;

    private void PlayMessage(IMonitorSynth synth, PlaybackEvent message)
    {
        if (message.Command is 0x80 or 0x90)
            _heldKeys[KeyIndex(message)] = message.Command == 0x90;
        else if (message.Command == 0xb0 && message.Data1 is 120 or 123)
            Array.Clear(_heldKeys, (message.Status & 0x0f) * Keys, Keys);
        synth.ProcessMidi(message.Status, message.Data1, message.Data2);
    }

    private void ReleaseMissingNotes(IMonitorSynth synth, Dictionary<int, PlaybackEvent[]> keyEvents, long position)
    {
        for (int key = 0; key < _heldKeys.Length; key++)
        {
            if (!_heldKeys[key])
                continue;
            if (keyEvents.TryGetValue(key, out var events))
            {
                int index = FirstAtOrAfter(events, position);
                if (index > 0 && events[index - 1].Command == 0x90)
                    continue;
            }
            _heldKeys[key] = false;
            synth.ProcessMidi((byte)(0x80 | (key / Keys)), (byte)(key % Keys), 0);
        }
    }

    private static ChannelSound[] GetOpeningSounds(PlaybackEvent[] events)
    {
        var sounds = new ChannelSound[Channels];
        foreach (var message in events)
        {
            if (message.Tick != 0)
                break;
            ref var sound = ref sounds[message.Status & 0x0f];
            switch (message.Command)
            {
                case 0xb0 when message.Data1 == 0: sound = sound with { BankMsb = message.Data2 }; break;
                case 0xb0 when message.Data1 == 32: sound = sound with { BankLsb = message.Data2 }; break;
                case 0xc0: sound = sound with { Program = message.Data1 }; break;
            }
        }
        return sounds;
    }

    private static void ApplyOpeningSoundEdits(IMonitorSynth synth, ChannelSound[] previous, ChannelSound[] current)
    {
        // The edited opening events have already passed on this playthrough. Apply the
        // changed patch for future attacks without touching the channel's sounding voices.
        for (int channel = 0; channel < Channels; channel++)
        {
            var sound = current[channel];
            if (sound == previous[channel])
                continue;

            synth.ProcessMidi((byte)(0xb0 | channel), 0, sound.BankMsb);
            synth.ProcessMidi((byte)(0xb0 | channel), 32, sound.BankLsb);
            synth.ProcessMidi((byte)(0xc0 | channel), sound.Program, 0);
        }
    }

    private void Mix(IMonitorSynth synth, Span<float> destination, long framesToFadeEnd, int fadeFrames)
    {
        while (!destination.IsEmpty)
        {
            int length = Math.Min(destination.Length, _scratch.Length);
            var samples = _scratch.AsSpan(0, length);
            synth.Render(samples);
            for (int index = 0; index < length; index += 2)
            {
                float gain = framesToFadeEnd >= fadeFrames ? 1f : framesToFadeEnd / (float)fadeFrames;
                destination[index] += samples[index] * gain;
                destination[index + 1] += samples[index + 1] * gain;
                framesToFadeEnd--;
            }
            destination = destination[length..];
        }
    }

    private PlaybackLoop PrepareLoop(PlaybackEvent[] timeline, TempoMap tempo, LoopRange range)
    {
        long start = tempo.TickToSample(range.StartTick, sampleRate);
        long end = tempo.TickToSample(range.EndTick, sampleRate);
        if (end <= start)
            throw new ArgumentOutOfRangeException(nameof(range), "The loop must span at least one audio sample.");

        var events = new List<PlaybackEvent>();
        var held = new PlaybackEvent?[Channels * Keys];
        var setup = new Dictionary<(byte Status, byte Key), PlaybackEvent>();
        foreach (var message in timeline)
        {
            if (message.Tick < range.StartTick)
            {
                ChaseBeforeStart(message, setup, held);
            }
            else if (message.Tick < range.EndTick)
            {
                // A rounded frame can land at the end even when the MIDI event is inside the range.
                events.Add(message with { Frame = Math.Clamp(message.Frame, start, end - 1) });
            }
        }

        // Releases at the first tick are applied before the loop attacks. Do not briefly
        // retrigger these voices only to release them before the first rendered sample.
        foreach (var message in events)
        {
            if (message.Tick != range.StartTick)
                continue;

            ClearHeldAtBoundary(message, held);
        }

        return new PlaybackLoop(range, start, end, events.ToArray(), OrderChase(setup, held));
    }

    private static PlaybackEvent[] PrepareSeekChase(PlaybackEvent[] events, long frame)
    {
        var held = new PlaybackEvent?[Channels * Keys];
        var setup = new Dictionary<(byte Status, byte Key), PlaybackEvent>();
        foreach (var message in events)
        {
            if (message.Frame > frame)
                break;
            if (message.Frame < frame)
                ChaseBeforeStart(message, setup, held);
            else
                ClearHeldAtBoundary(message, held);
        }
        return OrderChase(setup, held);
    }

    private static void ClearHeldAtBoundary(PlaybackEvent message, PlaybackEvent?[] held)
    {
        int channelStart = (message.Status & 0x0f) * Keys;
        if (message.Command == 0x80)
            held[channelStart + message.Data1] = null;
        else if (message.Command == 0xb0 && message.Data1 is 120 or 123)
            Array.Clear(held, channelStart, Keys);
    }

    private static PlaybackEvent[] OrderChase(Dictionary<(byte Status, byte Key), PlaybackEvent> setup,
        PlaybackEvent?[] held) =>
        setup.Values.OrderBy(message => message.Frame).ThenBy(message => message.Tick)
            .ThenBy(message => message.Order)
            .Concat(held.Where(message => message.HasValue).Select(message => message.GetValueOrDefault()))
            .ToArray();

    private static void ChaseBeforeStart(PlaybackEvent message,
        Dictionary<(byte Status, byte Key), PlaybackEvent> setup, PlaybackEvent?[] held)
    {
        int channelStart = (message.Status & 0x0f) * Keys;
        switch (message.Command)
        {
            case 0x80:
                held[channelStart + message.Data1] = null;
                break;
            case 0x90:
                held[channelStart + message.Data1] = message;
                break;
            case 0xb0 when message.Data1 is 120 or 123:
                Array.Clear(held, channelStart, Keys);
                break;
            default:
                byte key = message.Command is 0xa0 or 0xb0 ? message.Data1 : (byte)0;
                setup[(message.Status, key)] = message;
                break;
        }
    }

    private static int FirstAtOrAfter(PlaybackEvent[] events, long frame)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (events[middle].Frame < frame)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static void ValidateRange(LoopRange? loop)
    {
        if (loop is { } range && (range.StartTick < 0 || range.EndTick <= range.StartTick))
            throw new ArgumentOutOfRangeException(nameof(loop), "The loop end must follow its nonnegative start.");
    }

    private static void MergeOverlappingReleases(List<PlaybackEvent> events)
    {
        // A MIDI note-off releases all voices on that pitch and channel in MeltySynth.
        // Keep retriggers, and release only when the last overlapping note ends.
        var held = new int[Channels * Keys];
        int written = 0;
        for (int index = 0; index < events.Count; index++)
        {
            var note = events[index];
            int key = (note.Status & 0x0f) * Keys + note.Data1;
            if (note.Command == 0x90)
                held[key]++;
            else if (note.Command == 0x80 && --held[key] != 0)
                continue;

            events[written++] = note;
        }
        events.RemoveRange(written, events.Count - written);
    }

    internal sealed class PlaybackPlan(IMonitorSynth synth, TempoMap tempoMap, MeterMap meterMap, PlaybackTimeline timeline)
    {
        public IMonitorSynth Synth { get; } = synth;
        public TempoMap TempoMap { get; } = tempoMap;
        public MeterMap MeterMap { get; } = meterMap;
        public PlaybackTimeline Timeline = timeline;
        public OverdubReplay? Overdub;
        public long? OriginFrame;
        public long Tick;
    }

    internal sealed record PlaybackTimeline(PlaybackEvent[] Events, long EndFrame, long EndWithRelease, PlaybackLoop? Loop,
        ChannelSound[] OpeningSounds)
    {
        public PlaybackSeek? Seek { get; init; }
        public Dictionary<int, PlaybackEvent[]>? KeyEvents { get; init; }
    }

    internal sealed record PlaybackSeek(long Frame, PlaybackEvent[] Chase);

    internal readonly record struct ChannelSound(byte BankMsb, byte BankLsb, byte Program);

    internal sealed record PlaybackLoop(LoopRange Range, long StartFrame, long EndFrame,
        PlaybackEvent[] Events, PlaybackEvent[] Chase)
    {
        public long Length => EndFrame - StartFrame;
        public long PositionAt(long elapsedFrames) => StartFrame + elapsedFrames % Length;
    }

    internal readonly record struct PlaybackEvent(long Frame, long Tick, byte Status, byte Data1, byte Data2, int Order)
    {
        public int Command => Status & 0xf0;
        public int Priority => Command switch { 0x80 => 0, 0x90 => 2, _ => 1 };
    }
}
