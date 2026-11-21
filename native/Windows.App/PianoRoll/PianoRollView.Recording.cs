using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Signal.Native.Engine;
using Windows.Foundation;

namespace Signal.Native.Windows.App.PianoRoll;

public sealed partial class PianoRollView
{
    private PianoRollDocument? _recordingDocument;
    private long _takeExtentTick;
    private int _displayedNoteCount = -1;
    private int _recordingPreviewCount;
    private Rect? _recordingWindow;
    private readonly HashSet<(long Start, long Length, byte Pitch)> _recordingPreviewNotes = [];

    private void RefreshNoteCount()
    {
        int count = _document?.Notes.Count ?? 0;
        if (_recordingDocument is not null && ReferenceEquals(_document, _recordingDocument))
            count += _recordingPreviewCount + HeldLayer.Children.Count;
        if (count != _displayedNoteCount)
        {
            NoteCountText.Text = count == 1 ? "1 note" : $"{count} notes";
            _displayedNoteCount = count;
        }
    }

    public void BeginRecordingPreview(PianoRollDocument document)
    {
        RecordedLayer.Children.Clear();
        HeldLayer.Children.Clear();
        _recordingPreviewNotes.Clear();
        _recordingPreviewCount = 0;
        _recordingWindow = null;
        _recordingDocument = document;
        UpdateRecordingLayerVisibility();
        RefreshViewport();
    }

    private void UpdateRecordingLayerVisibility()
    {
        bool visible = _recordingDocument is not null && ReferenceEquals(_document, _recordingDocument);
        RecordedLayer.Visibility = HeldLayer.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        RefreshNoteCount();
    }

    public void AppendRecordingPreview(SongNote note)
    {
        if (_recordingDocument is null)
            return;

        _recordingPreviewCount++;
        // Repeated overdubs can put many identical shapes on one drum lane. Draw it
        // once while preserving every recorded note in the take and in the count.
        if (_recordingPreviewNotes.Add((note.StartTick, note.LengthTicks, note.NoteNumber)) &&
            _recordingWindow is { } window && NoteIsInWindow(window, note.StartTick, note.LengthTicks, note.NoteNumber))
            PositionRecordingRectangle(CreateRecordingRectangle(RecordedLayer),
                note.StartTick, note.LengthTicks, note.NoteNumber);
        ExtendTakeTimeline(note.StartTick + note.LengthTicks);
    }

    private void RefreshRecordingViewport(double left, double top, double width, double height)
    {
        if (_recordingDocument is null || (_recordingWindow is { } current && current.Left <= left &&
            current.Right >= left + width && current.Top <= top && current.Bottom >= top + height))
            return;

        double firstColumn = Math.Max(0, left - width);
        double firstRow = Math.Max(0, top - RowHeight * 2);
        var window = new Rect(firstColumn, firstRow, left + width * 2 - firstColumn, top + height + RowHeight * 2 - firstRow);
        _recordingWindow = window;
        int used = 0;
        foreach (var (start, length, pitch) in _recordingPreviewNotes)
        {
            if (!NoteIsInWindow(window, start, length, pitch))
                continue;
            var rect = used < RecordedLayer.Children.Count ? (Rectangle)RecordedLayer.Children[used] :
                CreateRecordingRectangle(RecordedLayer);
            PositionRecordingRectangle(rect, start, length, pitch);
            used++;
        }
        while (RecordedLayer.Children.Count > used)
            RecordedLayer.Children.RemoveAt(RecordedLayer.Children.Count - 1);
    }

    public void SetHeldRecordingNotes(ReadOnlySpan<HeldRecordingNote> held)
    {
        if (_recordingDocument is null || HeldLayer.Visibility != Visibility.Visible)
            return;

        // Reuse the existing shapes. Only the geometry of currently held notes changes each frame.
        for (int i = 0; i < held.Length; i++)
        {
            var note = held[i];
            var rect = i < HeldLayer.Children.Count ? (Rectangle)HeldLayer.Children[i] : CreateRecordingRectangle(HeldLayer);
            PositionRecordingRectangle(rect, note.StartTick, note.LengthTicks, note.NoteNumber);
        }
        while (HeldLayer.Children.Count > held.Length)
            HeldLayer.Children.RemoveAt(HeldLayer.Children.Count - 1);
        RefreshNoteCount();
    }

    public void EndRecordingPreview()
    {
        _recordingDocument = null;
        RecordedLayer.Children.Clear();
        HeldLayer.Children.Clear();
        _recordingPreviewNotes.Clear();
        _recordingPreviewCount = 0;
        _recordingWindow = null;
        UpdateRecordingLayerVisibility();
        // Keep the visible part of a long take available if recording stopped during a rest.
        long visibleEnd = long.CreateSaturating(Math.Ceiling(
            (EditorScroll.HorizontalOffset + EditorScroll.ViewportWidth) * _ticksPerBeat / BeatWidth));
        _takeExtentTick = Math.Min(_takeExtentTick, visibleEnd);
        UpdateSurfaceExtent();
    }

    private void ExtendTakeTimeline(long tick)
    {
        double page = Math.Max(EditorScroll.ViewportWidth, BeatWidth);
        if (TickX(tick) + page / 2 < Surface.Width)
            return;

        long pageTicks = long.CreateSaturating(Math.Ceiling(page * _ticksPerBeat / BeatWidth));
        _takeExtentTick = long.CreateSaturating((Int128)tick + pageTicks);
        UpdateSurfaceExtent();
    }

    private Rectangle CreateRecordingRectangle(Canvas layer)
    {
        var rect = new Rectangle { Height = RowHeight - 4, RadiusX = 3, RadiusY = 3, Fill = _palette.Playhead };
        layer.Children.Add(rect);
        return rect;
    }

    private void PositionRecordingRectangle(Rectangle rect, long start, long length, byte pitch)
    {
        Canvas.SetLeft(rect, TickX(start));
        Canvas.SetTop(rect, PitchY(pitch) + 2);
        rect.Width = NoteWidth(length);
    }

    private void RecolorRecordingLayers()
    {
        foreach (Rectangle rect in RecordedLayer.Children)
            rect.Fill = _palette.Playhead;
        foreach (Rectangle rect in HeldLayer.Children)
            rect.Fill = _palette.Playhead;
    }
}
