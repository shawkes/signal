using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private bool _transportSpaceDown;

    private void TransportKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Handled || AudioMidiSettingsOpen)
            return;

        if (args.Key == VirtualKey.Space)
        {
            HandleTransportSpace(args);
            return;
        }

        if (!CanSeek || args.Key is not (VirtualKey.Home or VirtualKey.Left or VirtualKey.Right))
            return;

        bool horizontal = args.Key is VirtualKey.Left or VirtualKey.Right;
        bool shift = IsTransportModifierDown(VirtualKey.Shift);
        bool control = IsTransportModifierDown(VirtualKey.Control);
        if (IsTransportModifierDown(VirtualKey.Menu) || (!horizontal && (shift || control)) || (shift && control))
            return;

        for (var element = args.OriginalSource as DependencyObject; element is not null && element != WorkspaceRoot;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or RichEditBox or NumberBox or Selector or RangeBase || element == MainMenu)
                return;
            if (element == RollView && (RollView.HasActiveGesture ||
                (horizontal && !shift && !control && RollView.HasNoteSelection)))
                return;
        }

        args.Handled = true;
        switch (args.Key)
        {
            case VirtualKey.Home:
                RewindSong();
                break;
            case VirtualKey.Left:
            case VirtualKey.Right:
                StepPlayhead(forward: args.Key == VirtualKey.Right, byBar: shift);
                break;
        }
    }

    private void HandleTransportSpace(KeyRoutedEventArgs args)
    {
        if (_transportSpaceDown)
        {
            args.Handled = true;
            return;
        }
        if (IsTransportModifierDown(VirtualKey.Control) || IsTransportModifierDown(VirtualKey.Shift) ||
            IsTransportModifierDown(VirtualKey.Menu) || IsTransportModifierDown(VirtualKey.LeftWindows) ||
            IsTransportModifierDown(VirtualKey.RightWindows))
            return;

        var focused = args.OriginalSource as DependencyObject;
        if (focused is null && WorkspaceRoot.XamlRoot is { } root)
            focused = FocusManager.GetFocusedElement(root) as DependencyObject;
        for (var element = focused; element is not null && element != WorkspaceRoot;
             element = VisualTreeHelper.GetParent(element))
        {
            if (element is TextBox or RichEditBox or PasswordBox or NumberBox or
                ComboBox { IsDropDownOpen: true } or MenuFlyoutPresenter or ContentDialog or Popup)
                return;
        }

        // Own both halves of the key press so a focused toolbar button cannot act on release.
        args.Handled = true;
        _transportSpaceDown = true;
        if (!args.KeyStatus.WasKeyDown && CanSeek && !RollView.HasActiveGesture)
            ToggleSongPlayback();
    }

    private void TransportKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Space || !_transportSpaceDown)
            return;

        _transportSpaceDown = false;
        args.Handled = true;
    }

    private void TransportWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
            _transportSpaceDown = false;
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
