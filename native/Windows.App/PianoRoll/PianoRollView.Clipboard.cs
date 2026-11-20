using Signal.Native.Engine;

namespace Signal.Native.Windows.App.PianoRoll;

public sealed partial class PianoRollView
{
    private (SongNote[] Notes, int TicksPerQuarter)? _clipboard;

    private bool CanPaste => _editingEnabled && _document is not null && _clipboard is not null;

    private bool CopySelection()
    {
        var selection = SelectedNotes();
        if (selection.Length == 0)
            return false;

        long firstTick = selection.Min(item => item.Note.StartTick);
        var notes = new SongNote[selection.Length];
        for (int i = 0; i < selection.Length; i++)
        {
            var note = selection[i].Note;
            notes[i] = note with { StartTick = note.StartTick - firstTick };
        }

        _clipboard = (notes, _ticksPerBeat);
        RefreshSelection();
        return true;
    }

    private bool PasteSelection()
    {
        if (!_editingEnabled || _document is not { } document || _clipboard is not { } clipboard)
            return false;

        var notes = new SongNote[clipboard.Notes.Length];
        for (int i = 0; i < notes.Length; i++)
        {
            var note = clipboard.Notes[i];
            Int128 relativeStart = ScaleTick(note.StartTick);
            Int128 start = _entryTick + relativeStart;
            Int128 end = _entryTick + ScaleTick(note.StartTick + note.LengthTicks);
            if (note.LengthTicks > 0)
                end = Int128.Max(start + 1, end);

            if (end > long.MaxValue)
                return false;

            notes[i] = note with { StartTick = (long)start, LengthTicks = (long)(end - start), Channel = _defaultChannel };
        }

        long firstId = document.AddRange(notes);
        SelectAddedNotes(firstId, notes.Length);
        return true;

        // Round positions independently so adjacent source notes keep a shared boundary.
        Int128 ScaleTick(long tick) => ((Int128)tick * _ticksPerBeat + clipboard.TicksPerQuarter / 2) / clipboard.TicksPerQuarter;
    }
}
