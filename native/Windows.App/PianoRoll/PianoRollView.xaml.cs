using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Signal.Native.Engine;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace Signal.Native.Windows.App.PianoRoll;

public sealed partial class PianoRollView : UserControl
{
    private const int BeatsPerBar = 4;
    private const int SixteenthsPerBeat = 4;
    private const int PitchCount = 128;
    private const double RowHeight = 24;
    private const double BeatWidth = 96;
    private const double KeyboardWidth = 68;
    private const double TimelineHeight = 30;
    private static readonly string[] PitchNames = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

    private readonly Dictionary<long, Border> _noteViews = [];
    private readonly Rectangle _rulerCursor = new() { Width = 2, Height = TimelineHeight };
    private PianoRollDocument? _document;
    private Palette _palette = Palette.Dark;
    private int _ticksPerBeat = TempoMap.DefaultTicksPerQuarter;
    private int _cellsPerBeat = SixteenthsPerBeat;
    private byte _defaultChannel;
    private long _timelineEnd;
    private long? _selected;
    private long? _playhead;
    private long _entryTick;
    private byte _entryPitch = 60;
    private (long Tick, byte Pitch, bool Recenter)? _reveal = (0, 60, true);
    private uint? _pointerId;
    private PianoRollNote? _grabbed;
    private SongNote? _preview;
    private Point _press;
    private bool _resizing;
    private bool _editingEnabled = true;

    private double CellWidth => BeatWidth / _cellsPerBeat;
    private long TicksPerBar => (long)BeatsPerBar * _ticksPerBeat;

