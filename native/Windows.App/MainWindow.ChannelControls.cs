using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Signal.Native.Engine;
using Windows.System;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private void RefreshChannelControls(EditableSongTrack track)
    {
        ChannelVolumeBox.Text = _trackEditor.GetOpeningControlValue(track.PrimaryChannel, ChannelControl.Volume).ToString();
        ChannelPanBox.Text = (_trackEditor.GetOpeningControlValue(track.PrimaryChannel, ChannelControl.Pan) - 64).ToString();
        RollView.SetChannelPoints(ChannelControl.Volume, _trackEditor.GetChannelControlPoints(track.PrimaryChannel, ChannelControl.Volume));
        RollView.SetChannelPoints(ChannelControl.Pan, _trackEditor.GetChannelControlPoints(track.PrimaryChannel, ChannelControl.Pan));
    }

    private void ChannelControlLostFocus(object sender, RoutedEventArgs args) => CommitOpeningControl((TextBox)sender);

    private void ChannelControlKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Enter)
            return;
        args.Handled = true;
        CommitOpeningControl((TextBox)sender);
    }

    private void CommitOpeningControl(TextBox field)
    {
        if (!CanSeek || _selectedTrack is not { } track)
            return;
        bool pan = ReferenceEquals(field, ChannelPanBox);
        var control = pan ? ChannelControl.Pan : ChannelControl.Volume;
        int offset = pan ? 64 : 0;
        byte current = _trackEditor.GetOpeningControlValue(track.PrimaryChannel, control);
        if (int.TryParse(field.Text, out int entered) && entered >= -offset && entered <= 127 - offset &&
            entered + offset != current)
            ApplyChannelEdit(track, control, 0, (byte)(entered + offset));
        else
            field.Text = (current - offset).ToString();
    }

    private void ShowVolumeLaneClicked(object sender, RoutedEventArgs args)
    {
        ShowChannelLane(ChannelControl.Volume);
        ChannelControlsFlyout.Hide();
    }

    private void ShowPanLaneClicked(object sender, RoutedEventArgs args)
    {
        ShowChannelLane(ChannelControl.Pan);
        ChannelControlsFlyout.Hide();
    }

    private void ShowChannelLane(ChannelControl control)
    {
        RollView.ShowChannelLane(control);
    }

    private void EditChannelPoint(ChannelControl control, long tick, byte value, long? previousTick)
    {
        if (CanSeek && _selectedTrack is { } track)
            ApplyChannelEdit(track, control, tick, value, previousTick);
    }

    private void ApplyChannelEdit(EditableSongTrack track, ChannelControl control, long tick, byte? value, long? previousTick = null)
    {
        if (_trackEditor.EditChannelControl(track, control, tick, value, previousTick) &&
            _engine is { PlaybackTick: long playbackTick } engine && _bank is not null)
            StartSongPlayback(engine, playbackTick, seek: true, refreshMix: true);
    }

    private async void ShowAutomationPoints(ChannelControl control)
    {
        if (!CanSeek || _selectedTrack is not { } track)
            return;

        SetFileBusy(true);
        try
        {
            int offset = control == ChannelControl.Pan ? 64 : 0;
            var list = new ListView { MaxHeight = 220, SelectionMode = ListViewSelectionMode.Single };
            var value = new NumberBox
            {
                Header = control.ToString(), Minimum = -offset, Maximum = 127 - offset,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
            };
            var apply = new Button { Content = "Apply value" };
            var remove = new Button { Content = "Remove point" };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(apply);
            buttons.Children.Add(remove);
            var panel = new StackPanel { Spacing = 8, MinWidth = 270 };
            panel.Children.Add(list);
            panel.Children.Add(value);
            panel.Children.Add(buttons);
            ChannelControlPoint[] points = [];
            void Reload(long? selectedTick = null)
            {
                points = _trackEditor.GetChannelControlPoints(track.PrimaryChannel, control);
                list.ItemsSource = points.Select(point =>
                {
                    var position = _trackEditor.MeterMap.PositionAt(point.Tick);
                    return $"Bar {position.Bar} · Beat {position.Beat} · Tick {point.Tick - position.BeatTick}     {point.Value - offset}";
                }).ToArray();
                int index = Array.FindIndex(points, point => point.Tick == selectedTick);
                list.SelectedIndex = index >= 0 ? index : points.Length > 0 ? 0 : -1;
                value.Visibility = buttons.Visibility = points.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
                if (points.Length == 0)
                    list.ItemsSource = new[] { "No points. Click the lane to add one." };
            }
            list.SelectionChanged += (_, _) =>
            {
                bool selected = (uint)list.SelectedIndex < (uint)points.Length;
                apply.IsEnabled = remove.IsEnabled = selected;
                if (selected)
                    value.Value = points[list.SelectedIndex].Value - offset;
            };
            apply.Click += (_, _) =>
            {
                if ((uint)list.SelectedIndex >= (uint)points.Length || !double.IsFinite(value.Value))
                    return;
                long tick = points[list.SelectedIndex].Tick;
                ApplyChannelEdit(track, control, tick, (byte)((int)Math.Round(value.Value) + offset));
                Reload(tick);
            };
            remove.Click += (_, _) =>
            {
                if ((uint)list.SelectedIndex >= (uint)points.Length)
                    return;
                ApplyChannelEdit(track, control, points[list.SelectedIndex].Tick, null);
                Reload();
            };
            Reload();
            await new ContentDialog
            {
                XamlRoot = WorkspaceRoot.XamlRoot,
                Title = $"{control} points · Channel {track.PrimaryChannel + 1}",
                Content = panel,
                CloseButtonText = "Done"
            }.ShowAsync();
        }
        finally
        {
            SetFileBusy(false);
        }
    }
}
