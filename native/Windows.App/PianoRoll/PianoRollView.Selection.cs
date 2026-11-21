using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Signal.Native.Engine;
using Windows.Foundation;
using Windows.System;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace Signal.Native.Windows.App.PianoRoll;

public sealed partial class PianoRollView
{
    private HashSet<long> _selectedIds = [];
    private PianoRollNote? _grabbedNote;
    private PianoRollNote[] _dragNotes = [];
    private PianoRollNote[] _previewNotes = [];
    private bool _resizeDrag;
    private bool _addToSelection;
    private bool _positionOnly;
    private Point _editPointer;
    private bool _editDragStarted;

    public event Action<SongNote>? NotePreviewRequested;

    internal bool HasNoteSelection => _selectedIds.Count > 0;
    internal bool HasActiveGesture => _pointerId is not null || _rulerPointerId is not null ||
        _tempoPointerId is not null || _tempoMarkerFlyout is not null || _automationPointerId is not null;

    private void RefreshSelection()
    {
        RefreshNoteCount();
        AddButton.IsEnabled = _editingEnabled && _document is not null;
        CutButton.IsEnabled = CopyButton.IsEnabled = DeleteButton.IsEnabled = _editingEnabled && _selectedIds.Count > 0;
        PasteButton.IsEnabled = CanPaste;
        UndoButton.IsEnabled = _editingEnabled && _document?.CanUndo is true;
        GridSizeBox.IsEnabled = _editingEnabled;
        var velocities = SelectedNotes().Select(item => item.Note.Velocity).Distinct().Take(2).ToArray();
        VelocityButton.Content = velocities.Length switch
        {
            1 => $"Velocity {velocities[0]}",
            2 => "Velocity · Mixed",
            _ => "Velocity"
        };
        if (_showVelocityLane)
            PaintVelocityLane();

        SelectionText.Text = _selectedIds.Count switch
        {
            > 1 => $"{_selectedIds.Count} notes selected",
            1 => DescribeNote(_previewNotes.Length == 1 ? _previewNotes[0].Note :
                _document!.Notes.First(item => _selectedIds.Contains(item.Id)).Note),
            _ => CanPaste ? $"Paste at tick {_entryTick}" : string.Empty
        };
    }

    private string DescribeNote(SongNote note)
    {
        var position = _meterMap.PositionAt(note.StartTick);
        string beat = $"{position.Bar}.{position.Beat}";
        return $"{Caption(note.NoteNumber, note.Channel == SongTrackEditor.DrumChannel)} · {beat} · {note.LengthTicks} ticks";
    }

    private PianoRollNote[] SelectedNotes() => _document is null || _selectedIds.Count == 0
        ? []
        : _document.Notes.Where(item => _selectedIds.Contains(item.Id)).ToArray();

    private void SetSelection(HashSet<long> ids)
    {
        if (!_selectedIds.SetEquals(ids))
        {
            _selectedIds = ids;
            InvalidateScore();
        }
        RefreshSelection();
    }

    private void SelectAddedNotes(long firstId, int count) =>
        SetSelection(Enumerable.Range(0, count).Select(index => firstId + index).ToHashSet());

    private void SetEntry(SongNote note)
    {
        _entryTick = note.StartTick;
        _entryPitch = note.NoteNumber;
    }

    private PianoRollNote? HitNote(Point point)
    {
        PianoRollNote? hit = null;
        bool hitSelected = false;
        for (int i = FirstNoteReaching(point.X); i < _notesByStart.Length; i++)
        {
            var item = _notesByStart[i];
            if (TickX(item.Note.StartTick) > point.X)
                break;
            if (!NoteContains(item.Note, point))
                continue;

            bool selected = _selectedIds.Contains(item.Id);
            if (hit is null || (selected && !hitSelected) || (selected == hitSelected && item.Id > hit.Value.Id))
            {
                hit = item;
                hitSelected = selected;
            }
        }
        return hit;
    }

