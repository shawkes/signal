using System.Globalization;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace Signal.Native.Uno.App.Controls;

internal readonly record struct ScrubRange(decimal Minimum, decimal Maximum, decimal Fallback = 0, decimal Step = 1);

/// <summary>Provides mouse adjustment beside a numeric input while the field keeps its native editing behavior.</summary>
internal sealed class NumericScrub : Button
{
    private const double PixelsPerStep = 4;
    private readonly Control _field;
    private readonly Func<ScrubRange> _getRange;
    private readonly Func<(decimal? Value, Action Restore)> _read;
    private readonly Action<decimal> _write;
    private readonly Action? _commit;
    private uint? _pointerId;
    private Point _pressedAt;
    private Axis? _axis;
    private double _lastPosition;
    private ScrubRange _range;
    private decimal _value;
    private Action? _restore;

    private NumericScrub(Control field, Func<ScrubRange> getRange, Func<(decimal?, Action)> read,
        Action<decimal> write, Action? commit)
    {
        _field = field;
        _getRange = getRange;
        _read = read;
        _write = write;
        _commit = commit;
        Content = "↔";
        FontSize = 18;
        MinWidth = MinHeight = 32;
        Padding = new Thickness(0);
        VerticalAlignment = VerticalAlignment.Bottom;
        IsTabStop = false;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        AutomationProperties.SetName(this, "Drag to adjust value");
        ToolTipService.SetToolTip(this, "Drag right or up to increase. Drag left or down to decrease.");
        SetBinding(IsEnabledProperty, new Binding { Source = field, Path = new PropertyPath(nameof(IsEnabled)) });
        Click += (_, _) => field.Focus(FocusState.Programmatic);
    }

    public bool IsDragging => _axis.HasValue;

    public static Grid Wrap(TextBox field, Func<ScrubRange> range, bool fractional = false)
    {
        var host = new Grid { Children = { field } };
        Attach(host, field, range, fractional);
        return host;
    }

    public static NumericScrub Attach(Grid host, TextBox field, Func<ScrubRange> range, bool fractional = false,
        Action? commit = null) =>
        AddGrip(host, new NumericScrub(field, range, () =>
        {
            string original = field.Text;
            return (Parse(original, fractional), () => field.Text = original);
        }, value => field.Text = value.ToString(fractional ? "G29" : "0", CultureInfo.CurrentCulture), commit));

    public static NumericScrub Attach(Grid host, NumberBox field, decimal fallback, Action? commit = null) =>
        AddGrip(host, new NumericScrub(field,
            () => new(decimal.CreateSaturating(field.Minimum), decimal.CreateSaturating(field.Maximum),
                fallback, (decimal)field.SmallChange),
            () =>
            {
                double original = field.Value;
                string text = field.Text;
                decimal? value = Parse(text, fractional: true) ??
                    (double.IsFinite(original) ? decimal.CreateSaturating(original) : null);
                return (value, () =>
                {
                    field.Value = original;
                    field.Text = text;
                });
            }, value => field.Value = (double)value, commit));

    private static decimal? Parse(string text, bool fractional) =>
        decimal.TryParse(text, fractional ? NumberStyles.Float : NumberStyles.Integer, CultureInfo.CurrentCulture,
            out decimal value) ? value : null;

    private static NumericScrub AddGrip(Grid host, NumericScrub grip)
    {
        host.ColumnSpacing = 4;
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(grip, 1);
        host.Children.Add(grip);
        return grip;
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(null);
        if (_pointerId.HasValue || !IsEnabled || args.Pointer.PointerDeviceType != PointerDeviceType.Mouse ||
            !point.Properties.IsLeftButtonPressed || !CapturePointer(args.Pointer))
        {
            base.OnPointerPressed(args);
            return;
        }

        _pointerId = args.Pointer.PointerId;
        _pressedAt = point.Position;
        args.Handled = true;
    }

    protected override void OnPointerMoved(PointerRoutedEventArgs args)
    {
        if (_pointerId != args.Pointer.PointerId)
        {
            base.OnPointerMoved(args);
            return;
        }

        UpdateValue(args.GetCurrentPoint(null).Position);
        args.Handled = true;
    }

    private void UpdateValue(Point point)
    {
        bool started = !IsDragging;
        if (started)
        {
            double horizontal = Math.Abs(point.X - _pressedAt.X);
            double vertical = Math.Abs(point.Y - _pressedAt.Y);
            if (Math.Max(horizontal, vertical) < PixelsPerStep)
                return;

            _axis = horizontal >= vertical ? Axis.Horizontal : Axis.Vertical;
            _lastPosition = PositionAlongAxis(_pressedAt);
            _range = _getRange();
            var (value, restore) = _read();
            _restore = restore;
            _value = Math.Clamp(value ?? _range.Fallback, _range.Minimum, _range.Maximum);
        }

        double position = PositionAlongAxis(point);
        double steps = Math.Truncate((position - _lastPosition) / PixelsPerStep);
        if (steps == 0)
            return;

        _lastPosition += steps * PixelsPerStep;
        decimal next = Math.Clamp(_value + (decimal)steps * _range.Step, _range.Minimum, _range.Maximum);
        if (started || next != _value)
        {
            _value = next;
            _write(next);
        }
    }

    private double PositionAlongAxis(Point point) => _axis == Axis.Horizontal ? point.X : -point.Y;

    protected override void OnPointerReleased(PointerRoutedEventArgs args)
    {
        if (_pointerId != args.Pointer.PointerId)
        {
            base.OnPointerReleased(args);
            return;
        }

        UpdateValue(args.GetCurrentPoint(null).Position);
        bool wasDragging = IsDragging;
        ClearDrag();
        ReleasePointerCapture(args.Pointer);
        args.Handled = true;
        if (wasDragging)
            _commit?.Invoke();
        else
            _field.Focus(FocusState.Programmatic);
    }

    protected override void OnPointerCaptureLost(PointerRoutedEventArgs args)
    {
        if (_pointerId == args.Pointer.PointerId)
            CancelDrag();
        base.OnPointerCaptureLost(args);
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs args)
    {
        if (_pointerId == args.Pointer.PointerId)
        {
            CancelDrag();
            ReleasePointerCapture(args.Pointer);
            args.Handled = true;
        }
        base.OnPointerCanceled(args);
    }

    private void CancelDrag()
    {
        _restore?.Invoke();
        ClearDrag();
    }

    private void ClearDrag()
    {
        _pointerId = null;
        _axis = null;
        _restore = null;
    }

    private enum Axis { Horizontal, Vertical }
}
