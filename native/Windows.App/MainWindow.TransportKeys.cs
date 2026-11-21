using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private void TransportKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || !CanSeek || !ReferenceEquals(WorkspaceTabs.SelectedItem, PianoRollTab) ||
            args.Key is not (VirtualKey.Space or VirtualKey.Home or VirtualKey.Left or VirtualKey.Right))
            return;

        bool horizontal = args.Key is VirtualKey.Left or VirtualKey.Right;
        bool shift = IsTransportModifierDown(VirtualKey.Shift);
        bool control = IsTransportModifierDown(VirtualKey.Control);
        if (IsTransportModifierDown(VirtualKey.Menu) || (!horizontal && (shift || control)) || (shift && control))
            return;

        for (var element = args.OriginalSource as DependencyObject; element is not null && element != WorkspaceTabs;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or RichEditBox or NumberBox or Selector or RangeBase or MenuBar)
                return;
            if (args.Key == VirtualKey.Space && element is ButtonBase { FocusState: FocusState.Keyboard } button &&
                button != PlayButton)
                return;
            if (element == RollView && (RollView.HasActiveGesture ||
                (horizontal && !shift && !control && RollView.HasNoteSelection)))
                return;
        }

        args.Handled = true;
        switch (args.Key)
        {
            case VirtualKey.Space:
                if (!args.KeyStatus.WasKeyDown)
                    ToggleSongPlayback();
                break;
            case VirtualKey.Home:
                RewindSong();
                break;
            case VirtualKey.Left:
            case VirtualKey.Right:
                StepPlayhead(forward: args.Key == VirtualKey.Right, byBar: shift);
                break;
        }
    }

    private static bool IsTransportModifierDown(VirtualKey key) =>
        (InputKeyboardSource.GetKeyStateForCurrentThread(key) & CoreVirtualKeyStates.Down) != 0;

    private void StepPlayhead(bool forward, bool byBar)
    {
        long? playingTick = _engine?.PlaybackTick;
        long tick = playingTick ?? _pausedTick ?? _cueTick;
        var meter = (playingTick.HasValue ? _playingSong?.MeterMap : null) ?? _trackEditor.MeterMap;
        if (forward)
        {
            SeekPlayhead(byBar ? meter.NextBarTick(tick) : meter.NextBeatTick(tick));
            return;
        }

        var position = meter.PositionAt(tick);
        long target = byBar ? position.BarTick : position.BeatTick;
        if (target > 0)
        {
            // During playback the audio clock advances between key presses. Skip the
            // current boundary if the playhead has only just passed it.
            long nextBeat = meter.NextBeatTick(target);
            long rewindWindow = byBar ? nextBeat - target : (nextBeat - target) / 2;
            if (tick == target || (playingTick.HasValue && tick - target < rewindWindow))
            {
                var previous = meter.PositionAt(target - 1);
                target = byBar ? previous.BarTick : previous.BeatTick;
            }
        }
        SeekPlayhead(target);
    }
}
