using Microsoft.UI.Dispatching;

namespace Signal.Native.Windows.App.PianoRoll;

public sealed partial class PianoRollView
{
    private const double DragScrollMargin = 28;
    private const double DragScrollStep = 18;
    private DispatcherQueueTimer? _dragScrollTimer;
    private long _editExtentTick;

    private (int Horizontal, int Vertical) DragScrollDirection()
    {
        if (_pointerId is not null && _editDragStarted)
        {
            double visibleWidth = Math.Min(EditorScroll.ViewportWidth, Surface.Width - EditorScroll.HorizontalOffset);
            int horizontal = EdgeDirection(_editPointer.X, visibleWidth, EditorScroll.HorizontalOffset,
                EditorScroll.ScrollableWidth, canExtend: true);
            int vertical = _resizeDrag && _grabbedNote is not null ? 0 :
                EdgeDirection(_editPointer.Y, EditorScroll.ViewportHeight,
                    EditorScroll.VerticalOffset, EditorScroll.ScrollableHeight);
            return (horizontal, vertical);
        }
        if (_rulerPointerId is not null && (_loopPreview.HasValue || _rulerCuePreview.HasValue))
            return (EdgeDirection(_rulerPointerX, EditorScroll.ViewportWidth,
                EditorScroll.HorizontalOffset, EditorScroll.ScrollableWidth), 0);
        return default;
    }

    private static int EdgeDirection(double position, double viewport, double offset, double scrollable, bool canExtend = false)
    {
        double margin = Math.Min(DragScrollMargin, viewport / 3);
        if (viewport <= 0)
            return 0;
        if (position < margin && offset > 0)
            return -1;
        return position > viewport - margin && (offset < scrollable || canExtend) ? 1 : 0;
    }

    private void UpdateDragScroll()
    {
        var direction = DragScrollDirection();
        if (direction == default)
        {
            _dragScrollTimer?.Stop();
            return;
        }
        if (_dragScrollTimer is null)
        {
            _dragScrollTimer = DispatcherQueue.CreateTimer();
            _dragScrollTimer.Interval = TimeSpan.FromMilliseconds(30);
            _dragScrollTimer.Tick += (_, _) => StepDragScroll(DragScrollDirection());
        }
        if (!_dragScrollTimer.IsRunning)
        {
            _dragScrollTimer.Start();
            StepDragScroll(direction);
        }
    }

    private void StepDragScroll((int Horizontal, int Vertical) direction)
    {
        var (horizontal, vertical) = direction;
        if (horizontal == 0 && vertical == 0)
        {
            _dragScrollTimer?.Stop();
            return;
        }
        if (horizontal > 0 && _pointerId is not null &&
            EditorScroll.ScrollableWidth - EditorScroll.HorizontalOffset < DragScrollStep)
            ExtendEditTimeline();

        double? left = horizontal == 0 ? null : Math.Clamp(
            EditorScroll.HorizontalOffset + horizontal * DragScrollStep, 0, EditorScroll.ScrollableWidth);
        double? top = vertical == 0 ? null : Math.Clamp(
            EditorScroll.VerticalOffset + vertical * DragScrollStep, 0, EditorScroll.ScrollableHeight);
        EditorScroll.ChangeView(left, top, null, disableAnimation: true);
    }

    private void ExtendEditTimeline()
    {
        double page = Math.Max(EditorScroll.ViewportWidth, BeatWidth);
        double right = EditorScroll.HorizontalOffset + EditorScroll.ViewportWidth + page;
        long tick = long.CreateSaturating(Math.Ceiling(right / BeatWidth * _ticksPerBeat));
        if (tick <= _editExtentTick)
            return;

        _editExtentTick = tick;
        UpdateSurfaceExtent();
    }
}
