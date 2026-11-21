using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Signal.Native.Engine;
using Windows.Foundation;

namespace Signal.Native.Windows.App.PianoRoll;

public sealed partial class PianoRollView
{
    private const double AutomationInset = 10;
    private const double OpeningHandleWidth = 15;
    private const double PanCenterSnapDistance = 6;
    private readonly Dictionary<ChannelControl, ChannelLane> _channelLanes = [];
    private bool _showVelocityLane;
    private long? _hoveredVelocity;
    private ChannelLane? _activeAutomationLane;
    private uint? _automationPointerId;
    private Point _automationPress;
    private ChannelControlPoint? _draggedControlPoint;
    private ChannelControlPoint? _automationPreview;
    private bool _openingControlDrag;

    public event Action<ChannelControl, long, byte, long?>? ChannelPointEdited;
    public event Action<ChannelControl>? AutomationPointsRequested;

    private void InitializeChannelLanes()
    {
        foreach (var control in new[] { ChannelControl.Volume, ChannelControl.Pan })
        {
            var row = new Grid { Visibility = Visibility.Collapsed };
            var labelColumn = new ColumnDefinition { Width = new GridLength(LaneLabelWidth) };
            row.ColumnDefinitions.Add(labelColumn);
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var heading = new StackPanel { Spacing = 4, Margin = new Thickness(4) };
            heading.Children.Add(new TextBlock { Text = control.ToString() });
            var points = new Button { Content = "Points…", Padding = new Thickness(4, 2, 4, 2) };
            points.Click += (_, _) => AutomationPointsRequested?.Invoke(control);
            heading.Children.Add(points);
            var hide = new Button { Content = "Hide", Padding = new Thickness(4, 2, 4, 2) };
            heading.Children.Add(hide);
            row.Children.Add(heading);

            var graph = new Grid { Height = 140, Background = _palette.Ruler };
            Grid.SetColumn(graph, 1);
            row.Children.Add(graph);
            var canvas = new Canvas { IsHitTestVisible = false };
            graph.Children.Add(canvas);
            var lane = new ChannelLane(control, row, labelColumn, graph, canvas);
            ToolTipService.SetToolTip(graph, "Click to add a point. Drag the leftmost handle to change the opening value.");
            graph.PointerPressed += (sender, args) => AutomationPointerPressed(lane, args);
            graph.PointerMoved += AutomationPointerMoved;
            graph.PointerReleased += AutomationPointerReleased;
            graph.PointerCaptureLost += AutomationPointerCanceled;
            graph.PointerCanceled += AutomationPointerCanceled;
            hide.Click += (_, _) =>
            {
                if (ReferenceEquals(lane, _activeAutomationLane))
                    CancelAutomationDrag();
                row.Visibility = Visibility.Collapsed;
            };
            _channelLanes.Add(control, lane);
            ChannelLanes.Children.Add(row);
        }
    }

    public void ShowChannelLane(ChannelControl control)
    {
        var lane = _channelLanes[control];
        lane.Row.Visibility = Visibility.Visible;
        PaintChannelLane(lane);
    }

    public void SetChannelPoints(ChannelControl control, ChannelControlPoint[] points)
    {
        var lane = _channelLanes[control];
        lane.Points = points;
        PaintChannelLane(lane);
    }

    private void SetVelocityLane(bool show)
    {
        _showVelocityLane = show;
        AutomationLane.Visibility = AutomationLaneHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PaintVelocityLane();
    }

    private void HideAutomationLaneClicked(object sender, RoutedEventArgs args) => SetVelocityLane(false);

    private void SetVelocityHover(long? noteId)
    {
        if (_hoveredVelocity == noteId)
            return;
        _hoveredVelocity = noteId;
        if (_showVelocityLane)
            PaintVelocityLane();
    }

    private static double AutomationValueY(double height, byte value) =>
        height - AutomationInset - (height - 2 * AutomationInset) * value / 127;

