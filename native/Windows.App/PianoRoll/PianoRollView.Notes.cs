using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signal.Native.Engine;
using Windows.Foundation;
using Windows.UI;

namespace Signal.Native.Windows.App.PianoRoll;

public sealed partial class PianoRollView
{
    private const double MinimumNoteWidth = 3;
    private PianoRollNote[] _notesByStart = [];
    private long[] _latestEndByStart = [];
    private readonly List<PianoRollNote> _notesInTile = [];
    private CanvasTextFormat? _noteText;
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

    private void InvalidateScore()
    {
        if (ScoreDrawing?.ReadyToDraw == true)
            ScoreDrawing.Invalidate();
    }

    private void DrawScore(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        foreach (var tile in args.InvalidatedRegions)
        {
            using var drawing = sender.CreateDrawingSession(tile);
            DrawBeatGrid(drawing, tile);
            DrawNotes(drawing, tile);
        }
    }

    private void DrawBeatGrid(CanvasDrawingSession drawing, Rect tile)
    {
        drawing.Antialiasing = CanvasAntialiasing.Aliased;
        var minor = BrushColor(_palette.MinorLine);
        var beat = BrushColor(_palette.BeatLine);
        var major = BrushColor(_palette.MajorLine);
        long lastCell = CellAtPosition(tile.Right);
        for (long cell = CellAtPosition(tile.Left);; cell++)
        {
            PlaceLine(TickOfCell(cell), 1, minor);
            if (cell >= lastCell)
                break;
        }

        long firstTick = long.CreateSaturating(Math.Floor(tile.Left / BeatWidth * _ticksPerBeat));
        long lastTick = long.CreateSaturating(Math.Ceiling(tile.Right / BeatWidth * _ticksPerBeat));
        for (long tick = _meterMap.PositionAt(firstTick).BeatTick; tick <= lastTick;)
        {
            bool bar = tick == _meterMap.PositionAt(tick).BarTick;
            PlaceLine(tick, bar ? 2 : 1, bar ? major : beat);
            long next = _meterMap.NextBeatTick(tick);
            if (next <= tick)
                break;
            tick = next;
        }

        void PlaceLine(long tick, float width, Color color) =>
            drawing.FillRectangle((float)TickX(tick), (float)tile.Top, width, (float)tile.Height, color);
    }

    private void DrawNotes(CanvasDrawingSession drawing, Rect tile)
    {
        _notesInTile.Clear();
        for (int i = FirstNoteReaching(tile.Left); i < _notesByStart.Length; i++)
        {
            var item = _notesByStart[i];
            if (TickX(item.Note.StartTick) >= tile.Right)
                break;
            if (_grabbedNote is null || !_selectedIds.Contains(item.Id))
                IncludeIfVisible(item);
        }
        foreach (var preview in _previewNotes)
            IncludeIfVisible(preview);

        if (_notesInTile.Count == 0)
            return;

        // Keep the document's stacking order for overlapping notes and draw the selection on top.
        _notesInTile.Sort(static (a, b) => a.Id.CompareTo(b.Id));
        drawing.Antialiasing = CanvasAntialiasing.Antialiased;
        var fill = BrushColor(_palette.Note);
        var outline = BrushColor(_palette.NoteOutline);
        var handle = BrushColor(_palette.Handle);
        var text = BrushColor(_palette.NoteText);
        foreach (var item in _notesInTile)
        {
            if (!_selectedIds.Contains(item.Id))
                DrawNote(item.Note, fill, outline);
        }
        if (_selectedIds.Count > 0)
        {
            var selectedFill = BrushColor(_palette.SelectedNote);
            var selectedOutline = BrushColor(_palette.SelectedOutline);
            foreach (var item in _notesInTile)
            {
                if (_selectedIds.Contains(item.Id))
                    DrawNote(item.Note, selectedFill, selectedOutline);
            }
        }

        void IncludeIfVisible(PianoRollNote item)
        {
            var note = item.Note;
            if (NoteIsInWindow(tile, note.StartTick, note.LengthTicks, note.NoteNumber))
                _notesInTile.Add(item);
        }

        void DrawNote(SongNote note, Color body, Color edge)
        {
            float x = (float)TickX(note.StartTick);
            float y = (float)PitchY(note.NoteNumber) + 2;
            float width = (float)NoteWidth(note);
            const float height = (float)RowHeight - 4;
            drawing.FillRoundedRectangle(x, y, width, height, 4, 4, body);
            drawing.DrawRoundedRectangle(x + 0.5f, y + 0.5f, width - 1, height - 1, 3.5f, 3.5f, edge, 1);
            if (width >= 8)
                drawing.FillRoundedRectangle(x + width - 5, y + (height - 8) / 2, 2, 8, 1, 1, handle);
            if (width >= 42)
            {
                _noteText ??= new CanvasTextFormat
                {
                    FontFamily = "Segoe UI Semibold", FontSize = 11,
                    VerticalAlignment = CanvasVerticalAlignment.Center,
                    WordWrapping = CanvasWordWrapping.NoWrap, TrimmingGranularity = CanvasTextTrimmingGranularity.Character,
                    TrimmingSign = CanvasTrimmingSign.Ellipsis
                };
                drawing.DrawText(Caption(note.NoteNumber, note.Channel == SongTrackEditor.DrumChannel),
                    new Rect(x + 6, y, width - 13, height), text, _noteText);
            }
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

    private void ShowDrumHoverTip(byte? pitch)
    {
        if (_drumHoverPitch == pitch)
            return;
        _drumHoverPitch = pitch;
        ToolTipService.SetToolTip(Surface, pitch is byte drum ? $"{Caption(drum, percussion: true)} · MIDI note {drum}" : null);
    }

    private static Color BrushColor(Brush brush) => ((SolidColorBrush)brush).Color;
}
