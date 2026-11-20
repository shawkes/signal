using Microsoft.UI.Xaml;
using Signal.Native.Engine;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private LoopRange? _loopRange;
    private bool _loopEnabled;
    private long _playingSongEndTick;

    private void LoopRangeSelected(LoopRange range) => ChangeLoop(range, _loopEnabled);

    private void LoopClicked(object sender, RoutedEventArgs args) =>
        ChangeLoop(_loopRange, LoopButton.IsChecked == true);

    private void ClearLoopRangeClicked(object sender, RoutedEventArgs args) => ChangeLoop(null, _loopEnabled);

    private void ChangeLoop(LoopRange? range, bool enabled)
    {
        try
        {
            if (_engine?.PlaybackTick is not null)
                _engine.SetPlaybackLoop(EnabledLoopRange(range, enabled, _playingSongEndTick));

            _loopRange = range;
            _loopEnabled = enabled;
            _playbackError = null;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            _playbackError = $"Could not set the loop. {error.Message}";
        }

        UpdateLoopDisplay();
        UpdatePlaybackControls();
    }

    private LoopRange? EnabledLoopRange(LoopRange? range, bool enabled, long endTick) =>
        enabled ? range ?? new LoopRange(0, endTick > 0 ? endTick : _songTempo.TicksPerQuarter) : null;

    private void UpdateLoopDisplay()
    {
        var range = _loopRange ?? EnabledLoopRange(null, _loopEnabled, _playing ? _playingSongEndTick : _songEndTick);
        RollView.SetLoopRange(range, _loopEnabled, showHandles: _loopRange is not null);
        LoopButton.IsChecked = _loopEnabled;
        ClearLoopRangeButton.Visibility = _loopRange is null ? Visibility.Collapsed : Visibility.Visible;
        LoopRangeText.Text = _loopRange is { } selected
            ? $"Range {BeatLabel(selected.StartTick)}–{BeatLabel(selected.EndTick)}"
            : _loopEnabled ? "Repeating the whole song" : "Drag the ruler to set a loop range";
    }

    private string BeatLabel(long tick)
    {
        long beat = tick / _songTempo.TicksPerQuarter;
        return $"{beat / 4 + 1}.{beat % 4 + 1}";
    }
}