    public PianoRollView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshViewport();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyReveal);
        };
        Unloaded += (_, _) => CancelRulerGesture();
        ActualThemeChanged += (_, _) => ApplyPalette();
        ApplyPalette();
        RefreshDocument();
    }

    public bool IsEditingEnabled
    {
        get => _editingEnabled;
        set
        {
            _editingEnabled = value;
            if (!value)
            {
                CancelGesture();
                Surface.ReleasePointerCaptures();
                CancelRulerGesture();
                RulerInput.ReleasePointerCaptures();
            }
            RefreshSelection();
        }
    }

    public void Attach(
        PianoRollDocument document, int ticksPerQuarter = TempoMap.DefaultTicksPerQuarter, byte defaultChannel = 0)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerQuarter);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(defaultChannel, (byte)15);
        if (ReferenceEquals(_document, document) && _ticksPerBeat == ticksPerQuarter && _defaultChannel == defaultChannel)
            return;

        if (_pointerId is not null)
        {
            CancelGesture();
            Surface.ReleasePointerCaptures();
        }
        if (_rulerPointerId is not null)
        {
            CancelRulerGesture();
            RulerInput.ReleasePointerCaptures();
        }
        if (_document is not null)
            _document.Changed -= RefreshDocument;
        _document = document;
        document.Changed += RefreshDocument;
        _ticksPerBeat = ticksPerQuarter;
        _cellsPerBeat = Math.Min(ticksPerQuarter, SixteenthsPerBeat);
        _defaultChannel = defaultChannel;
        GridLabel.Text = _cellsPerBeat == SixteenthsPerBeat ? "4-beat ruler  ·  1/16 edit grid" : "4-beat ruler  ·  1-tick edit grid";
        _selected = null;
        var firstNote = document.Notes.Count == 0
            ? new SongNote(0, 0, defaultChannel, 60, 96)
            : document.Notes.MinBy(item => item.Note.StartTick).Note;
        _entryTick = firstNote.StartTick;
        _entryPitch = firstNote.NoteNumber;
        RefreshDocument();
        QueueReveal(_playhead ?? firstNote.StartTick, firstNote.NoteNumber, recenter: true);
    }

    public void SetTimelineEnd(long tick)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        if (_timelineEnd == tick)
            return;

        _timelineEnd = tick;
        UpdateSurfaceExtent();
        RefreshViewport();
    }

    public void RevealTick(long tick, byte pitch) => QueueReveal(tick, pitch, recenter: false);

    private void QueueReveal(long tick, byte pitch, bool recenter)
    {
        _reveal = (tick, pitch, recenter);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyReveal);
    }

    public void SetPlayhead(long? tick)
    {
        long? previous = _playhead;
        _playhead = tick;
        RefreshPlayhead();
        if (tick is not { } active || _pointerId is not null || _rulerPointerId is not null
            || !IsLoaded || EditorScroll.ViewportWidth <= 0)
            return;

        double left = EditorScroll.HorizontalOffset;
        double right = left + EditorScroll.ViewportWidth;
        double x = TickX(active);
        bool outsideView = x < left || x >= right;
        bool wasVisible = previous is { } prior && TickX(prior) >= left && TickX(prior) < right;
        if (outsideView && (previous is null || wasVisible))
            EditorScroll.ChangeView(Math.Clamp(x - CellWidth, 0, EditorScroll.ScrollableWidth), null, null, true);
    }

    private void ApplyPalette()
    {
        _palette = ActualTheme == ElementTheme.Light ? Palette.Light : Palette.Dark;
        Surface.Background = _palette.NaturalLane;
        Corner.Background = RulerInput.Background = _palette.Ruler;
        PitchKeys.Background = _palette.Ruler;
        Cursor.Fill = _rulerCursor.Fill = _palette.Playhead;
        GridLoopShade.Fill = RulerLoopFill.Fill = RulerLoopStart.Fill = RulerLoopEnd.Fill = _palette.Loop;
        PaintPitchLanes();
        PaintNotes();
        RefreshViewport();
    }

    private void RefreshDocument()
    {
        if (_selected is { } id && _document?.Notes.All(item => item.Id != id) is true)
            _selected = null;
        if (SelectedNote() is { } selected)
        {
            _entryTick = selected.Note.StartTick;
            _entryPitch = selected.Note.NoteNumber;
        }

        UpdateSurfaceExtent();
        PaintNotes();
        RefreshSelection();
        RefreshViewport();
    }

    private void UpdateSurfaceExtent()
    {
        long trackEnd = _document?.Notes.Select(item => item.Note.StartTick + item.Note.LengthTicks).DefaultIfEmpty().Max() ?? 0;
        long lastTick = Math.Max(Math.Max(_timelineEnd, trackEnd), _loopRange?.EndTick ?? 0);
        double bars = Math.Max(4.0, Math.Floor(lastTick / (double)TicksPerBar) + 2);
        double width = bars * BeatsPerBar * BeatWidth;
        if (Surface.Width == width)
            return;

        Surface.Width = width;
        Surface.Height = PitchCount * RowHeight;
        Cursor.Height = Surface.Height;
        PaintPitchLanes();
    }

    private void PaintPitchLanes()
    {
        GridLayer.Children.Clear();
        if (!double.IsFinite(Surface.Width))
            return;

        for (int pitch = 0; pitch < PitchCount; pitch++)
        {
            double y = PitchY(pitch);
            if (IsAccidental(pitch))
                AddRect(GridLayer, 0, y, Surface.Width, RowHeight, _palette.AccidentalLane);
            AddRect(GridLayer, 0, y + RowHeight - 1, Surface.Width, 1,
                pitch % 12 == 0 ? _palette.MajorLine : _palette.MinorLine);
        }
    }

    private void RefreshViewport()
    {
        double width = EditorScroll.ViewportWidth;
        double height = EditorScroll.ViewportHeight;
        if (width <= 0 || height <= 0 || !double.IsFinite(Surface.Width))
            return;

        Timeline.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, TimelineHeight) };
        PitchKeys.Clip = new RectangleGeometry { Rect = new Rect(0, 0, KeyboardWidth, height) };
        Timeline.Children.Clear();
        PitchKeys.Children.Clear();
        BeatLayer.Children.Clear();
        double left = EditorScroll.HorizontalOffset;
        double top = EditorScroll.VerticalOffset;
        int cellsPerBar = _cellsPerBeat * BeatsPerBar;
        long firstCell = (long)Math.Floor(left / CellWidth);
        long lastCell = (long)Math.Ceiling(Math.Min(Surface.Width, left + width) / CellWidth);
        for (long cell = firstCell; cell <= lastCell; cell++)
        {
            bool bar = cell % cellsPerBar == 0;
            bool beat = cell % _cellsPerBeat == 0;
            double x = cell * CellWidth;
            var line = bar ? _palette.MajorLine : beat ? _palette.BeatLine : _palette.MinorLine;
            AddRect(BeatLayer, x, 0, 1, Surface.Height, line);
            if (beat)
            {
                AddRect(Timeline, x - left, bar ? 0 : TimelineHeight - 6, 1, bar ? TimelineHeight : 6, line);
                string label = bar ? $"{cell / cellsPerBar + 1}" : $"{cell / cellsPerBar + 1}.{cell % cellsPerBar / _cellsPerBeat + 1}";
                AddCaption(Timeline, label, x - left + 8, bar ? 5 : 6, bar ? _palette.RulerText : _palette.RulerMuted, bar);
            }
        }
        AddRect(Timeline, 0, TimelineHeight - 1, width, 1, _palette.MajorLine);
        Timeline.Children.Add(_rulerCursor);

        int firstRow = Math.Clamp((int)Math.Floor(top / RowHeight), 0, PitchCount - 1);
        int lastRow = Math.Clamp((int)Math.Floor((top + height) / RowHeight), 0, PitchCount - 1);
        for (int row = firstRow; row <= lastRow; row++)
        {
            int pitch = PitchCount - 1 - row;
            double y = row * RowHeight - top;
            bool accidental = IsAccidental(pitch);
            AddRect(PitchKeys, 0, y, KeyboardWidth - 1, RowHeight - 1, _palette.NaturalKey);
            if (accidental)
                AddRect(PitchKeys, 0, y, KeyboardWidth - 9, RowHeight - 1, _palette.AccidentalKey);
            AddCaption(PitchKeys, Caption(pitch), 10, y + 4,
                accidental ? _palette.AccidentalKeyText : _palette.NaturalKeyText, pitch % 12 == 0);
        }
        RefreshPlayhead();
        RefreshLoopRangeVisuals();
    }

    private void PaintNotes()
    {
        NotesLayer.Children.Clear();
        _noteViews.Clear();
        if (_document is null)
            return;

        foreach (var item in _document.Notes)
        {
            var border = new Border { Height = RowHeight - 4, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
            var body = new Grid();
            body.Children.Add(new TextBlock
            {
                Margin = new Thickness(6, 0, 7, 0),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _palette.NoteText,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            body.Children.Add(new Rectangle
            {
                Width = 2,
                Height = 8,
                RadiusX = 1,
                RadiusY = 1,
                Margin = new Thickness(0, 0, 3, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Fill = _palette.Handle
            });
            border.Child = body;
            NotesLayer.Children.Add(border);
            _noteViews.Add(item.Id, border);
            UpdateNoteVisual(item.Id, _grabbed?.Id == item.Id ? _preview ?? item.Note : item.Note);
        }
    }

    private void UpdateNoteVisual(long id, SongNote note)
    {
        if (!_noteViews.TryGetValue(id, out var border))
            return;

        bool selected = _selected == id;
        double width = NoteWidth(note);
        border.Width = width;
        border.Background = selected ? _palette.SelectedNote : _palette.Note;
        border.BorderBrush = selected ? _palette.SelectedOutline : _palette.NoteOutline;
        Canvas.SetLeft(border, TickX(note.StartTick));
        Canvas.SetTop(border, PitchY(note.NoteNumber) + 2);
        Canvas.SetZIndex(border, selected ? 1 : 0);
        string name = Caption(note.NoteNumber);
        AutomationProperties.SetName(border, $"{name}, tick {note.StartTick}, length {note.LengthTicks} ticks");
        var body = (Grid)border.Child;
        var label = (TextBlock)body.Children[0];
        label.Text = name;
        label.Visibility = width < 42 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshSelection()
    {
        var active = SelectedNote();
        int count = _document?.Notes.Count ?? 0;
        NoteCountText.Text = count == 1 ? "1 note" : $"{count} notes";
        AddButton.IsEnabled = _editingEnabled && _document is not null;
        DeleteButton.IsEnabled = _editingEnabled && active is not null;
        UndoButton.IsEnabled = _editingEnabled && _document?.CanUndo is true;
        if (active is not null)
        {
            var note = _preview ?? active.Value.Note;
            string beat = $"{note.StartTick / TicksPerBar + 1}.{note.StartTick % TicksPerBar / _ticksPerBeat + 1}";
            SelectionText.Text = $"{Caption(note.NoteNumber)} · {beat} · {note.LengthTicks} ticks";
        }
        else
        {
            SelectionText.Text = string.Empty;
        }
    }

    private void Select(PianoRollNote item)
    {
        var previous = SelectedNote();
        _selected = item.Id;
        _entryPitch = item.Note.NoteNumber;
        _entryTick = item.Note.StartTick;
        if (previous is { } old && old.Id != item.Id)
            UpdateNoteVisual(old.Id, old.Note);
        UpdateNoteVisual(item.Id, item.Note);
        RefreshSelection();
    }

    private PianoRollNote? SelectedNote()
    {
        if (_selected is not { } id || _document is null)
            return null;
        foreach (var item in _document.Notes)
        {
            if (item.Id == id)
                return item;
        }
        return null;
    }

    private PianoRollNote? HitNote(Point point)
    {
        if (SelectedNote() is { } selected && NoteContains(selected.Note, point))
            return selected;

        var notes = _document!.Notes;
        for (int i = notes.Count - 1; i >= 0; i--)
        {
            if (NoteContains(notes[i].Note, point))
                return notes[i];
        }
        return null;
    }

    private void SurfacePointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!_editingEnabled || _document is null || _pointerId is not null || _rulerPointerId is not null)
            return;
        var point = args.GetCurrentPoint(Surface);
        if (!point.Properties.IsLeftButtonPressed || !IsWithinSurface(point.Position))
            return;
        Focus(FocusState.Pointer);
        if (!Surface.CapturePointer(args.Pointer))
            return;

        _pointerId = args.Pointer.PointerId;
        _press = point.Position;
        _grabbed = HitNote(_press);
        if (_grabbed is { } item)
        {
            double width = NoteWidth(item.Note);
            _resizing = _press.X >= TickX(item.Note.StartTick) + width - Math.Min(7, width / 2);
            Select(item);
        }
        args.Handled = true;
    }

    private void SurfacePointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId != args.Pointer.PointerId)
            return;
        PreviewGesture(args.GetCurrentPoint(Surface).Position);
        args.Handled = true;
    }

    private void PreviewGesture(Point point)
    {
        if (_grabbed is not { } item)
            return;
        var note = item.Note;
        long deltaCells = (long)Math.Round((point.X - _press.X) / CellWidth, MidpointRounding.AwayFromZero);
        var candidate = _resizing
            ? note with { LengthTicks = ResizeLength(note, deltaCells) }
            : note with
            {
                StartTick = MoveStart(note, deltaCells),
                NoteNumber = (byte)Math.Clamp(note.NoteNumber - (int)Math.Round((point.Y - _press.Y) / RowHeight), 0, 127)
            };
        if (_preview == candidate)
            return;
        _preview = candidate;
        UpdateNoteVisual(item.Id, candidate);
        RefreshSelection();
    }

    private void SurfacePointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId != args.Pointer.PointerId)
            return;
        var point = args.GetCurrentPoint(Surface).Position;
        PreviewGesture(point);
        var original = _grabbed;
        var edited = _preview;
        bool resize = _resizing;
        CancelGesture();
        Surface.ReleasePointerCapture(args.Pointer);
        args.Handled = true;

        if (original is { } item && edited is { } candidate && candidate != item.Note)
        {
            if (resize)
                _document!.Resize(item.Id, candidate.StartTick, candidate.LengthTicks);
            else
                _document!.Move(item.Id, candidate.StartTick, candidate.NoteNumber);
        }
        else if (original is null && IsWithinSurface(point)
                 && Math.Abs(point.X - _press.X) < 6 && Math.Abs(point.Y - _press.Y) < 6)
        {
            AddAt((long)Math.Floor(point.X / CellWidth),
                  (byte)(PitchCount - 1 - (int)(point.Y / RowHeight)));
        }
    }

    private void SurfacePointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_pointerId == args.Pointer.PointerId)
            CancelGesture();
    }

    private void CancelGesture()
    {
        if (_grabbed is { } item)
            UpdateNoteVisual(item.Id, item.Note);
        _pointerId = null;
        _grabbed = null;
        _preview = null;
        RefreshSelection();
    }

    private void EditorKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!_editingEnabled || _document is null || _pointerId is not null || _rulerPointerId is not null)
            return;

        bool control = IsDown(VirtualKey.Control);
        bool shift = IsDown(VirtualKey.Shift);
        if (control && args.Key == VirtualKey.Z)
            _document.Undo();
        else if (args.Key == VirtualKey.Delete)
            DeleteSelected();
        else if (args.Key == VirtualKey.Insert)
            AddNext();
        else if (!control && args.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
            Nudge(args.Key, shift);
        else
            return;
        args.Handled = true;
    }

    private void Nudge(VirtualKey key, bool resize)
    {
        int cells = key switch { VirtualKey.Left => -1, VirtualKey.Right => 1, _ => 0 };
        int semitones = key switch { VirtualKey.Up => 1, VirtualKey.Down => -1, _ => 0 };
        if (SelectedNote() is { } item)
        {
            var note = item.Note;
            if (resize)
            {
                long length = ResizeLength(note, cells);
                if (length != note.LengthTicks)
                    _document!.Resize(item.Id, note.StartTick, length);
            }
            else
                _document!.Move(item.Id, MoveStart(note, cells), (byte)Math.Clamp(note.NoteNumber + semitones, 0, 127));

            if (SelectedNote() is { } moved)
            {
                _entryTick = moved.Note.StartTick;
                _entryPitch = moved.Note.NoteNumber;
                RevealTick(_entryTick, _entryPitch);
            }
        }
        else
        {
            _entryTick = MoveTick(_entryTick, cells, long.MaxValue);
            _entryPitch = (byte)Math.Clamp(_entryPitch + semitones, 0, 127);
            RevealTick(_entryTick, _entryPitch);
        }
    }

    private void AddClicked(object sender, RoutedEventArgs args) => AddNext();
    private void DeleteClicked(object sender, RoutedEventArgs args) => DeleteSelected();
    private void UndoClicked(object sender, RoutedEventArgs args) => _document?.Undo();

    private void AddNext()
    {
        long cell = CellNearestTick(_entryTick);
        if (SelectedNote() is not null && TickOfCell(cell) <= _entryTick && cell < long.MaxValue)
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
        Select(new PianoRollNote(id, note));
    }

    private void DeleteSelected()
    {
        if (_selected is { } id)
            _document!.Delete(id);
    }

    private void EditorViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        RefreshViewport();
        if (_rulerPointerId is not null)
            PreviewLoopDrag(_rulerPointerX);
        if (_reveal is not null)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyReveal);
    }

    private void EditorSizeChanged(object sender, SizeChangedEventArgs args)
    {
        RefreshViewport();
        if (_reveal is not null)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyReveal);
    }

    private void ApplyReveal()
    {
        if (_reveal is not { } target || !IsLoaded || EditorScroll.ViewportWidth <= 0 || EditorScroll.ViewportHeight <= 0
            || EditorScroll.ExtentWidth < Surface.Width - 1)
            return;
        _reveal = null;

        double x = TickX(target.Tick);
        double y = PitchY(target.Pitch);
        double? left = target.Recenter || x < EditorScroll.HorizontalOffset || x >= EditorScroll.HorizontalOffset + EditorScroll.ViewportWidth - CellWidth
            ? Math.Clamp(x - CellWidth * 2, 0, EditorScroll.ScrollableWidth)
            : null;
        double? top = target.Recenter || y < EditorScroll.VerticalOffset || y >= EditorScroll.VerticalOffset + EditorScroll.ViewportHeight - RowHeight
            ? Math.Clamp(y - EditorScroll.ViewportHeight / 2, 0, EditorScroll.ScrollableHeight)
            : null;
        if (left is not null || top is not null)
            EditorScroll.ChangeView(left, top, null, true);
    }

    private void RefreshPlayhead()
    {
        if (_playhead is not { } tick)
        {
            Cursor.Visibility = _rulerCursor.Visibility = Visibility.Collapsed;
            return;
        }
        double x = TickX(tick);
        double relativeX = x - EditorScroll.HorizontalOffset;
        Cursor.Visibility = Visibility.Visible;
        _rulerCursor.Visibility = relativeX >= 0 && relativeX < EditorScroll.ViewportWidth
            ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(Cursor, x);
        Canvas.SetLeft(_rulerCursor, relativeX);
    }

    private bool IsWithinSurface(Point point) =>
        point.X >= 0 && point.X < Surface.Width && point.Y >= 0 && point.Y < Surface.Height;

    private bool NoteContains(SongNote note, Point point)
    {
        double x = TickX(note.StartTick);
        double y = PitchY(note.NoteNumber);
        return point.X >= x && point.X < x + NoteWidth(note) && point.Y >= y && point.Y < y + RowHeight;
    }

    private long MoveStart(SongNote note, long cells) => MoveTick(note.StartTick, cells, long.MaxValue - note.LengthTicks);

    private long MoveTick(long tick, long cells, long maxTick)
    {
        long cell = CellNearestTick(tick);
        long target = cell + Math.Clamp(cells, -cell, long.MaxValue - cell);
        long delta = TickOfCell(target) - TickOfCell(cell);
        return tick + Math.Clamp(delta, -tick, maxTick - tick);
    }

    private long ResizeLength(SongNote note, long cells)
    {
        if (cells == 0 || note.StartTick == long.MaxValue)
            return note.LengthTicks;

        long end = MoveTick(note.StartTick + note.LengthTicks, cells, long.MaxValue);
        long minimum = Math.Clamp(note.LengthTicks, 1, TickOfCell(1));
        return Math.Max(minimum, end - note.StartTick);
    }

    // Convert absolute cell numbers so rounding never accumulates across beats.
    private long TickOfCell(long cell) => long.CreateSaturating(
        ((Int128)cell * _ticksPerBeat + _cellsPerBeat / 2) / _cellsPerBeat);

    private long CellNearestTick(long tick) => (long)(((Int128)tick * _cellsPerBeat + _ticksPerBeat / 2) / _ticksPerBeat);
    private double NoteWidth(SongNote note) => Math.Max(9, TickX(note.LengthTicks) - 1);
    private double TickX(long tick) => tick * (BeatWidth / _ticksPerBeat);
    private static double PitchY(int pitch) => (PitchCount - 1 - pitch) * RowHeight;
    private static bool IsAccidental(int pitch) => pitch % 12 is 1 or 3 or 6 or 8 or 10;
    private static string Caption(int pitch) => $"{PitchNames[pitch % 12]}{pitch / 12 - 1}";
    private static bool IsDown(VirtualKey key) => (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private static void AddRect(Canvas canvas, double x, double y, double width, double height, Brush fill)
    {
        var rect = new Rectangle { Width = width, Height = height, Fill = fill };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        canvas.Children.Add(rect);
    }

    private static void AddCaption(Canvas canvas, string caption, double x, double y, Brush brush, bool emphasized)
    {
        var label = new TextBlock
        {
            Text = caption,
            Foreground = brush,
            FontSize = emphasized ? 13 : 11,
            FontWeight = emphasized ? FontWeights.SemiBold : FontWeights.Normal
        };
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, y);
        canvas.Children.Add(label);
    }

    private sealed record Palette(
        Brush NaturalLane, Brush AccidentalLane, Brush MinorLine, Brush BeatLine, Brush MajorLine,
        Brush Ruler, Brush RulerText, Brush RulerMuted,
        Brush NaturalKey, Brush AccidentalKey, Brush NaturalKeyText, Brush AccidentalKeyText,
        Brush Note, Brush NoteOutline, Brush SelectedNote, Brush SelectedOutline, Brush NoteText, Brush Handle,
        Brush Playhead, Brush Loop)
    {
        public static Palette Dark { get; } = new(
            Color(0x182432), Color(0x131D29), Color(0x273648), Color(0x374C63), Color(0x4A6078),
            Color(0x222F3E), Color(0xE5EAF1), Color(0xA3B5C8),
            Color(0xC5D2DF), Color(0x33465D), Color(0x172636), Color(0xE2EBF4),
            Color(0x57BAA9), Color(0x64CDB8), Color(0x96E7D4), Color(0xE2FFF9),
            Color(0x132D2A), Color(0x326952), Color(0xFFC16C), Color(0x76B9FF));

        public static Palette Light { get; } = new(
            Color(0xF8FAFD), Color(0xEEF3F9), Color(0xDEE7F0), Color(0xC7D6E4), Color(0xA6BBCC),
            Color(0xE4EDF6), Color(0x22384D), Color(0x4B637B),
            Color(0xF4F7FC), Color(0x3B526A), Color(0x1B3043), Color(0xF0F5FB),
            Color(0x7BCCBA), Color(0x318574), Color(0x63C5AD), Color(0x134D43),
            Color(0x112E29), Color(0x376F64), Color(0xA75314), Color(0x0C65B4));

        private static SolidColorBrush Color(uint rgb) =>
            new(global::Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }
}
