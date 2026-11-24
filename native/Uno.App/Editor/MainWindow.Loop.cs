using Microsoft.UI.Xaml;
using Signal.Native.Engine;

namespace Signal.Native.Uno.App;

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
        if (CaptureInProgress)
            return;

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
        enabled ? range ?? LoopRange.WholeSong(endTick, _trackEditor.MeterMap) : null;

    private long PlaybackSeekEndTick(long songEndTick) => _loopEnabled && _loopRange is null
        ? LoopRange.WholeSong(songEndTick, _trackEditor.MeterMap).EndTick
        : songEndTick;

    private void UpdateLoopDisplay()
    {
        bool songTake = SongCaptureInProgress;
        var range = songTake ? _recordingLoop ?? _loopRange :
            _loopRange ?? EnabledLoopRange(null, _loopEnabled, _playing ? _playingSongEndTick : _songEndTick);
        RollView.SetLoopRange(range, songTake ? _recordingLoop is not null : _loopEnabled,
            showHandles: _loopRange is not null && !CaptureInProgress);
        LoopButton.IsChecked = _loopEnabled;
        ClearLoopRangeButton.Visibility = _loopRange is null ? Visibility.Collapsed : Visibility.Visible;
        LoopRangeText.Text = songTake
            ? _recordingLoop is { } loop ? $"Loop {BeatLabel(loop.StartTick)}–{BeatLabel(loop.EndTick)}" : "Recording from bar 1"
            : _loopRange is { } selected
            ? $"Range {BeatLabel(selected.StartTick)}–{BeatLabel(selected.EndTick)}"
            : _loopEnabled ? "Repeating the whole song" : "Right-click the Bars ruler for loop markers";
    }

    private string BeatLabel(long tick)
    {
        var position = _trackEditor.MeterMap.PositionAt(tick);
        return $"{position.Bar}.{position.Beat}";
    }
}