    private void SurfacePointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!_editingEnabled || _document is null || _pointerId is not null || _rulerPointerId is not null || _tempoPointerId is not null)
            return;
        var point = args.GetCurrentPoint(Surface);
        if (!point.Properties.IsLeftButtonPressed || !IsWithinSurface(point.Position))
            return;

        Focus(FocusState.Pointer);
        var hit = HitNote(point.Position);
        bool control = args.KeyModifiers.HasFlag(VirtualKeyModifiers.Control);
        if (control && hit is { } toggle)
        {
            var ids = new HashSet<long>(_selectedIds);
            if (ids.Add(toggle.Id))
                SetEntry(toggle.Note);
            else
                ids.Remove(toggle.Id);
            SetSelection(ids);
            args.Handled = true;
            return;
        }

        if (!Surface.CapturePointer(args.Pointer))
            return;

        _pointerId = args.Pointer.PointerId;
        _press = point.Position;
        RememberEditPointer(point.Position);
        _addToSelection = control;
        _positionOnly = args.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        if (hit is { } item)
        {
            SetEntry(item.Note);
            if (!_selectedIds.Contains(item.Id))
                SetSelection([item.Id]);
            _grabbedNote = item;
            _dragNotes = SelectedNotes();
            _previewNotes = (PianoRollNote[])_dragNotes.Clone();
            double width = NoteWidth(item.Note);
            _resizeDrag = _press.X >= TickX(item.Note.StartTick) + width - Math.Min(7, width / 2);
            RefreshSelection();
        }
        args.Handled = true;
    }

    private void SurfacePointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId != args.Pointer.PointerId)
        {
            if (_pointerId is null && (ShowsDrumLanes || _showVelocityLane))
            {
                var hit = HitNote(args.GetCurrentPoint(Surface).Position);
                if (ShowsDrumLanes)
                    ShowDrumHoverTip(hit?.Note.NoteNumber);
                SetVelocityHover(hit?.Id);
            }
            return;
        }
        var point = args.GetCurrentPoint(Surface).Position;
        RememberEditPointer(point);
        PreviewGesture(point);
        UpdateDragScroll();
        args.Handled = true;
    }

    private void SurfacePointerExited(object sender, PointerRoutedEventArgs args)
    {
        ShowDrumHoverTip(null);
        SetVelocityHover(null);
    }

    private void RememberEditPointer(Point point) => _editPointer = new(
        point.X - EditorScroll.HorizontalOffset, point.Y - EditorScroll.VerticalOffset);

    private Point EditPointerOnSurface() => new(
        _editPointer.X + EditorScroll.HorizontalOffset, _editPointer.Y + EditorScroll.VerticalOffset);

    private void PreviewGesture(Point point)
    {
        _editDragStarted |= Math.Abs(point.X - _press.X) >= 6 || Math.Abs(point.Y - _press.Y) >= 6;
        if (_grabbedNote is { } grabbed)
        {
            long cells = (long)Math.Round((point.X - _press.X) / CellWidth, MidpointRounding.AwayFromZero);
            int pitch = -(int)Math.Round((point.Y - _press.Y) / RowHeight, MidpointRounding.AwayFromZero);
            var previous = _previewNotes[0];
            TransformSelection(_dragNotes, _previewNotes, grabbed.Note, cells, pitch, _resizeDrag);
            if (_previewNotes[0] != previous)
            {
                InvalidateScore();
                RefreshSelection();
            }
            return;
        }

        SelectionBox.Visibility = _editDragStarted ? Visibility.Visible : Visibility.Collapsed;
        if (!_editDragStarted)
            return;

        var box = SelectionBounds(point);
        SelectionBox.Width = box.Width;
        SelectionBox.Height = box.Height;
        Canvas.SetLeft(SelectionBox, box.X);
        Canvas.SetTop(SelectionBox, box.Y);
    }

    private Rect SelectionBounds(Point point)
    {
        double x = Math.Clamp(point.X, 0, Surface.Width);
        double y = Math.Clamp(point.Y, 0, Surface.Height);
        return new Rect(Math.Min(x, _press.X), Math.Min(y, _press.Y),
            Math.Max(1, Math.Abs(x - _press.X)), Math.Max(1, Math.Abs(y - _press.Y)));
    }

    private void SurfacePointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId != args.Pointer.PointerId)
            return;

        var point = args.GetCurrentPoint(Surface).Position;
        PreviewGesture(point);
        var grabbed = _grabbedNote;
        var edited = _previewNotes;
        bool moving = !_resizeDrag;
        bool selectionDrag = SelectionBox.Visibility == Visibility.Visible;
        CancelGesture();
        Surface.ReleasePointerCapture(args.Pointer);
        args.Handled = true;

        if (grabbed is { } item)
        {
            if (_document!.ReplaceMany(edited))
            {
                var note = edited.First(note => note.Id == item.Id).Note;
                SetEntry(note);
                if (moving)
                    NotePreviewRequested?.Invoke(note);
            }
        }
        else if (selectionDrag)
        {
            SelectWithin(SelectionBounds(point));
        }
        else if (IsWithinSurface(point))
        {
            long cell = CellAtPosition(point.X);
            byte pitch = (byte)(PitchCount - 1 - (int)(point.Y / RowHeight));
            if (_positionOnly)
            {
                _entryTick = TickOfCell(cell);
                _entryPitch = pitch;
                SetSelection([]);
            }
            else if (!_addToSelection)
            {
                AddAt(cell, pitch);
            }
        }
    }

    private void SelectWithin(Rect box)
    {
        var ids = _addToSelection ? new HashSet<long>(_selectedIds) : [];
        foreach (var item in _document!.Notes)
        {
            double x = TickX(item.Note.StartTick);
            double y = PitchY(item.Note.NoteNumber) + 2;
            if (x < box.Right && box.Left < x + NoteWidth(item.Note) &&
                y < box.Bottom && box.Top < y + RowHeight - 4)
                ids.Add(item.Id);
        }
        SetSelection(ids);
        SetEntryFromSelection();
    }

    private void SurfacePointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId == args.Pointer.PointerId)
            CancelGesture();
    }

    private void CancelGesture()
    {
        if (_pointerId is not null)
            _dragScrollTimer?.Stop();
        bool hadPreview = _previewNotes.Length > 0;
        _pointerId = null;
        _editDragStarted = false;
        _grabbedNote = null;
        _dragNotes = [];
        _previewNotes = [];
        if (hadPreview)
            InvalidateScore();
        SelectionBox.Visibility = Visibility.Collapsed;
        RefreshSelection();
    }

    private void TransformSelection(PianoRollNote[] source, PianoRollNote[] destination, SongNote anchor,
        long cells, int pitch, bool resize)
    {
        long reference = resize ? anchor.StartTick + anchor.LengthTicks : anchor.StartTick;
        long tickDelta = MoveTick(reference, cells) - reference;
        long minDelta = long.MinValue;
        long maxDelta = long.MaxValue;
        int minPitch = -127;
        int maxPitch = 127;
        long gridStep = TickOfCell(1);
        foreach (var item in source)
        {
            var note = item.Note;
            long roomBefore = resize ? note.LengthTicks - Math.Min(note.LengthTicks, gridStep) : note.StartTick;
            minDelta = Math.Max(minDelta, -roomBefore);
            maxDelta = Math.Min(maxDelta, long.MaxValue - (note.StartTick + note.LengthTicks));
            minPitch = Math.Max(minPitch, -note.NoteNumber);
            maxPitch = Math.Min(maxPitch, 127 - note.NoteNumber);
        }
        tickDelta = Math.Clamp(tickDelta, minDelta, maxDelta);
        pitch = Math.Clamp(pitch, minPitch, maxPitch);
        for (int i = 0; i < source.Length; i++)
        {
            var item = source[i];
            destination[i] = item with
            {
                Note = resize
                    ? item.Note with { LengthTicks = item.Note.LengthTicks + tickDelta }
                    : item.Note with { StartTick = item.Note.StartTick + tickDelta, NoteNumber = (byte)(item.Note.NoteNumber + pitch) }
            };
        }
    }

    private void EditorKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_editingEnabled || _document is null || HasActiveGesture || GridSizeBox.FocusState != FocusState.Unfocused)
            return;

        bool control = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        switch (args.Key)
        {
            case VirtualKey.A when control:
                SetSelection(_document.Notes.Select(item => item.Id).ToHashSet());
                SetEntryFromSelection();
                break;
            case VirtualKey.C when control:
                CopySelection();
                break;
            case VirtualKey.X when control:
                CutSelection();
                break;
            case VirtualKey.V when control:
                PasteSelection();
                break;
            case VirtualKey.Z when control:
                UndoEdit();
                break;
            case VirtualKey.Escape:
                SetSelection([]);
                break;
            case VirtualKey.Delete:
                DeleteSelected();
                break;
            case VirtualKey.Insert:
                AddNext();
                break;
            case VirtualKey.Left or VirtualKey.Right when HasNoteSelection && ((!control && !shift) || (control && shift)):
            case VirtualKey.Up or VirtualKey.Down when !control:
                Nudge(args.Key, shift);
                break;
            default:
                return;
        }
        args.Handled = true;
    }

    private void Nudge(VirtualKey key, bool resize)
    {
        int cells = key switch { VirtualKey.Left => -1, VirtualKey.Right => 1, _ => 0 };
        int pitch = key switch { VirtualKey.Up => 1, VirtualKey.Down => -1, _ => 0 };
        var notes = SelectedNotes();
        if (notes.Length == 0)
        {
            _entryTick = MoveTick(_entryTick, cells);
            _entryPitch = (byte)Math.Clamp(_entryPitch + pitch, 0, 127);
            RefreshSelection();
        }
        else
        {
            var edited = new PianoRollNote[notes.Length];
            TransformSelection(notes, edited, notes.MinBy(item => item.Note.StartTick).Note, cells, pitch, resize);
            if (_document!.ReplaceMany(edited))
                SetEntryFromSelection();
        }
        RevealTick(_entryTick, _entryPitch);
    }

    private void GridSizeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (GridSizeBox is null || ScoreHost is null || EditorScroll is null)
            return;
        UpdateGrid();
        InvalidateScore();
        RefreshViewport();
        if (IsLoaded)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Focus(FocusState.Programmatic));
    }

    private void UpdateGrid()
    {
        int cells = GridSizeBox.SelectedIndex >= 0 ? 1 << GridSizeBox.SelectedIndex : DefaultCellsPerBeat;
        _cellsPerBeat = Math.Min(_ticksPerBeat, cells);
    }

    private void SetEntryFromSelection()
    {
        var notes = SelectedNotes();
        if (notes.Length > 0)
            SetEntry(notes.MinBy(item => item.Note.StartTick).Note);
    }

    private void AddClicked(object sender, RoutedEventArgs args) => AddNext();
    private void DeleteClicked(object sender, RoutedEventArgs args) => DeleteSelected();
    private void CutClicked(object sender, RoutedEventArgs args) => CutSelection();
    private void CopyClicked(object sender, RoutedEventArgs args) => CopySelection();
    private void PasteClicked(object sender, RoutedEventArgs args) => PasteSelection();
    private void UndoClicked(object sender, RoutedEventArgs args) => UndoEdit();

    private void UndoEdit()
    {
        if (_document?.Undo() is true)
            SetEntryFromSelection();
    }

    private void AddNext()
    {
        long cell = CellNearestTick(_entryTick);
        if (_selectedIds.Count > 0 && TickOfCell(cell) <= _entryTick && cell < long.MaxValue)
            cell++;

        AddAt(cell, _entryPitch);
        RevealTick(TickOfCell(cell), _entryPitch);
    }

    private void AddAt(long cell, byte pitch)
    {
        if (cell == long.MaxValue)
            return;
        long tick = TickOfCell(cell);
        long duration = TickOfCell(cell + 1) - tick;
        if (duration <= 0)
            return;

        var note = new SongNote(tick, duration, _defaultChannel, pitch, 96);
        long id = _document!.Add(note);
        SetEntry(note);
        SetSelection([id]);
        NotePreviewRequested?.Invoke(note);
    }

    private void DeleteSelected()
    {
        if (_selectedIds.Count > 0)
            _document!.DeleteMany(_selectedIds);
    }
}
