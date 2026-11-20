namespace Signal.Native.Engine;

internal readonly record struct PlaybackGrid(long Origin, TempoMap TempoMap);

/// <summary>Plays a prepared snapshot on the audio thread without changing live MIDI state.</summary>
internal sealed class NotePlayback(int sampleRate)
{
    private const long Finished = -1;
    private const int Channels = 16;
    private const int Keys = 128;
    private readonly float[] _scratch = new float[512];
    private readonly int _fadeFrames = Math.Max(1, sampleRate / 100);
    private PlaybackPlan? _requested;
    private PlaybackPlan? _active;
    private IMonitorSynth? _fadingSynth;
    private int _fadeRemaining;
    private int _nextEvent;
    private long _startFrame;

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

    public bool Start(IEnumerable<SongNote> notes, IEnumerable<SongChannelMessage> messages,
        TempoMap tempo, IMonitorSynth synth, long endTick = 0)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(synth);
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
        if (events.Count == 0)
            return false;

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
        long endFrame = Math.Max(events[^1].Frame, tempo.TickToSample(endTick, sampleRate));
        long endWithRelease = checked(endFrame + sampleRate);
        Volatile.Write(ref _requested, new PlaybackPlan(synth, tempo, events.ToArray(), endFrame, endWithRelease));
        return true;
    }

    public void Stop() => Volatile.Write(ref _requested, null);

    /// <returns>The playback metronome grid when the song is playing.</returns>
    public PlaybackGrid? Render(Span<float> output, long firstFrame)
    {
        var requested = Volatile.Read(ref _requested);
        if (!ReferenceEquals(requested, _active))
        {
            if (_active is { } previous)
            {
                _fadingSynth = previous.Synth;
                _fadeRemaining = _fadeFrames;
            }
            _active = requested;
            _startFrame = firstFrame;
            _nextEvent = 0;
        }

        if (_fadingSynth is { } fading)
        {
            int frames = Math.Min(_fadeRemaining, output.Length / 2);
            Mix(fading, output[..(2 * frames)], _fadeRemaining);
            _fadeRemaining -= frames;
            if (_fadeRemaining == 0)
                _fadingSynth = null;
        }

        if (_active is not { } active)
            return null;

        long relativeFirst = firstFrame - _startFrame;
        long relativeEnd = Math.Min(relativeFirst + output.Length / 2, active.EndWithRelease);
        long cursor = relativeFirst;
        PlaybackGrid? grid = _nextEvent < active.Events.Length || relativeFirst < active.EndFrame
            ? new PlaybackGrid(_startFrame, active.TempoMap)
            : null;
        long fadeEnd = active.EndWithRelease;
        while (_nextEvent < active.Events.Length && active.Events[_nextEvent].Frame < relativeEnd)
        {
            var message = active.Events[_nextEvent++];
            if (message.Frame > cursor)
            {
                Mix(active.Synth, output[BufferIndex(cursor)..BufferIndex(message.Frame)], fadeEnd - cursor);
                cursor = message.Frame;
            }
            active.Synth.ProcessMidi(message.Status, message.Data1, message.Data2);
        }
        if (cursor < relativeEnd)
            Mix(active.Synth, output[BufferIndex(cursor)..BufferIndex(relativeEnd)], fadeEnd - cursor);

        long tick = _nextEvent == active.Events.Length && relativeEnd >= active.EndFrame
            ? Finished
            : (long)active.TempoMap.SampleToTick(relativeEnd, sampleRate);
        Volatile.Write(ref active.Tick, tick);
        if (relativeEnd >= active.EndWithRelease)
        {
            Interlocked.CompareExchange(ref _requested, null, active);
            _active = null;
        }
        return grid;

        int BufferIndex(long frame) => (int)((frame - relativeFirst) * 2);
    }

    private void Mix(IMonitorSynth synth, Span<float> destination, long framesToFadeEnd)
    {
        while (!destination.IsEmpty)
        {
            int length = Math.Min(destination.Length, _scratch.Length);
            var samples = _scratch.AsSpan(0, length);
            synth.Render(samples);
            for (int index = 0; index < length; index += 2)
            {
                float gain = framesToFadeEnd >= _fadeFrames ? 1f : framesToFadeEnd / (float)_fadeFrames;
                destination[index] = Math.Clamp(destination[index] + samples[index] * gain, -1f, 1f);
                destination[index + 1] = Math.Clamp(destination[index + 1] + samples[index + 1] * gain, -1f, 1f);
                framesToFadeEnd--;
            }
            destination = destination[length..];
        }
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

    private sealed class PlaybackPlan(
        IMonitorSynth synth, TempoMap tempoMap, PlaybackEvent[] events, long endFrame, long endWithRelease)
    {
        public IMonitorSynth Synth { get; } = synth;
        public TempoMap TempoMap { get; } = tempoMap;
        public PlaybackEvent[] Events { get; } = events;
        public long EndFrame { get; } = endFrame;
        public long EndWithRelease { get; } = endWithRelease;
        public long Tick;
    }

    private readonly record struct PlaybackEvent(long Frame, long Tick, byte Status, byte Data1, byte Data2, int Order)
    {
        public int Command => Status & 0xf0;
        public int Priority => Command switch { 0x80 => 0, 0x90 => 2, _ => 1 };
    }
}
