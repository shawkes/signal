using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Microsoft.UI.Xaml.Shapes;
using Signal.Native.Engine;

namespace Signal.Native.Uno.App.PianoRoll;

public sealed partial class PianoRollView
{
    private const double RulerHandleHitWidth = 7;
    private const double RulerMarkerWidth = 5;

    private LoopRange? _loopRange;
    private LoopRange? _loopPreview;
    private bool _loopEnabled;
    private bool _showLoopHandles;
    private bool _loopRangeEditable = true;
    private uint? _rulerPointerId;
    private double _rulerPressX;
    private double _rulerPointerX;
    private long _rulerAnchorTick;
    private long? _rulerCuePreview;
    private RulerDrag _rulerDrag;

    public event Action<LoopRange>? LoopRangeSelected;
    public event Action<long>? PlayheadPositionSelected;

    public void SetLoopRangeEditable(bool editable)
    {
        _loopRangeEditable = editable;
        if (!editable && _rulerPointerId is not null)
        {
            CancelRulerGesture();
            RulerInput.ReleasePointerCaptures();
        }
    }

    public void SetLoopRange(LoopRange? range, bool enabled, bool showHandles = true)
    {
        if (_loopRange == range && _loopEnabled == enabled && _showLoopHandles == showHandles)
            return;

        bool rangeChanged = _loopRange != range;
        _loopRange = range;
        _loopEnabled = enabled;
        _showLoopHandles = showHandles;
        if (rangeChanged)
            UpdateSurfaceExtent();
        RefreshLoopRangeVisuals();
    }

