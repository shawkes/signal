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
    private PlaybackPlan? _requested;
    private PlaybackPlan? _active;
    private PlaybackLoop? _loop;
    private IMonitorSynth? _fadingSynth;
    private int _fadeRemaining;
    private int _nextEvent;
    private long _startFrame;
    private long _firstAllowedOnset;

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
        bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(synth);
        ValidateRange(loop);
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
        if (events.Count == 0 && !allowEmpty)
            return null;

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
        long endFrame = Math.Max(events.Count == 0 ? 0 : events[^1].Frame, tempo.TickToSample(endTick, sampleRate));
        long endWithRelease = checked(endFrame + sampleRate);
        var plan = new PlaybackPlan(synth, tempo, meterMap ?? liveMeter, events.ToArray(), endFrame, endWithRelease);
        if (loop is { } range)
        {
            plan.RequestedLoop = PrepareLoop(plan, range);
            plan.Tick = range.StartTick;
        }
        return plan;
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
        if (plan is null || plan.OriginFrame is not null || Volatile.Read(ref plan.Tick) == Finished ||
            Volatile.Read(ref plan.RequestedLoop)?.Range == loop)
        {
            return;
        }

        var prepared = loop is { } range ? PrepareLoop(plan, range) : null;
        Volatile.Write(ref plan.RequestedLoop, prepared);
    }

    public void Stop() => Volatile.Write(ref _requested, null);

    public void Render(Span<float> output, long firstFrame, TempoMap liveTempo, MetronomeMixer? mixMetronome,
        MetronomeMixer? mixPreview = null)
    {
        var requested = Volatile.Read(ref _requested);
        bool restarting = !ReferenceEquals(requested, _active);
        if (restarting)
        {
            if (_active is { } previous)
            {
                _fadingSynth = previous.Synth;
                _fadeRemaining = _fadeFrames;
            }
            _active = requested;
            _loop = null;
            _startFrame = requested?.OriginFrame ?? firstFrame;
            _firstAllowedOnset = 0;
            _nextEvent = 0;
        }

        if (_fadingSynth is { } fading)
        {
            int frames = Math.Min(_fadeRemaining, output.Length / 2);
            Mix(fading, output[..(2 * frames)], _fadeRemaining, _fadeFrames);
            _fadeRemaining -= frames;
            if (_fadeRemaining == 0)
                _fadingSynth = null;
        }

        if (_active is not { } active)
        {
            mixPreview?.Invoke(output, firstFrame, liveTempo, liveMeter, 0);
            return;
        }

        if (firstFrame < _startFrame)
            return;

        ApplyLoopChange(active, firstFrame, restarting);
        if (_loop is { } loop)
            RenderLoop(output, firstFrame, active, loop, mixMetronome);
        else
            RenderSong(output, firstFrame, active, mixMetronome);
    }

    private void ApplyLoopChange(PlaybackPlan active, long firstFrame, bool restarting)
    {
        var next = Volatile.Read(ref active.RequestedLoop);
        if (ReferenceEquals(next, _loop))
            return;

        _loop = next;
        long position = firstFrame - _startFrame;
        if (next is { } anchoredLoop && active.OriginFrame is long origin)
        {
            // Recording and playback must keep the same cycle phase if the endpoint skips frames.
            SeekLoop(active, anchoredLoop, firstFrame, anchoredLoop.PositionAt(firstFrame - origin));
        }
        else if (next is { } loop && (restarting || position < loop.StartFrame || position >= loop.EndFrame))
        {
            SeekLoop(active, loop, firstFrame, loop.StartFrame);
        }
        else
        {
            _nextEvent = FirstAtOrAfter(next?.Events ?? active.Events, position);
        }
    }

    private void RenderLoop(Span<float> output, long firstFrame, PlaybackPlan active, PlaybackLoop loop,
        MetronomeMixer? mixMetronome)
    {
        long position = firstFrame - _startFrame;
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
        foreach (var message in loop.Chase)
            active.Synth.ProcessMidi(message.Status, message.Data1, message.Data2);
        _startFrame = firstFrame - position;
        _firstAllowedOnset = loop.StartFrame;
        _nextEvent = 0;
    }

    private void RenderSong(Span<float> output, long firstFrame, PlaybackPlan active,
        MetronomeMixer? mixMetronome)
    {
        long relativeFirst = firstFrame - _startFrame;
        long relativeEnd = Math.Min(relativeFirst + output.Length / 2, active.EndWithRelease);
        if (relativeFirst < relativeEnd)
        {
            int length = (int)((relativeEnd - relativeFirst) * 2);
            RenderEvents(active.Synth, active.Events, output[..length], relativeFirst, active.EndWithRelease, _fadeFrames);
        }
        if (mixMetronome is not null && relativeFirst < active.EndFrame)
        {
            int clickFrames = (int)Math.Min(output.Length / 2, active.EndFrame - relativeFirst);
            mixMetronome(output[..(2 * clickFrames)], relativeFirst, active.TempoMap, active.MeterMap, _firstAllowedOnset);
        }

        long tick = _nextEvent == active.Events.Length && relativeEnd >= active.EndFrame
            ? Finished
            : (long)active.TempoMap.SampleToTick(relativeEnd, sampleRate);
        Volatile.Write(ref active.Tick, tick);
        if (relativeEnd >= active.EndWithRelease)
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
            synth.ProcessMidi(message.Status, message.Data1, message.Data2);
        }
        if (cursor < endFrame)
            Mix(synth, output[BufferIndex(cursor)..], fadeEnd - cursor, fadeFrames);

        int BufferIndex(long frame) => (int)((frame - firstFrame) * 2);
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
                destination[index] = Math.Clamp(destination[index] + samples[index] * gain, -1f, 1f);
                destination[index + 1] = Math.Clamp(destination[index + 1] + samples[index + 1] * gain, -1f, 1f);
                framesToFadeEnd--;
            }
            destination = destination[length..];
        }
    }

    private PlaybackLoop PrepareLoop(PlaybackPlan plan, LoopRange range)
    {
        long start = plan.TempoMap.TickToSample(range.StartTick, sampleRate);
        long end = plan.TempoMap.TickToSample(range.EndTick, sampleRate);
        if (end <= start)
            throw new ArgumentOutOfRangeException(nameof(range), "The loop must span at least one audio sample.");

        var events = new List<PlaybackEvent>();
        var held = new PlaybackEvent?[Channels * Keys];
        var setup = new Dictionary<(byte Status, byte Key), PlaybackEvent>();
        foreach (var message in plan.Events)
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

            int channelStart = (message.Status & 0x0f) * Keys;
            if (message.Command == 0x80)
                held[channelStart + message.Data1] = null;
            else if (message.Command == 0xb0 && message.Data1 is 120 or 123)
                Array.Clear(held, channelStart, Keys);
        }

        var chased = setup.Values.OrderBy(message => message.Frame).ThenBy(message => message.Tick)
            .ThenBy(message => message.Order)
            .Concat(held.Where(message => message.HasValue).Select(message => message.GetValueOrDefault()))
            .ToArray();
        return new PlaybackLoop(range, start, end, events.ToArray(), chased);
    }

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

    internal sealed class PlaybackPlan(
        IMonitorSynth synth, TempoMap tempoMap, MeterMap meterMap, PlaybackEvent[] events, long endFrame, long endWithRelease)
    {
        public IMonitorSynth Synth { get; } = synth;
        public TempoMap TempoMap { get; } = tempoMap;
        public MeterMap MeterMap { get; } = meterMap;
        public PlaybackEvent[] Events { get; } = events;
        public long EndFrame { get; } = endFrame;
        public long EndWithRelease { get; } = endWithRelease;
        public PlaybackLoop? RequestedLoop;
        public OverdubReplay? Overdub;
        public long? OriginFrame;
        public long Tick;
    }

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
