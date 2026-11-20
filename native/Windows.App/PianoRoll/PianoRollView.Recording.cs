using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Signal.Native.Engine;

namespace Signal.Native.Windows.App.PianoRoll;

public sealed partial class PianoRollView
{
    private PianoRollDocument? _recordingDocument;
    private long _takeExtentTick;
    private int _displayedNoteCount = -1;

    private void RefreshNoteCount()
    {
        int count = _document?.Notes.Count ?? 0;
        if (_recordingDocument is not null && ReferenceEquals(_document, _recordingDocument))
            count += RecordedLayer.Children.Count + HeldLayer.Children.Count;
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
        _recordingDocument = document;
        UpdateRecordingLayerVisibility();
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

        var rect = CreateRecordingRectangle(RecordedLayer);
        PositionRecordingRectangle(rect, note.StartTick, note.LengthTicks, note.NoteNumber);
        ExtendTakeTimeline(note.StartTick + note.LengthTicks);
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
