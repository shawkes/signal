using Microsoft.UI.Xaml;
using Signal.Native.Engine;
using Signal.Native.SoundFont;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private CapturePurpose _capturePurpose;
    private int _monitorChannel = -1;
    private int? _countInRemaining;
    private LoopRange? _recordingLoop;
    private long? _overdubPass;

    private bool CaptureInProgress => _recording || _finishingTake;
    private bool SongCaptureInProgress => CaptureInProgress && _capturePurpose == CapturePurpose.Song;
    private bool CalibrationInProgress => CaptureInProgress && _capturePurpose == CapturePurpose.Calibration;

    private string SongCaptureStatus => _recordingLoop is null
        ? $"Recording · {_captureTrack?.Name}"
        : _overdubPass is { } pass ? $"Overdubbing · Pass {pass} · {_captureTrack?.Name}"
            : $"Overdubbing · {_captureTrack?.Name}";

    private double RecordingOffset => double.IsFinite(OffsetBox.Value) ? OffsetBox.Value : 10;
    private int RecordingGrid => QuantizeBox.SelectedIndex switch { 1 => 4, 2 => 8, 3 => 16, _ => 0 };

    private void SongRecordClicked(object sender, RoutedEventArgs args)
    {
        if (_engine is not { } engine || _bank is not { } bank || _selectedTrack is not { } track ||
            CaptureInProgress || _playing || _loadingBank || _fileBusy)
            return;

        try
        {
            var loop = EnabledLoopRange(_loopRange, _loopEnabled, _songEndTick);
            var monitor = new SoundFontPiano(bank, engine.SampleRate, generalMidi: true);
            var sound = _trackEditor.GetTrackSound(track);
            SongChannelMessage[] setup =
            [
                new(0, (byte)(0xb0 | track.PrimaryChannel), 0,
                    (byte)Math.Clamp(sound.Bank - (track.IsDrum ? 128 : 0), 0, 127)),
                new(0, (byte)(0xc0 | track.PrimaryChannel), sound.Program, 0)
            ];
            foreach (var message in setup)
                monitor.ProcessMidi(message.Status, message.Data1, message.Data2);

            var overdub = loop.HasValue ? new SoundFontPiano(bank, engine.SampleRate, generalMidi: true) : null;
            long takeId = engine.RecordSong(SnapshotSong(), new SoundFontPiano(bank, engine.SampleRate, generalMidi: true),
                CountInBox.IsChecked == true, RecordingOffset, RecordingGrid,
                loop: loop, overdubSynth: overdub, overdubSetup: setup);

            engine.SetSynth(monitor);
            Volatile.Write(ref _monitorChannel, track.PrimaryChannel);
            BeginTake(takeId, CapturePurpose.Song, track);
            _recordingLoop = loop;
            _playingSongEndTick = _songEndTick;
            _playbackError = null;
            UpdatePlayhead();
            _playheadTimer.Start();
            UpdateLoopDisplay();
            UpdateControls();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            _playbackError = error.Message;
            UpdatePlaybackControls();
        }
    }

    private void RecordClicked(object sender, RoutedEventArgs args)
    {
        if (_engine is not { } engine || _finishingTake || _fileBusy)
            return;

        if (CaptureInProgress)
        {
            if (_capturePurpose == CapturePurpose.PlacementTest)
                StopTake(engine);
            return;
        }

        StopSongPlayback();
        BeginTake(engine.SetRecording(true, RecordingOffset, RecordingGrid), CapturePurpose.PlacementTest, _selectedTrack);
        MetronomeSwitch.IsOn = true;
        UpdateReadout();
        UpdateControls();
    }

    private void BeginTake(long takeId, CapturePurpose purpose, EditableSongTrack? track = null)
    {
        _takeId = takeId;
        _capturePurpose = purpose;
        _recording = true;
        _captureTrack = track;
        if (purpose != CapturePurpose.Calibration)
        {
            _rows.Clear();
            _capturedNotes.Clear();
            _firstImportedNote = null;
            _noteCount = 0;
        }
    }

    private void StopTake(LatencyEngine engine)
    {
        if (!_recording)
            return;

        if (_capturePurpose == CapturePurpose.Song)
            engine.StopSongRecording();
        else
            engine.SetRecording(false, 0);
        _recording = false;
        _finishingTake = true;
        UpdateControls();
    }

    private void FinishTake(LatencyEngine engine)
    {
        _finishingTake = false;
        _recordingLoop = null;
        if (_capturePurpose == CapturePurpose.Calibration)
        {
            FinishCalibration();
        }
        else
        {
            AddTakeToPianoRoll(engine.TempoMap.TicksPerQuarter);
        }

        if (_capturePurpose != CapturePurpose.PlacementTest)
        {
            Volatile.Write(ref _monitorChannel, -1);
            ApplySoundFont(engine, engine.SampleRate);
        }
        UpdatePlayhead();
        UpdateLoopDisplay();
        UpdateControls();
    }

    private void EnqueueMonitoredMidi(LatencyEngine engine, MidiMessage message)
    {
        int channel = Volatile.Read(ref _monitorChannel);
        if (channel >= 0)
            message = message with { Status = (byte)((message.Status & 0xf0) | channel) };
        engine.TryEnqueue(message);
    }

    private enum CapturePurpose { PlacementTest, Song, Calibration }
}
