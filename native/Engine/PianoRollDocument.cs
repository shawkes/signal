namespace Signal.Native.Engine;

public readonly record struct PianoRollNote(long Id, SongNote Note);

/// <summary>Single-track note editing and undo on the caller's thread.</summary>
public sealed class PianoRollDocument
{
    private readonly List<PianoRollNote> _notes = [];
    private readonly Stack<(Action Revert, long Revision)> _undo = new();
    private long _nextId = 1;
    private long _nextRevision;

    public PianoRollDocument() : this([]) { }

    public PianoRollDocument(IEnumerable<SongNote> notes)
    {
        Notes = _notes.AsReadOnly();
        AppendNotes(notes.ToArray(), recordUndo: false);
    }

    public IReadOnlyList<PianoRollNote> Notes { get; }
    public long Revision { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public event Action? Changed;

    public long Add(SongNote note) => AppendNotes([note]);

    /// <summary>Appends a take as one undoable edit.</summary>
    public void Append(IEnumerable<SongNote> notes) => AppendNotes(notes.ToArray());

    public bool Move(long id, long startTick, byte noteNumber)
    {
        int index = IndexOf(id);
        return index >= 0 && Replace(index, _notes[index].Note with { StartTick = startTick, NoteNumber = noteNumber });
    }

    public bool Resize(long id, long startTick, long lengthTicks)
    {
        int index = IndexOf(id);
        return index >= 0 && Replace(index, _notes[index].Note with { StartTick = startTick, LengthTicks = lengthTicks });
    }

    public bool Delete(long id)
    {
        int index = IndexOf(id);
        if (index < 0)
            return false;

        var previous = _notes[index];
        _notes.RemoveAt(index);
        Commit(() => _notes.Insert(index, previous));
        return true;
    }

    public bool Undo()
    {
        if (!_undo.TryPop(out var edit))
            return false;

        edit.Revert();
        Revision = edit.Revision;
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<SongNote> CreatePlaybackSnapshot() =>
        Array.AsReadOnly(_notes.Select(entry => entry.Note).ToArray());

    private long AppendNotes(ReadOnlySpan<SongNote> notes, bool recordUndo = true)
    {
        if (notes.IsEmpty)
            return 0;

        foreach (var note in notes)
            Validate(note);

        int index = _notes.Count;
        int count = notes.Length;
        long firstId = _nextId;
        _nextId = checked(_nextId + count);
        for (int i = 0; i < count; i++)
            _notes.Add(new PianoRollNote(firstId + i, notes[i]));

        if (recordUndo)
            Commit(() => _notes.RemoveRange(index, count));
        return firstId;
    }

    private int IndexOf(long id) => _notes.FindIndex(entry => entry.Id == id);

    private bool Replace(int index, SongNote note)
    {
        Validate(note);
        var previous = _notes[index];
        if (previous.Note == note)
            return false;

        _notes[index] = previous with { Note = note };
        Commit(() => _notes[index] = previous);
        return true;
    }

    private void Commit(Action revert)
    {
        _undo.Push((revert, Revision));
        Revision = ++_nextRevision;
        Changed?.Invoke();
    }

    private static void Validate(SongNote note)
    {
        if (note.StartTick < 0 || note.LengthTicks < 0 || note.StartTick > long.MaxValue - note.LengthTicks
            || note.Channel > 15 || note.NoteNumber > 127 || note.Velocity is 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(note), "A note needs a valid tick range, MIDI channel, pitch, and nonzero velocity.");
    }
}
