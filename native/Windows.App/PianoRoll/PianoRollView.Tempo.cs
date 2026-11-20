using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Signal.Native.Engine;
using Windows.Foundation;

namespace Signal.Native.Windows.App.PianoRoll;

public sealed partial class PianoRollView
{
    private TempoMap? _tempoMap;
    private bool _tempoEditingEnabled = true;
    private uint? _tempoPointerId;
    private double _tempoPressX;
    private TempoChange? _pressedTempo;
    private bool _tempoDragging;
    private long? _tempoPreviewTick;

    public event Action<long, long>? TempoMarkerMoved;

    public void SetTempoMap(TempoMap map)
    {
        if (ReferenceEquals(_tempoMap, map))
            return;

        CancelTempoGesture();
        TempoInput.ReleasePointerCaptures();
        CloseTempoMarkerEditor();
        _tempoMap = map;
        UpdateSurfaceExtent();
        RefreshTempoLane();
    }

    public void SetTempoEditingEnabled(bool enabled)
    {
        if (_tempoEditingEnabled == enabled)
            return;
        _tempoEditingEnabled = enabled;
        if (!enabled)
        {
            CancelTempoGesture();
            TempoInput.ReleasePointerCaptures();
            CloseTempoMarkerEditor();
        }
        TempoInput.Opacity = enabled ? 1 : 0.65;
    }

