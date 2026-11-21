namespace Signal.Native.Engine;

/// <summary>Replays completed overdub notes on their own synth during a loop take.</summary>
internal sealed class OverdubReplay(
    TempoMap tempo, int sampleRate, long loopStartFrame, long loopEndFrame,
    IMonitorSynth synth, IReadOnlyList<SongChannelMessage> setup)
{
    private const int NoteCapacity = 2048;
    private const int KeysPerChannel = 128;
    private readonly ReplayNote[] _notes = new ReplayNote[NoteCapacity];
    private readonly Edge[] _edges = new Edge[2 * NoteCapacity];
    private readonly bool[] _sounding = new bool[NoteCapacity];
    private readonly int[] _held = new int[16 * KeysPerChannel];
    private readonly SongChannelMessage[] _setup = setup.ToArray();
    private readonly float[] _samples = new float[512];
    private readonly int _fadeFrames = (int)Math.Min(Math.Max(1, sampleRate / 500),
        Math.Max(1, (loopEndFrame - loopStartFrame) / 2));
    private int _noteCount;
    private int _edgeCount;
    private long _firstPlayablePass = long.MaxValue;
    private long _pass = -1;
    private long _nextPosition;
    private bool _synthReady;

    public void AddNote(RecordedNote note, long completedPass)
    {
        if (_noteCount == _notes.Length)
            return;

        long start = Math.Clamp(tempo.TickToSample(note.StartTick, sampleRate), loopStartFrame, loopEndFrame - 1);
        long end = Math.Clamp(tempo.TickToSample(note.StartTick + note.LengthTicks, sampleRate),
            start + 1, loopEndFrame);
        // A receipt may reach us after the next pass starts rendering. In that case start
        // replaying the note on the following pass so it never appears partway through a pass.
        long firstPass = Math.Max(_pass, completedPass) + 1;
        _firstPlayablePass = Math.Min(_firstPlayablePass, firstPass);
        int slot = _noteCount++;
        _notes[slot] = new ReplayNote(start, end, firstPass, note.Channel, note.NoteNumber, note.Velocity);
        InsertEdge(new Edge(start, slot, Attack: true));
        InsertEdge(new Edge(end, slot, Attack: false));
    }

    /// <summary>Mixes at the backing loop's position for a segment wholly within one pass.</summary>
    public void Render(Span<float> output, long pass, long position)
    {
        if (output.IsEmpty)
            return;

        bool discontinuity = pass != _pass || position != _nextPosition;
        long end = position + output.Length / 2;
        _pass = pass;
        _nextPosition = end;
        if (pass < _firstPlayablePass)
            return;

        if (discontinuity || !_synthReady)
            PrepareSynth(pass, position);

        long cursor = position;
        for (int index = EdgeAtOrAfter(position); index < _edgeCount && _edges[index].Frame < end; index++)
        {
            var edge = _edges[index];
            var note = _notes[edge.Slot];
            if (note.FirstPass > pass || (!edge.Attack && !_sounding[edge.Slot]))
                continue;

            if (edge.Frame > cursor)
            {
                Mix(output[(int)((cursor - position) * 2)..(int)((edge.Frame - position) * 2)], cursor);
                cursor = edge.Frame;
            }

            if (edge.Attack)
            {
                Attack(edge.Slot, note);
            }
            else
            {
                _sounding[edge.Slot] = false;
                if (--_held[note.Key] == 0)
                    synth.ProcessMidi((byte)(0x80 | note.Channel), note.Pitch, 0);
            }
        }

        if (cursor < end)
            Mix(output[(int)((cursor - position) * 2)..], cursor);
    }

    public void Stop()
    {
        if (_synthReady)
        {
            synth.Reset();
            _synthReady = false;
        }
    }

    private void PrepareSynth(long pass, long position)
    {
        synth.Reset();
        foreach (var message in _setup)
            synth.ProcessMidi(message.Status, message.Data1, message.Data2);
        _synthReady = true;
        Array.Clear(_sounding);
        Array.Clear(_held);

        // Normally we begin at the loop start. After a skipped audio span, restore notes
        // that should still be held at the first writable frame.
        if (position == loopStartFrame)
            return;

        for (int slot = 0; slot < _noteCount; slot++)
        {
            var note = _notes[slot];
            if (note.Channel != SongTrackEditor.DrumChannel && note.FirstPass <= pass &&
                note.StartFrame < position && position < note.EndFrame)
                Attack(slot, note);
        }
    }

    private void Attack(int slot, ReplayNote note)
    {
        _sounding[slot] = true;
        _held[note.Key]++;
        synth.ProcessMidi((byte)(0x90 | note.Channel), note.Pitch, note.Velocity);
    }

    private void Mix(Span<float> output, long position)
    {
        while (!output.IsEmpty)
        {
            int length = Math.Min(output.Length, _samples.Length);
            var scratch = _samples.AsSpan(0, length);
            synth.Render(scratch);
            for (int index = 0; index < length; index += 2, position++)
            {
                float gain = Math.Min(1, (loopEndFrame - position) / (float)_fadeFrames);
                output[index] = Math.Clamp(output[index] + scratch[index] * gain, -1, 1);
                output[index + 1] = Math.Clamp(output[index + 1] + scratch[index + 1] * gain, -1, 1);
            }
            output = output[length..];
        }
    }

    private void InsertEdge(Edge edge)
    {
        int index = EdgeAtOrAfter(edge.Frame);
        // At the same frame release the old note before retriggering its pitch.
        while (index < _edgeCount && _edges[index].Frame == edge.Frame && (edge.Attack || !_edges[index].Attack))
            index++;
        _edges.AsSpan(index, _edgeCount - index).CopyTo(_edges.AsSpan(index + 1));
        _edges[index] = edge;
        _edgeCount++;
    }

    private int EdgeAtOrAfter(long frame)
    {
        int low = 0;
        int high = _edgeCount;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (_edges[middle].Frame < frame)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private readonly record struct Edge(long Frame, int Slot, bool Attack);

    private readonly record struct ReplayNote(long StartFrame, long EndFrame, long FirstPass,
        byte Channel, byte Pitch, byte Velocity)
    {
        public int Key => Channel * KeysPerChannel + Pitch;
    }
}
