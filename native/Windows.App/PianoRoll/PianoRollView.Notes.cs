using Microsoft.UI.Xaml.Controls;
using Signal.Native.Engine;
using Windows.Foundation;

namespace Signal.Native.Windows.App.PianoRoll;

public sealed partial class PianoRollView
{
    private const double MinimumNoteWidth = 3;
    private PianoRollNote[] _notesByStart = [];
    private long[] _latestEndByStart = [];
    private readonly List<PianoRollNote> _notesInTile = [];
    private byte? _drumHoverPitch;

    private void IndexDocumentNotes()
    {
        _notesByStart = _document?.Notes.OrderBy(item => item.Note.StartTick).ToArray() ?? [];
        _latestEndByStart = new long[_notesByStart.Length];
        long latest = 0;
        for (int i = 0; i < _notesByStart.Length; i++)
        {
            var note = _notesByStart[i].Note;
            latest = Math.Max(latest, note.StartTick + note.LengthTicks);
            _latestEndByStart[i] = latest;
        }
    }

    private int FirstNoteReaching(double left)
    {
        // Prefix maxima also include long notes that start well before the tile.
        int low = 0;
        int high = _latestEndByStart.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (TickX(_latestEndByStart[middle]) + MinimumNoteWidth <= left)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private bool NoteIsInWindow(Rect window, long tick, long length, byte pitch)
    {
        double x = TickX(tick);
        double y = PitchY(pitch) + 2;
        return x < window.Right && x + NoteWidth(length) > window.Left &&
            y < window.Bottom && y + RowHeight - 4 > window.Top;
    }

    private void CollectVisibleNotes(Rect tile)
    {
        _notesInTile.Clear();
        for (int index = FirstNoteReaching(tile.Left); index < _notesByStart.Length; index++)
        {
            var item = _notesByStart[index];
            if (TickX(item.Note.StartTick) >= tile.Right)
                break;
            if (_grabbedNote is null || !_selectedIds.Contains(item.Id))
                IncludeIfVisible(item);
        }
        foreach (var preview in _previewNotes)
            IncludeIfVisible(preview);

        // Keep the document's stacking order for overlapping notes.
        _notesInTile.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        void IncludeIfVisible(PianoRollNote item)
        {
            var note = item.Note;
            if (NoteIsInWindow(tile, note.StartTick, note.LengthTicks, note.NoteNumber))
                _notesInTile.Add(item);
        }
    }

    private void ShowDrumHoverTip(byte? pitch)
    {
        if (_drumHoverPitch == pitch)
            return;
        _drumHoverPitch = pitch;
        ToolTipService.SetToolTip(Surface, pitch is byte drum ? $"{Caption(drum, percussion: true)} · MIDI note {drum}" : null);
    }

    partial void InitializeScoreDrawing();
    partial void InvalidateScore();
    partial void UpdateScoreViewport(double left, double top, double width, double height);
    partial void ReleaseScoreResources();
}