    private void RulerPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!_editingEnabled || !_loopRangeEditable || _document is null || _pointerId is not null ||
            _rulerPointerId is not null || _tempoPointerId is not null)
            return;

        var point = args.GetCurrentPoint(RulerInput);
        double x = point.Position.X;
        double contentX = EditorScroll.HorizontalOffset + x;
        if (!point.Properties.IsLeftButtonPressed || x < 0 || x > EditorScroll.ViewportWidth || contentX > Surface.Width
            || !RulerInput.CapturePointer(args.Pointer))
            return;

        Focus(FocusState.Pointer);
        _rulerPointerId = args.Pointer.PointerId;
        _rulerPressX = _rulerPointerX = x;
        _rulerDrag = RulerDrag.Playhead;

        if (point.Position.Y >= 35 && _showLoopHandles && _loopRange is { } range)
        {
            if (HitsRulerHandle(range.StartTick, x))
            {
                _rulerDrag = RulerDrag.Start;
                _rulerAnchorTick = range.EndTick;
            }
            else if (HitsRulerHandle(range.EndTick, x))
            {
                _rulerDrag = RulerDrag.End;
                _rulerAnchorTick = range.StartTick;
            }
        }

        args.Handled = true;
    }

    private void RulerContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (!_editingEnabled || !_loopRangeEditable || _document is null || _rulerPointerId is not null)
            return;

        bool hasPosition = args.TryGetPosition(RulerInput, out var position);
        long tick = hasPosition ? SnapRulerTick(position.X) : _playhead ?? 0;
        var range = _showLoopHandles ? _loopRange : null;
        var menu = new MenuFlyout();
        var start = new MenuFlyoutItem { Text = "Set loop in" };
        start.Click += (_, _) =>
        {
            long barLength = _meterMap.NextBarTick(tick) - _meterMap.PositionAt(tick).BarTick;
            long suggestedEnd = long.CreateSaturating((Int128)tick + barLength);
            long end = range is { EndTick: var currentEnd } && currentEnd > tick ? currentEnd : suggestedEnd;
            if (_loopRangeEditable && end > tick)
                LoopRangeSelected?.Invoke(new(tick, end));
        };
        var finish = new MenuFlyoutItem { Text = "Set loop out", IsEnabled = tick > (range?.StartTick ?? 0) };
        finish.Click += (_, _) =>
        {
            if (_loopRangeEditable)
                LoopRangeSelected?.Invoke(new(range?.StartTick ?? 0, tick));
        };
        menu.Items.Add(start);
        menu.Items.Add(finish);
        if (range is not null)
        {
            var clear = new MenuFlyoutItem { Text = "Clear loop markers" };
            clear.Click += (_, _) => ClearLoopRangeRequested?.Invoke();
            menu.Items.Add(clear);
        }
        args.Handled = true;
        if (hasPosition)
            menu.ShowAt(RulerInput, new FlyoutShowOptions { Position = position });
        else
            menu.ShowAt(RulerInput);
    }

    public event Action? ClearLoopRangeRequested;

    private void RulerPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_rulerPointerId != args.Pointer.PointerId)
            return;

        PreviewRulerDrag(args.GetCurrentPoint(RulerInput).Position.X);
        UpdateDragScroll();

        args.Handled = true;
    }

    private void RulerPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_rulerPointerId != args.Pointer.PointerId)
            return;

        PreviewRulerDrag(args.GetCurrentPoint(RulerInput).Position.X);
        var selected = _loopPreview;
        var cue = _rulerCuePreview;
        CancelRulerGesture();
        RulerInput.ReleasePointerCapture(args.Pointer);
        args.Handled = true;
        if (selected is { } range)
            LoopRangeSelected?.Invoke(range);
        else if (cue is long tick)
            PlayheadPositionSelected?.Invoke(tick);
    }

    private void RulerPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_rulerPointerId == args.Pointer.PointerId)
            CancelRulerGesture();
    }

    private void CancelRulerGesture()
    {
        if (_rulerPointerId is not null)
            _dragScrollTimer?.Stop();
        _rulerPointerId = null;
        if (_rulerCuePreview.HasValue)
        {
            _rulerCuePreview = null;
            RefreshPlayhead();
        }
        if (_loopPreview is null)
            return;

        _loopPreview = null;
        RefreshLoopRangeVisuals();
    }

    private void PreviewRulerDrag(double x)
    {
        _rulerPointerX = x;
        if (_rulerDrag == RulerDrag.Playhead)
        {
            _rulerCuePreview = Math.Min(_timelineEnd,
                TickOfCell(CellNearestTick(long.CreateSaturating(Math.Round(RulerTickAt(x))))));
            RefreshPlayhead();
            return;
        }
        if (_loopPreview is null && Math.Abs(x - _rulerPressX) < 4)
            return;

        long tick = SnapRulerTick(x);
        long anchor = _rulerAnchorTick;
        LoopRange candidate = _rulerDrag == RulerDrag.Start
            ? new(Math.Min(tick, PreviousBeat(anchor)), anchor)
            : new(anchor, Math.Max(tick, NextBeat(anchor)));
        if (_loopPreview == candidate)
            return;

        _loopPreview = candidate;
        RefreshLoopRangeVisuals();
    }

    private long SnapRulerTick(double x)
    {
        double rawTick = RulerTickAt(x);
        long tick = long.CreateSaturating(Math.Floor(rawTick));
        long before = _meterMap.PositionAt(tick).BeatTick;
        long after = _meterMap.NextBeatTick(tick);
        return rawTick - before < after - rawTick ? before : after;
    }

    private double RulerTickAt(double x)
    {
        double contentX = EditorScroll.HorizontalOffset + Math.Clamp(x, 0, EditorScroll.ViewportWidth);
        return Math.Min(contentX, Surface.Width) / BeatWidth * _ticksPerBeat;
    }

    private long PreviousBeat(long tick) => _meterMap.PositionAt(Math.Max(0, tick - 1)).BeatTick;
    private long NextBeat(long tick) => _meterMap.NextBeatTick(tick);

    private bool HitsRulerHandle(long tick, double pointerX)
    {
        double x = TickX(tick) - EditorScroll.HorizontalOffset;
        return x >= 0 && x <= EditorScroll.ViewportWidth && Math.Abs(pointerX - x) <= RulerHandleHitWidth;
    }

    private void RefreshLoopRangeVisuals()
    {
        if ((_loopPreview ?? _loopRange) is not { } range || range.EndTick <= range.StartTick)
        {
            GridLoopShade.Visibility = RulerLoopFill.Visibility = RulerLoopStart.Visibility = RulerLoopEnd.Visibility =
                Visibility.Collapsed;
            return;
        }

        double strength = _loopPreview.HasValue || _loopEnabled ? 1 : 0.5;
        double viewportWidth = EditorScroll.ViewportWidth;
        double start = TickX(range.StartTick);
        double end = TickX(range.EndTick);
        GridLoopShade.Height = Surface.Height;
        GridLoopShade.Opacity = 0.12 * strength;
        PositionRulerShape(GridLoopShade, start, end);

        double left = start - EditorScroll.HorizontalOffset;
        double right = end - EditorScroll.HorizontalOffset;
        RulerLoopFill.Opacity = 0.30 * strength;
        RulerLoopStart.Opacity = RulerLoopEnd.Opacity = strength;
        PositionRulerShape(RulerLoopFill, Math.Clamp(left, 0, viewportWidth), Math.Clamp(right, 0, viewportWidth));
        if (!_showLoopHandles && _loopPreview is null)
        {
            RulerLoopStart.Visibility = RulerLoopEnd.Visibility = Visibility.Collapsed;
            return;
        }
        PositionRulerShape(RulerLoopStart, Math.Clamp(left, 0, viewportWidth),
            left >= 0 ? Math.Min(left + RulerMarkerWidth, viewportWidth) : 0);
        PositionRulerShape(RulerLoopEnd, right <= viewportWidth ? Math.Max(0, right - RulerMarkerWidth) : viewportWidth,
            Math.Clamp(right, 0, viewportWidth));
    }

    private static void PositionRulerShape(Rectangle rectangle, double start, double end)
    {
        rectangle.Visibility = end > start ? Visibility.Visible : Visibility.Collapsed;
        if (end <= start)
            return;

        Canvas.SetLeft(rectangle, start);
        rectangle.Width = end - start;
    }

    private enum RulerDrag { Playhead, Start, End }
}
