namespace Signal.Native.Engine;

/// <summary>Plays a prepared snapshot on the audio thread without changing live MIDI state.</summary>
internal sealed class NotePlayback(TempoMap tempo, int sampleRate)
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

    public bool Start(IEnumerable<SongNote> notes, IMonitorSynth synth)
    {
        ArgumentNullException.ThrowIfNull(notes);
        ArgumentNullException.ThrowIfNull(synth);
        var events = new List<NoteEvent>();
        foreach (var note in notes)
        {
            if (note.StartTick < 0 || note.LengthTicks < 0 || note.Channel >= Channels ||
                note.NoteNumber >= Keys || note.Velocity is 0 or >= 128)
            {
                throw new ArgumentOutOfRangeException(nameof(notes), "Notes require valid MIDI data and nonnegative time.");
            }

            long start = tempo.TickToSample(note.StartTick, sampleRate);
            long end = tempo.TickToSample(checked(note.StartTick + note.LengthTicks), sampleRate);
            events.Add(new NoteEvent(start, note.Channel, note.NoteNumber, note.Velocity));
            events.Add(new NoteEvent(Math.Max(checked(start + 1), end), note.Channel, note.NoteNumber, 0));
        }
        if (events.Count == 0)
            return false;

        events.Sort(static (left, right) =>
        {
            int order = left.Frame.CompareTo(right.Frame);
            // A release must precede a retrigger at the same sample.
            return order != 0 ? order : left.Velocity.CompareTo(right.Velocity);
        });
        MergeOverlappingReleases(events);
        long endWithRelease = checked(events[^1].Frame + sampleRate);
        Volatile.Write(ref _requested, new PlaybackPlan(synth, events.ToArray(), endWithRelease));
        return true;
    }

    public void Stop() => Volatile.Write(ref _requested, null);

    /// <returns>The start frame of the playback metronome grid, when the note sequence is playing.</returns>
    public long? Render(Span<float> output, long firstFrame)
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
        long? clickOrigin = _nextEvent < active.Events.Length ? _startFrame : null;
        long fadeEnd = active.EndWithRelease;
        while (_nextEvent < active.Events.Length && active.Events[_nextEvent].Frame < relativeEnd)
        {
            var note = active.Events[_nextEvent++];
            if (note.Frame > cursor)
            {
                Mix(active.Synth, output[BufferIndex(cursor)..BufferIndex(note.Frame)], fadeEnd - cursor);
                cursor = note.Frame;
            }
            byte status = (byte)((note.Velocity == 0 ? 0x80 : 0x90) | note.Channel);
            active.Synth.ProcessMidi(status, note.Key, note.Velocity);
        }
        if (cursor < relativeEnd)
            Mix(active.Synth, output[BufferIndex(cursor)..BufferIndex(relativeEnd)], fadeEnd - cursor);

        long tick = _nextEvent == active.Events.Length ? Finished : (long)tempo.SampleToTick(relativeEnd, sampleRate);
        Volatile.Write(ref active.Tick, tick);
        if (relativeEnd >= active.EndWithRelease)
        {
            Interlocked.CompareExchange(ref _requested, null, active);
            _active = null;
        }
        return clickOrigin;

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

    private static void MergeOverlappingReleases(List<NoteEvent> events)
    {
        // A MIDI note-off releases all voices on that pitch and channel in MeltySynth.
        // Keep retriggers, and release only when the last overlapping note ends.
        var held = new int[Channels * Keys];
        int written = 0;
        for (int index = 0; index < events.Count; index++)
        {
            var note = events[index];
            int key = note.Channel * Keys + note.Key;
            if (note.Velocity != 0)
                held[key]++;
            else if (--held[key] != 0)
                continue;

            events[written++] = note;
        }
        events.RemoveRange(written, events.Count - written);
    }

    private sealed class PlaybackPlan(IMonitorSynth synth, NoteEvent[] events, long endWithRelease)
    {
        public IMonitorSynth Synth { get; } = synth;
        public NoteEvent[] Events { get; } = events;
        public long EndWithRelease { get; } = endWithRelease;
        public long Tick;
    }

    private readonly record struct NoteEvent(long Frame, byte Channel, byte Key, byte Velocity);
}
