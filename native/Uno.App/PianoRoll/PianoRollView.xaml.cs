using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Signal.Native.Engine;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace Signal.Native.Uno.App.PianoRoll;

public sealed partial class PianoRollView : UserControl
{
    private const int DefaultCellsPerBeat = 4;
    private const int PitchCount = 128;
    private const byte DefaultDrumPitch = 36;
    private const byte DrumLaneCenter = 42;
    private const int FirstGmDrumPitch = 35;
    private const double RowHeight = 24;
    private const double BeatWidth = 96;
    private const double KeyboardWidth = 68;
    private const double DrumKeyboardWidth = 122;
    private const double TimelineHeight = 44;
    private static readonly string[] PitchNames = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];
    private static readonly string[] DrumNames =
    [
        "Acoustic Kick", "Kick", "Side Stick", "Snare", "Hand Clap", "Electric Snare",
        "Low Floor Tom", "Closed Hi-Hat", "High Floor Tom", "Pedal Hi-Hat", "Low Tom", "Open Hi-Hat",
        "Low-Mid Tom", "Hi-Mid Tom", "Crash Cymbal 1", "High Tom", "Ride Cymbal 1", "Chinese Cymbal",
        "Ride Bell", "Tambourine", "Splash Cymbal", "Cowbell", "Crash Cymbal 2", "Vibraslap",
        "Ride Cymbal 2", "High Bongo", "Low Bongo", "Mute High Conga", "Open High Conga", "Low Conga",
        "High Timbale", "Low Timbale", "High Agogo", "Low Agogo", "Cabasa", "Maracas",
        "Short Whistle", "Long Whistle", "Short Güiro", "Long Güiro", "Claves", "High Wood Block",
        "Low Wood Block", "Mute Cuíca", "Open Cuíca", "Mute Triangle", "Open Triangle"
    ];

    private readonly Rectangle _rulerCursor = new() { Width = 2, Height = TimelineHeight };
    private const double CueMarkerWidth = 23;
    private readonly Polygon _cueMarker = new() { Points = [new(0, 19), new(CueMarkerWidth, 19), new(11.5, 34)] };
    private readonly TranslateTransform _cursorOffset = new();
    private readonly TranslateTransform _rulerCursorOffset = new();
    private readonly TranslateTransform _timelineOffset = new();
    private readonly RectangleGeometry _timelineClip = new();
    private (double Left, double Right, int Resolution, MeterMap Meter, Palette Colors)? _paintedRuler;
    private (double Left, double Width)? _rulerViewport;
    private (double Top, double Height, bool Drums, Palette Colors)? _paintedKeys;
    private PianoRollDocument? _document;
    private MeterMap _meterMap = new(TempoMap.DefaultTicksPerQuarter, []);
    private Palette _palette = Palette.Dark;
    private int _ticksPerBeat = TempoMap.DefaultTicksPerQuarter;
    private int _cellsPerBeat = DefaultCellsPerBeat;
    private byte _defaultChannel;
    private long _timelineEnd;
    private long? _playhead;
    private bool _showCueMarker;
    private long _entryTick;
    private byte _entryPitch = 60;
    private (long Tick, byte Pitch, bool Recenter)? _reveal = (0, 60, true);
    private uint? _pointerId;
    private Point _press;
    private bool _editingEnabled = true;
    private bool _viewportRefreshPending;

    private double CellWidth => BeatWidth / _cellsPerBeat;
    private bool ShowsDrumLanes => _defaultChannel == SongTrackEditor.DrumChannel;
    private double LaneLabelWidth => ShowsDrumLanes ? DrumKeyboardWidth : KeyboardWidth;

    public PianoRollView()
    {
        InitializeComponent();
        InitializeScoreDrawing();
        InitializeChannelLanes();
        Cursor.RenderTransform = _cursorOffset;
        _rulerCursor.RenderTransform = _rulerCursorOffset;
        Timeline.RenderTransform = _timelineOffset;
        TimelineViewport.Clip = _timelineClip;
        RulerCursorLayer.Children.Add(_rulerCursor);
        RulerCursorLayer.Children.Add(_cueMarker);
        Loaded += (_, _) =>
        {
            RefreshViewport();
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyReveal);
        };
        Unloaded += (_, _) =>
        {
            CancelViewportRefresh();
            ReleaseScoreResources();
            CancelGesture();
            CancelRulerGesture();
            CancelTempoGesture();
            TempoInput.ReleasePointerCaptures();
            CloseTempoMarkerEditor();
        };
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

        CancelAutomationDrag();
        SetVelocityHover(null);
        CancelTempoGesture();
        TempoInput.ReleasePointerCaptures();
        CloseTempoMarkerEditor();
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
        ShowDrumHoverTip(null);
        _editExtentTick = 0;
        if (_recordingDocument is null)
            _takeExtentTick = 0;
        UpdateRecordingLayerVisibility();
        _ticksPerBeat = ticksPerQuarter;
        UpdateGrid();
        bool previousDrumMode = ShowsDrumLanes;
        _defaultChannel = defaultChannel;
        if (ShowsDrumLanes != previousDrumMode)
        {
            PitchColumn.Width = new GridLength(LaneLabelWidth);
            PaintPitchLanes();
        }
        _selectedIds.Clear();
        var firstNote = document.Notes.Count == 0
            ? new SongNote(0, 0, defaultChannel, ShowsDrumLanes ? DefaultDrumPitch : (byte)60, 96)
            : document.Notes.MinBy(item => item.Note.StartTick).Note;
        _entryTick = firstNote.StartTick;
        _entryPitch = firstNote.NoteNumber;
        RefreshDocument();
        byte center = ShowsDrumLanes && firstNote.NoteNumber is >= 35 and <= 46 ? DrumLaneCenter : firstNote.NoteNumber;
        QueueReveal(_playhead ?? firstNote.StartTick, center, recenter: true);
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

    public void SetMeterMap(MeterMap map)
    {
        if (ReferenceEquals(_meterMap, map))
            return;

        _meterMap = map;
        UpdateSurfaceExtent();
        InvalidateScore();
        RefreshSelection();
        RefreshViewport();
    }

    public void RevealTick(long tick, byte pitch) => QueueReveal(tick, pitch, recenter: false);

    private void QueueReveal(long tick, byte pitch, bool recenter)
    {
        _reveal = (tick, pitch, recenter);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyReveal);
    }

    public void SetPlayhead(long? tick, bool reveal = false, bool showCueMarker = false)
    {
        long? previous = _playhead;
        _playhead = tick;
        _showCueMarker = showCueMarker;
        if (tick is { } position && _recordingDocument is not null)
            ExtendTakeTimeline(position);
        RefreshPlayhead();
        if (tick is not { } active || _pointerId is not null || _rulerPointerId is not null || _tempoPointerId is not null
            || !IsLoaded || EditorScroll.ViewportWidth <= 0)
            return;

        double left = EditorScroll.HorizontalOffset;
        double right = left + EditorScroll.ViewportWidth;
        double x = TickX(active);
        bool outsideView = x < left || x >= right;
        bool wasVisible = previous is { } prior && TickX(prior) >= left && TickX(prior) < right;
        if (outsideView && (reveal || previous is null || wasVisible))
            EditorScroll.ChangeView(Math.Clamp(x - CellWidth, 0, EditorScroll.ScrollableWidth), null, null, true);
    }

    private void ApplyPalette()
    {
        _palette = ActualTheme == ElementTheme.Light ? Palette.Light : Palette.Dark;
        Surface.Background = _palette.NaturalLane;
        Corner.Background = RulerInput.Background = _palette.Ruler;
        TempoHeader.Background = TempoInput.Background = _palette.Ruler;
        PitchKeys.Background = _palette.Ruler;
        Cursor.Fill = _rulerCursor.Fill = _cueMarker.Fill = _palette.Playhead;
        GridLoopShade.Fill = RulerLoopFill.Fill = RulerLoopStart.Fill = RulerLoopEnd.Fill = _palette.Loop;
        SelectionBox.Fill = SelectionBox.Stroke = _palette.Loop;
        RecolorRecordingLayers();
        PaintPitchLanes();
        InvalidateScore();
        RefreshViewport();
    }

    private void RefreshDocument()
    {
        if (_document is { } document && _selectedIds.Count != 0)
            _selectedIds.IntersectWith(document.Notes.Select(item => item.Id));

        IndexDocumentNotes();
        UpdateSurfaceExtent();
        InvalidateScore();
        RefreshSelection();
        RefreshViewport();
    }

    private void UpdateSurfaceExtent()
    {
        long trackEnd = _latestEndByStart.Length == 0 ? 0 : _latestEndByStart[^1];
        long lastTick = Math.Max(Math.Max(_timelineEnd, trackEnd), _loopRange?.EndTick ?? 0);
        lastTick = Math.Max(lastTick, _takeExtentTick);
        lastTick = Math.Max(lastTick, _editExtentTick);
        lastTick = Math.Max(lastTick, _tempoMap?.Changes[^1].Tick ?? 0);
        lastTick = Math.Max(lastTick, _meterMap.Changes[^1].Tick);
        long endTick = _meterMap.NextBarTick(_meterMap.NextBarTick(lastTick));
        double width = TickX(Math.Max(_meterMap.TickAtBar(5), endTick));
        if (Surface.Width == width)
            return;

        Surface.Width = width;
        Surface.Height = PitchCount * RowHeight;
        Cursor.Height = Surface.Height;
        if (GridLayer.Children.Count == 0)
            PaintPitchLanes();
        else
            foreach (Rectangle stripe in GridLayer.Children)
                stripe.Width = width;
    }

    private void PaintPitchLanes()
    {
        GridLayer.Children.Clear();
        if (!double.IsFinite(Surface.Width))
            return;

        for (int pitch = 0; pitch < PitchCount; pitch++)
        {
            double y = PitchY(pitch);
            bool shaded = ShowsDrumLanes ? (pitch & 1) != 0 : IsAccidental(pitch);
            if (shaded)
                AddRect(GridLayer, 0, y, Surface.Width, RowHeight, _palette.AccidentalLane);
            AddRect(GridLayer, 0, y + RowHeight - 1, Surface.Width, 1,
                !ShowsDrumLanes && pitch % 12 == 0 ? _palette.MajorLine : _palette.MinorLine);
        }
    }

    private void RefreshViewport()
    {
        double width = EditorScroll.ViewportWidth;
        double height = EditorScroll.ViewportHeight;
        if (width <= 0 || height <= 0 || !double.IsFinite(Surface.Width))
            return;

        double left = EditorScroll.HorizontalOffset;
        double top = EditorScroll.VerticalOffset;
        UpdateScoreViewport(left, top, Math.Min(width, Math.Max(0, Surface.Width - left)),
            Math.Min(height, Math.Max(0, Surface.Height - top)));
        double right = Math.Min(Surface.Width, left + width);
        if (_paintedRuler is not { } rulerPage || left < rulerPage.Left || right > rulerPage.Right ||
            rulerPage.Resolution != _ticksPerBeat || rulerPage.Meter != _meterMap || rulerPage.Colors != _palette)
        {
            double first = Math.Max(0, left - width);
            double last = Math.Min(Surface.Width, right + width);
            _paintedRuler = (first, last, _ticksPerBeat, _meterMap, _palette);
            PaintRuler(first, last);
            _rulerViewport = null;
        }
        _timelineOffset.X = _paintedRuler.Value.Left - left;

        var ruler = (left, width);
        if (_rulerViewport != ruler)
        {
            _rulerViewport = ruler;
            _timelineClip.Rect = new Rect(0, 0, width, TimelineHeight);
            RefreshTempoLane();
            RefreshLoopRangeVisuals();
            RefreshPlayhead();
            PaintAutomationLane();
        }

        RefreshRecordingViewport(left, top, width, height);
        var keys = (top, height, ShowsDrumLanes, _palette);
        if (_paintedKeys == keys)
            return;
        _paintedKeys = keys;
        PitchKeys.Clip = new RectangleGeometry { Rect = new Rect(0, 0, LaneLabelWidth, height) };
        PitchKeys.Children.Clear();
        int firstRow = Math.Clamp((int)Math.Floor(top / RowHeight), 0, PitchCount - 1);
        int lastRow = Math.Clamp((int)Math.Floor((top + height) / RowHeight), 0, PitchCount - 1);
        for (int row = firstRow; row <= lastRow; row++)
            PaintPitchLabel(PitchCount - 1 - row, row * RowHeight - top);
    }

    private void PaintRuler(double left, double right)
    {
        Timeline.Children.Clear();
        long firstTick = long.CreateSaturating(Math.Floor(left / BeatWidth * _ticksPerBeat));
        long lastTick = long.CreateSaturating(Math.Ceiling(right / BeatWidth * _ticksPerBeat));
        for (long tick = _meterMap.PositionAt(firstTick).BeatTick; tick <= lastTick;)
        {
            var pos = _meterMap.PositionAt(tick);
            bool bar = tick == pos.BarTick;
            double x = TickX(tick);
            var line = bar ? _palette.MajorLine : _palette.BeatLine;
            AddRect(Timeline, x - left, bar ? 0 : TimelineHeight - 6, bar ? 2 : 1, bar ? TimelineHeight : 6, line);
            long next = _meterMap.NextBeatTick(tick);
            double labelWidth = bar ? TickX(_meterMap.NextBarTick(tick)) - x : TickX(next) - x;
            string label = bar ? $"{pos.Bar}" : $"{pos.Bar}.{pos.Beat}";
            if (labelWidth >= 12 + label.Length * 7)
            {
                AddCaption(Timeline, label, x - left + 8, bar ? 5 : 6, bar ? _palette.RulerText : _palette.RulerMuted, bar);
            }
            if (next <= tick)
                break;
            tick = next;
        }
        AddRect(Timeline, 0, TimelineHeight - 1, right - left, 1, _palette.MajorLine);
    }

    private void PaintPitchLabel(int pitch, double y)
    {
        if (ShowsDrumLanes)
        {
            AddRect(PitchKeys, 0, y, LaneLabelWidth - 1, RowHeight - 1,
                (pitch & 1) != 0 ? _palette.AccidentalLane : _palette.Ruler);
            AddCaption(PitchKeys, Caption(pitch, percussion: true), 10, y + 4, _palette.RulerText,
                pitch is 36 or 38 or 42 or 46);
            return;
        }

        bool accidental = IsAccidental(pitch);
        AddRect(PitchKeys, 0, y, KeyboardWidth - 1, RowHeight - 1, _palette.NaturalKey);
        if (accidental)
            AddRect(PitchKeys, 0, y, KeyboardWidth - 9, RowHeight - 1, _palette.AccidentalKey);
        AddCaption(PitchKeys, Caption(pitch, percussion: false), 10, y + 4,
            accidental ? _palette.AccidentalKeyText : _palette.NaturalKeyText, pitch % 12 == 0);
    }

    private void PitchKeyPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(PitchKeys);
        if (_document is null || !point.Properties.IsLeftButtonPressed)
            return;

        int pitch = PitchCount - 1 - (int)((point.Position.Y + EditorScroll.VerticalOffset) / RowHeight);
        if (pitch is < 0 or >= PitchCount)
            return;

        Focus(FocusState.Pointer);
        PitchPreviewRequested?.Invoke(new SongNote(0, 0, _defaultChannel, (byte)pitch, 96));
        args.Handled = true;
    }

    public event Action<SongNote>? PitchPreviewRequested;

    private void EditorViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (args.IsIntermediate && IsLoaded && !HasActiveGesture)
        {
            // The thumb can report several positions between display frames. Paint
            // the latest viewport once, and apply the final position immediately.
            if (!_viewportRefreshPending)
            {
                _viewportRefreshPending = true;
                CompositionTarget.Rendering += RenderPendingViewport;
            }
            return;
        }

        CancelViewportRefresh();
        RefreshViewport();
        if (_pointerId is not null)
            PreviewGesture(EditPointerOnSurface());
        if (_rulerPointerId is not null)
            PreviewRulerDrag(_rulerPointerX);
        if (_reveal is not null)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyReveal);
    }

    private void EditorSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (ReferenceEquals(sender, Surface))
            InvalidateScore();
        RefreshViewport();
        if (_reveal is not null)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyReveal);
    }

    private void RenderPendingViewport(object? sender, object args)
    {
        CancelViewportRefresh();
        RefreshViewport();
    }

    private void CancelViewportRefresh()
    {
        if (!_viewportRefreshPending)
            return;
        _viewportRefreshPending = false;
        CompositionTarget.Rendering -= RenderPendingViewport;
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
        if ((_rulerCuePreview ?? _playhead) is not { } tick)
        {
            Cursor.Visibility = _rulerCursor.Visibility = Visibility.Collapsed;
            _cueMarker.Visibility = Visibility.Collapsed;
            return;
        }
        double x = TickX(tick);
        double relativeX = x - EditorScroll.HorizontalOffset;
        Cursor.Visibility = Visibility.Visible;
        _rulerCursor.Visibility = relativeX >= 0 && relativeX < EditorScroll.ViewportWidth
            ? Visibility.Visible : Visibility.Collapsed;
        _cueMarker.Visibility = (_showCueMarker || _rulerCuePreview.HasValue) ? _rulerCursor.Visibility : Visibility.Collapsed;
        if (_cueMarker.Visibility == Visibility.Visible)
        {
            double tip = relativeX + 1;
            double left = Math.Clamp(tip - CueMarkerWidth / 2, 0, Math.Max(0, EditorScroll.ViewportWidth - CueMarkerWidth));
            var point = new Point(tip - left, 34);
            if (_cueMarker.Points[2] != point)
                _cueMarker.Points[2] = point;
            Canvas.SetLeft(_cueMarker, left);
        }
        _cursorOffset.X = x;
        _rulerCursorOffset.X = relativeX;
    }

    private bool IsWithinSurface(Point point) =>
        point.X >= 0 && point.X < Surface.Width && point.Y >= 0 && point.Y < Surface.Height;

    private bool NoteContains(SongNote note, Point point)
    {
        double x = TickX(note.StartTick);
        double y = PitchY(note.NoteNumber);
        return point.X >= x && point.X < x + NoteWidth(note) && point.Y >= y && point.Y < y + RowHeight;
    }

    private long MoveTick(long tick, long cells)
    {
        long cell = CellNearestTick(tick);
        if (cells < -cell)
            return 0;
        long target = cell + Math.Clamp(cells, -cell, long.MaxValue - cell);
        long delta = TickOfCell(target) - TickOfCell(cell);
        return tick + Math.Clamp(delta, -tick, long.MaxValue - tick);
    }

    private long CellAtPosition(double x)
    {
        long cell = (long)Math.Floor(x / CellWidth);
        if (cell > 0 && x < TickX(TickOfCell(cell)))
            return cell - 1;
        if (cell < long.MaxValue && x >= TickX(TickOfCell(cell + 1)))
            return cell + 1;
        return cell;
    }

    // Convert absolute cell numbers so rounding never accumulates across beats.
    private long TickOfCell(long cell) => long.CreateSaturating(
        ((Int128)cell * _ticksPerBeat + _cellsPerBeat / 2) / _cellsPerBeat);

    private long CellNearestTick(long tick) => (long)(((Int128)tick * _cellsPerBeat + _ticksPerBeat / 2) / _ticksPerBeat);
    private double NoteWidth(SongNote note) => NoteWidth(note.LengthTicks);
    private double NoteWidth(long lengthTicks) => Math.Max(MinimumNoteWidth, TickX(lengthTicks) - 1);
    private double TickX(long tick) => tick * (BeatWidth / _ticksPerBeat);
    private static double PitchY(int pitch) => (PitchCount - 1 - pitch) * RowHeight;
    private static bool IsAccidental(int pitch) => pitch % 12 is 1 or 3 or 6 or 8 or 10;
    private static string Caption(int pitch, bool percussion)
    {
        if (!percussion)
            return $"{PitchNames[pitch % 12]}{pitch / 12 - 1}";

        int index = pitch - FirstGmDrumPitch;
        return index >= 0 && index < DrumNames.Length ? DrumNames[index] : $"MIDI {pitch}";
    }
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
