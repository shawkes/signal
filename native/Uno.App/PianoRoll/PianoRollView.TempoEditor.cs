using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Foundation;
using Windows.System;

namespace Signal.Native.Uno.App.PianoRoll;

public sealed partial class PianoRollView
{
    private Flyout? _tempoMarkerFlyout;

    public event Action<long, int?>? TempoMarkerEdited;
    public event Action? TempoUndoRequested;
    public bool CanUndoTempoEdit { get; set; }

    private void OpenTempoMarkerEditor(long tick)
    {
        if (!_tempoEditingEnabled || _tempoMap is not { } map || tick < 0)
            return;

        CloseTempoMarkerEditor();
        var current = map.Changes.Last(change => change.Tick <= tick);
        bool existing = current.Tick == tick;
        string displayedBpm = (60_000_000.0 / current.MicrosecondsPerQuarterNote)
            .ToString("0.###", CultureInfo.CurrentCulture);
        var position = _meterMap.PositionAt(tick);
        var input = new TextBox
        {
            Header = $"BPM at bar {position.Bar}, beat {position.Beat}, tick {tick - position.BeatTick}",
            Text = displayedBpm
        };
        var error = new TextBlock
        {
            Text = "Enter a positive BPM within the MIDI tempo range.",
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        var apply = new Button { Content = existing ? "Apply" : "Add tempo" };
        var cancel = new Button { Content = "Cancel" };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(apply);
        actions.Children.Add(cancel);
        var panel = new StackPanel { MinWidth = 185, MaxWidth = 330, Spacing = 8 };
        panel.Children.Add(input);
        panel.Children.Add(error);
        panel.Children.Add(actions);

        var flyout = new Flyout { Content = panel };
        _tempoMarkerFlyout = flyout;
        bool IsCurrent() => _tempoEditingEnabled && ReferenceEquals(_tempoMap, map) &&
                            ReferenceEquals(_tempoMarkerFlyout, flyout);

        int? ReadTempo()
        {
            // A rounded display value can stand for several distinct MIDI tempos.
            if (input.Text == displayedBpm)
                return current.MicrosecondsPerQuarterNote;
            if (!double.TryParse(input.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double bpm) ||
                !double.IsFinite(bpm) || bpm <= 0)
                return null;
            double value = Math.Round(60_000_000.0 / bpm, MidpointRounding.AwayFromZero);
            return value is >= 1 and <= 0xffffff ? (int)value : null;
        }

        void Apply()
        {
            if (!IsCurrent() || ReadTempo() is not int tempo)
                return;
            CloseTempoMarkerEditor();
            if (!existing || tempo != current.MicrosecondsPerQuarterNote)
                TempoMarkerEdited?.Invoke(tick, tempo);
        }

        input.TextChanged += (_, _) =>
        {
            apply.IsEnabled = ReadTempo().HasValue;
            error.Visibility = apply.IsEnabled ? Visibility.Collapsed : Visibility.Visible;
        };
        apply.Click += (_, _) => Apply();
        cancel.Click += (_, _) => CloseTempoMarkerEditor();
        if (existing && tick > 0)
        {
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                if (!IsCurrent())
                    return;
                CloseTempoMarkerEditor();
                TempoMarkerEdited?.Invoke(tick, null);
            };
            actions.Children.Add(remove);
        }
        if (CanUndoTempoEdit)
        {
            var undo = new Button { Content = "Undo tempo edit" };
            undo.Click += (_, _) =>
            {
                if (!IsCurrent())
                    return;
                CloseTempoMarkerEditor();
                TempoUndoRequested?.Invoke();
            };
            panel.Children.Add(undo);
        }
        input.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                Apply();
            }
        };
        panel.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                CloseTempoMarkerEditor();
            }
        };
        flyout.Opened += (_, _) =>
        {
            if (!IsCurrent())
                return;
            input.Focus(FocusState.Programmatic);
            input.SelectAll();
        };
        flyout.Closed += (_, _) =>
        {
            if (ReferenceEquals(_tempoMarkerFlyout, flyout))
                _tempoMarkerFlyout = null;
        };
        double x = Math.Clamp(TickX(tick) - EditorScroll.HorizontalOffset, 0, TempoInput.ActualWidth);
        flyout.ShowAt(TempoInput, new FlyoutShowOptions
        {
            Position = new Point(x, TempoInput.ActualHeight),
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft
        });
    }

    private void CloseTempoMarkerEditor()
    {
        var flyout = _tempoMarkerFlyout;
        _tempoMarkerFlyout = null;
        flyout?.Hide();
    }
}