    private void PaintAutomationLane()
    {
        PaintVelocityLane();
        foreach (var lane in _channelLanes.Values)
            PaintChannelLane(lane);
    }

    private void PaintVelocityLane()
    {
        if (!_showVelocityLane)
            return;
        double width = EditorScroll.ViewportWidth;
        AutomationCanvas.Children.Clear();
        if (width <= 0)
            return;

        double left = EditorScroll.HorizontalOffset;
        AutomationCanvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, AutomationLane.Height) };
        PianoRollNote? hovered = null;
        for (int i = FirstNoteReaching(left); i < _notesByStart.Length; i++)
        {
            var item = _notesByStart[i];
            double x = TickX(item.Note.StartTick) - left;
            if (x >= width)
                break;
            if (x < 0)
                continue;
            if (item.Id == _hoveredVelocity)
                hovered = item;
            else
                PaintVelocity(item, left, _selectedIds.Contains(item.Id) ? _palette.SelectedNote : _palette.RulerMuted, 2);
        }
        if (hovered is { } note)
            PaintVelocity(note, left, _palette.Playhead, 4);
    }

    private void PaintVelocity(PianoRollNote item, double left, Brush color, double width)
    {
        double y = AutomationValueY(AutomationLane.Height, item.Note.Velocity);
        AddRect(AutomationCanvas, TickX(item.Note.StartTick) - left, y, width, AutomationLane.Height - y, color);
    }

    private void PaintChannelLane(ChannelLane lane)
    {
        if (lane.Row.Visibility == Visibility.Collapsed)
            return;

        double width = EditorScroll.ViewportWidth;
        double height = lane.Graph.Height;
        double left = EditorScroll.HorizontalOffset;
        lane.LabelColumn.Width = PitchColumn.Width;
        lane.Graph.Background = _palette.Ruler;
        lane.Canvas.Children.Clear();
        if (width <= 0)
            return;
        lane.Canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };

        byte defaultValue = SongTrackEditor.DefaultControlValue(lane.Control);
        if (lane.Control == ChannelControl.Pan)
        {
            double center = AutomationValueY(height, defaultValue);
            lane.Canvas.Children.Add(new Line
            {
                X2 = width,
                Y1 = center,
                Y2 = center,
                Stroke = _palette.RulerText,
                StrokeThickness = 1,
                StrokeDashArray = [4, 4],
                Opacity = 0.65
            });
        }
        IEnumerable<ChannelControlPoint> points = lane.Points;
        if (ReferenceEquals(lane, _activeAutomationLane) && _automationPreview is { } edit)
            points = points.Where(point => point.Tick != _draggedControlPoint?.Tick && point.Tick != edit.Tick)
                .Append(edit).OrderBy(point => point.Tick);
        if (left == 0 && !points.Any(point => point.Tick == 0))
            points = points.Prepend(new ChannelControlPoint(0, defaultValue));

        double previousX = 0;
        double y = AutomationValueY(height, defaultValue);
        foreach (var point in points)
        {
            double x = TickX(point.Tick) - left;
            if (x >= width)
                break;
            double nextY = AutomationValueY(height, point.Value);
            if (x >= 0)
            {
                AddRect(lane.Canvas, previousX, y, x - previousX, 1, _palette.RulerMuted);
                if (point.Tick != 0)
                    AddRect(lane.Canvas, x, Math.Min(y, nextY), 1, Math.Abs(nextY - y), _palette.RulerMuted);
                bool opening = point.Tick == 0;
                var dot = new Ellipse { Width = opening ? 12 : 8, Height = opening ? 12 : 8,
                    Fill = opening ? _palette.Playhead : _palette.SelectedNote };
                Canvas.SetLeft(dot, opening ? 2 : x - dot.Width / 2);
                Canvas.SetTop(dot, nextY - dot.Height / 2);
                lane.Canvas.Children.Add(dot);
                previousX = x;
            }
            y = nextY;
        }
        AddRect(lane.Canvas, previousX, y, width - previousX, 1, _palette.RulerMuted);
    }

    private void AutomationPointerPressed(ChannelLane lane, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(lane.Graph);
        if (!_editingEnabled || !_loopRangeEditable || _automationPointerId.HasValue ||
            !point.Properties.IsLeftButtonPressed || point.Position.X >= EditorScroll.ViewportWidth ||
            !lane.Graph.CapturePointer(args.Pointer))
            return;

        _activeAutomationLane = lane;
        _automationPointerId = args.Pointer.PointerId;
        _automationPress = point.Position;
        double left = EditorScroll.HorizontalOffset;
        _openingControlDrag = left == 0 && _automationPress.X <= OpeningHandleWidth;
        var candidates = _openingControlDrag
            ? lane.Points.Where(item => item.Tick == 0).DefaultIfEmpty(new(0, SongTrackEditor.DefaultControlValue(lane.Control)))
            : lane.Points.Where(item => item.Tick != 0 && Math.Abs(TickX(item.Tick) - left - _automationPress.X) <= 6);
        foreach (var item in candidates)
        {
            if (Math.Abs(AutomationValueY(lane.Graph.Height, item.Value) - _automationPress.Y) > 7)
                continue;
            _draggedControlPoint = item;
            break;
        }
        PreviewAutomation(lane, point.Position);
        args.Handled = true;
    }

    private void AutomationPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_automationPointerId != args.Pointer.PointerId || _activeAutomationLane is not { } lane)
            return;
        PreviewAutomation(lane, args.GetCurrentPoint(lane.Graph).Position);
        args.Handled = true;
    }

    private void PreviewAutomation(ChannelLane lane, Point position)
    {
        double dx = Math.Abs(position.X - _automationPress.X);
        double dy = Math.Abs(position.Y - _automationPress.Y);
        long tick = _openingControlDrag ? 0 : _draggedControlPoint is { } previous && dx < 4 ? previous.Tick :
            TickOfCell(CellNearestTick(long.CreateSaturating(Math.Round(RulerTickAt(position.X)))));
        double height = lane.Graph.Height;
        byte center = SongTrackEditor.DefaultControlValue(ChannelControl.Pan);
        byte value = _draggedControlPoint is { } point && dy < 4 ? point.Value :
            lane.Control == ChannelControl.Pan && Math.Abs(position.Y - AutomationValueY(height, center)) <= PanCenterSnapDistance
                ? center
                : (byte)Math.Clamp(Math.Round(127 * (height - AutomationInset - position.Y) / (height - 2 * AutomationInset)), 0, 127);
        var preview = new ChannelControlPoint(tick, value);
        if (preview == _automationPreview)
            return;
        _automationPreview = preview;
        PaintChannelLane(lane);
    }

    private void AutomationPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_automationPointerId != args.Pointer.PointerId || _activeAutomationLane is not { } lane)
            return;
        PreviewAutomation(lane, args.GetCurrentPoint(lane.Graph).Position);
        var preview = _automationPreview;
        var original = _draggedControlPoint;
        CancelAutomationDrag();
        if (_editingEnabled && _loopRangeEditable && preview is { } edit && edit != original)
            ChannelPointEdited?.Invoke(lane.Control, edit.Tick, edit.Value, original?.Tick);
        args.Handled = true;
    }

    private void AutomationPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_automationPointerId == args.Pointer.PointerId)
            CancelAutomationDrag();
    }

    private void CancelAutomationDrag()
    {
        if (_activeAutomationLane is not { } lane)
            return;
        _activeAutomationLane = null;
        _automationPointerId = null;
        _draggedControlPoint = _automationPreview = null;
        lane.Graph.ReleasePointerCaptures();
        PaintChannelLane(lane);
    }

    private sealed record ChannelLane(ChannelControl Control, Grid Row, ColumnDefinition LabelColumn, Grid Graph, Canvas Canvas)
    {
        public ChannelControlPoint[] Points { get; set; } = [];
    }
}