    private void RefreshTempoLane()
    {
        TempoCanvas.Children.Clear();
        double width = EditorScroll.ViewportWidth;
        if (_tempoMap is null || width <= 0)
            return;
        TempoCanvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, 32) };

        var changes = _tempoMap.Changes;
        double left = EditorScroll.HorizontalOffset;
        // The preceding marker's label may extend into the left side of the viewport.
        int first = Math.Max(0, FirstTempoAtOrAfter(left) - 1);
        for (int i = first; i < changes.Count; i++)
        {
            var change = changes[i];
            double x = TickX(change.Tick) - left;
            if (x >= width)
                break;
            double room = i + 1 < changes.Count ? TickX(changes[i + 1].Tick) - left - x : width - x;
            if (room < 1 && i + 1 < changes.Count && Math.Floor(x) == Math.Floor(x + room))
                continue;
            if (_tempoPreviewTick.HasValue && _pressedTempo?.Tick == change.Tick)
                continue;
            DrawTempoFlag(change, x, Math.Min(room, width - x), preview: false);
        }

        if (_tempoPreviewTick is long preview && _pressedTempo is { } pressed)
        {
            double x = TickX(preview) - left;
            DrawTempoFlag(pressed with { Tick = preview }, x, width - x, preview: true);
        }
    }

    private static string TempoLabel(TempoChange change) => $"{60_000_000.0 / change.MicrosecondsPerQuarterNote:0.###} BPM";

    private static double TempoFlagWidth(string label) => 16 + 7.5 * label.Length;

    private void DrawTempoFlag(TempoChange change, double x, double room, bool preview)
    {
        string label = TempoLabel(change);
        double width = Math.Min(TempoFlagWidth(label), Math.Max(0, room - 1));
        if (x + Math.Max(width, 1) <= 0 || room <= 0)
            return;

        var accent = preview ? _palette.SelectedNote : _palette.Playhead;
        AddRect(TempoCanvas, x, 4, Math.Min(2, room), 27, accent);
        if (width < 22)
        {
            if (width > 2)
                AddRect(TempoCanvas, x, 4, width, 4, accent);
            return;
        }

        var flag = new Border
        {
            Width = width, Height = 21,
            Background = _palette.NaturalLane,
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0, 3, 3, 0),
            Child = new TextBlock
            {
                Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold,
                Foreground = accent, Margin = new Thickness(5, 0, 2, 0),
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            }
        };
        AutomationProperties.SetName(flag, $"{label}, tick {change.Tick}");
        Canvas.SetLeft(flag, x);
        Canvas.SetTop(flag, 4);
        TempoCanvas.Children.Add(flag);
    }

    // Lower bound in the sorted map. Rendering and hit testing use the same pixel coordinates.
    private int FirstTempoAtOrAfter(double contentX)
    {
        var changes = _tempoMap!.Changes;
        int low = 0, high = changes.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (TickX(changes[middle].Tick) < contentX)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private TempoChange? HitTempoFlag(double contentX, double y)
    {
        if (y < 2 || y > 31)
            return null;
        var changes = _tempoMap!.Changes;
        int next = FirstTempoAtOrAfter(contentX);
        if (next < changes.Count && TickX(changes[next].Tick) - contentX <= 2)
            return changes[next];
        if (next == 0)
            return null;
        var previous = changes[next - 1];
        double start = TickX(previous.Tick);
        if (contentX - start <= 3)
            return previous;
        double end = EditorScroll.HorizontalOffset + EditorScroll.ViewportWidth;
        if (next < changes.Count)
            end = Math.Min(end, TickX(changes[next].Tick));
        double width = Math.Min(TempoFlagWidth(TempoLabel(previous)), end - start - 1);
        double bottom = width < 22 ? 9 : 26;
        return y <= bottom && contentX < start + width ? previous : null;
    }

    private long SnapTempoTick(double contentX)
    {
        double x = Math.Clamp(contentX, 0, Surface.Width);
        long cell = CellAtPosition(x);
        long left = TickOfCell(cell);
        if (cell == long.MaxValue)
            return left;
        long right = TickOfCell(cell + 1);
        return x - TickX(left) < TickX(right) - x ? left : right;
    }

    private void TempoPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        if (!_editingEnabled || !_tempoEditingEnabled || _tempoMap is null || _document is null ||
            _tempoPointerId.HasValue || _rulerPointerId.HasValue || _pointerId.HasValue)
            return;
        var point = args.GetCurrentPoint(TempoInput);
        double x = point.Position.X;
        double contentX = EditorScroll.HorizontalOffset + x;
        if (!point.Properties.IsLeftButtonPressed || x < 0 || x >= EditorScroll.ViewportWidth ||
            contentX >= Surface.Width || !TempoInput.CapturePointer(args.Pointer))
            return;

        _tempoPointerId = args.Pointer.PointerId;
        _tempoPressX = contentX;
        _pressedTempo = HitTempoFlag(contentX, point.Position.Y);
        args.Handled = true;
    }

    private void TempoPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_tempoPointerId != args.Pointer.PointerId)
            return;
        UpdateTempoDrag(args.GetCurrentPoint(TempoInput).Position.X);
        args.Handled = true;
    }

    private void UpdateTempoDrag(double x)
    {
        double contentX = EditorScroll.HorizontalOffset + Math.Clamp(x, 0, EditorScroll.ViewportWidth);
        double delta = contentX - _tempoPressX;
        if (!_tempoDragging && Math.Abs(delta) < 4)
            return;
        _tempoDragging = true;
        if (_pressedTempo is not { Tick: > 0 } pressed)
            return;

        // Move the anchor by the pointer displacement, even when the label was grabbed at its far end.
        long candidate = SnapTempoTick(TickX(pressed.Tick) + delta);
        var changes = _tempoMap!.Changes;
        int next = FirstTempoAtOrAfter(TickX(candidate));
        bool occupied = candidate != pressed.Tick && next < changes.Count && changes[next].Tick == candidate;
        long? preview = occupied || candidate == 0 ? null : candidate;
        if (_tempoPreviewTick == preview)
            return;
        _tempoPreviewTick = preview;
        RefreshTempoLane();
    }

    private void TempoPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_tempoPointerId != args.Pointer.PointerId)
            return;
        UpdateTempoDrag(args.GetCurrentPoint(TempoInput).Position.X);
        var original = _pressedTempo;
        var target = _tempoPreviewTick;
        bool dragged = _tempoDragging;
        CancelTempoGesture();
        TempoInput.ReleasePointerCapture(args.Pointer);
        args.Handled = true;

        if (!dragged)
            OpenTempoMarkerEditor(original?.Tick ?? SnapTempoTick(_tempoPressX));
        else if (original is { } change && target is long tick && tick != change.Tick)
            TempoMarkerMoved?.Invoke(change.Tick, tick);
    }

    private void TempoPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        if (_tempoPointerId == args.Pointer.PointerId)
            CancelTempoGesture();
    }

    private void CancelTempoGesture()
    {
        _tempoPointerId = null;
        _tempoDragging = false;
        _pressedTempo = null;
        if (_tempoPreviewTick.HasValue)
        {
            _tempoPreviewTick = null;
            RefreshTempoLane();
        }
    }
}
