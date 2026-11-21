using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.Engine;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private SongMeter? _displayedMeter;

    private void UpdateMeterButton(long? playbackTick)
    {
        long tick = playbackTick ?? (SongCaptureInProgress ? _recordingLoop?.StartTick ?? 0 : _pausedTick ?? _cueTick);
        var meter = _trackEditor.MeterMap.MeterAt(tick);
        if (meter == _displayedMeter)
            return;

        _displayedMeter = meter;
        string signature = $"{meter.Numerator}/{meter.Denominator}";
        MeterButton.Content = $"Meter {signature}";
        AutomationProperties.SetName(MeterButton, $"Edit time signature, {signature}");
    }

    private async void MeterClicked(object sender, RoutedEventArgs args)
    {
        if (_fileBusy || CaptureInProgress)
            return;

        var map = _trackEditor.MeterMap;
        int current = map.IndexAt(_engine?.PlaybackTick ?? 0);
        StopSongPlayback();
        SetFileBusy(true);
        try
        {
            var point = new ComboBox { Header = "Time signature", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var change in map.Changes)
            {
                long bar = map.PositionAt(change.Tick).Bar;
                point.Items.Add($"Bar {bar}   ·   {change.Meter.Numerator}/{change.Meter.Denominator}");
            }
            point.Items.Add("Add at a later bar…");
            var start = new TextBox { Header = "Starting bar" };
            var beats = new TextBox { Header = "Beats per bar", Width = 132 };
            var unit = new ComboBox { Header = "Beat unit", Width = 132 };
            int[] denominators = [1, 2, 4, 8, 16, 32, 64, 128];
            foreach (int value in denominators)
                unit.Items.Add($"1/{value}");

            var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            fields.Children.Add(beats);
            fields.Children.Add(unit);
            var undo = new Button { Content = "Undo time signature edit", IsEnabled = _trackEditor.CanUndoMeterEdit };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 12, MinWidth = 280 };
            content.Children.Add(point);
            content.Children.Add(start);
            content.Children.Add(fields);
            content.Children.Add(error);
            content.Children.Add(undo);

            var dialog = new ContentDialog
            {
                XamlRoot = WorkspaceTabs.XamlRoot,
                Title = "Time signatures",
                Content = content,
                PrimaryButtonText = "Apply",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };
            bool undoRequested = false;
            undo.Click += (_, _) => { undoRequested = true; dialog.Hide(); };
            bool IsAdding() => point.SelectedIndex == map.Changes.Count;

            string? ReadFields(out long tick, out int numerator)
            {
                tick = 0;
                if (!int.TryParse(beats.Text, out numerator) || numerator is < 1 or > 255)
                    return "Enter 1 to 255 beats per bar.";
                if (unit.SelectedIndex < 0)
                    return "Choose a beat unit.";
                if (!IsAdding())
                {
                    tick = map.Changes[point.SelectedIndex].Tick;
                    return null;
                }
                if (!long.TryParse(start.Text, out long bar) || bar < 2)
                    return "Enter a starting bar of 2 or later.";
                tick = map.TickAtBar(bar);
                if (tick == long.MaxValue || map.PositionAt(tick).Bar != bar)
                    return "That bar is outside the song’s tick range.";
                return map.Changes[map.IndexAt(tick)].Tick == tick ? "That bar already has a time signature." : null;
            }

            void Validate()
            {
                if (point.SelectedIndex < 0)
                    return;
                string? message = ReadFields(out _, out _);
                dialog.IsPrimaryButtonEnabled = message is null;
                error.Text = message ?? "";
                error.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
            }

            point.SelectionChanged += (_, _) =>
            {
                bool add = IsAdding();
                var change = map.Changes[add ? map.Changes.Count - 1 : point.SelectedIndex];
                start.Visibility = add ? Visibility.Visible : Visibility.Collapsed;
                start.Text = ((Int128)map.PositionAt(change.Tick).Bar + (add ? 1 : 0)).ToString();
                beats.Text = change.Meter.Numerator.ToString();
                unit.SelectedIndex = Array.IndexOf(denominators, change.Meter.Denominator);
                dialog.PrimaryButtonText = add ? "Add" : "Apply";
                dialog.SecondaryButtonText = !add && point.SelectedIndex > 0 ? "Remove" : "";
                Validate();
            };
            start.TextChanged += (_, _) => Validate();
            beats.TextChanged += (_, _) => Validate();
            unit.SelectionChanged += (_, _) => Validate();
            point.SelectedIndex = current;

            var result = await dialog.ShowAsync();
            if (undoRequested)
                _trackEditor.UndoMeterEdit();
            else if (result == ContentDialogResult.Secondary && point.SelectedIndex > 0 && !IsAdding())
                _trackEditor.SetMeterChanges(map.Changes.Where((_, i) => i != point.SelectedIndex));
            else if (result == ContentDialogResult.Primary && ReadFields(out long tick, out int numerator) is null)
            {
                var meter = new SongMeter(numerator, denominators[unit.SelectedIndex]);
                _trackEditor.SetMeterChanges(map.Changes.Where(change => change.Tick != tick).Append(new MeterChange(tick, meter)));
            }
        }
        catch (Exception error)
        {
            ShowFileError("Could not edit time signatures", error);
        }
        finally
        {
            if (!_closed)
                SetFileBusy(false);
        }
    }
}
