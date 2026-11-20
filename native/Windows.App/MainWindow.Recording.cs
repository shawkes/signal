using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.Engine;
using Signal.Native.SoundFont;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private CapturePurpose _capturePurpose;
    private readonly Dictionary<int, List<SongNote>> _capturedSongNotes = [];
    private int? _countInRemaining;
    private LoopRange? _recordingLoop;
    private long? _overdubPass;
    private readonly HeldRecordingNote[] _heldPreview = new HeldRecordingNote[LatencyEngine.MaxHeldRecordingSegments];
    private RecordingTestTiming? _placementTiming;
    private double _placementOffset;
    private bool _placementQuantized;
    private bool _placementClickContinuous;
    private double? _placementSuggestedOffset;

    private bool CaptureInProgress => _recording || _finishingTake;
    private bool SongCaptureInProgress => CaptureInProgress && _capturePurpose == CapturePurpose.Song;
    private bool CalibrationInProgress => CaptureInProgress && _capturePurpose == CapturePurpose.Calibration;

    private string SongCaptureStatus
    {
        get
        {
            string destination = MultiMidiRouting ? "Multi" : _selectedTrack?.Name ?? "";
            return _recordingLoop is null ? $"Recording · {destination}" :
                _overdubPass is { } pass ? $"Overdubbing · Pass {pass} · {destination}" : $"Overdubbing · {destination}";
        }
    }

    private double RecordingOffset => double.IsFinite(OffsetBox.Value) ? OffsetBox.Value : 10;
    // Off is followed by quarter notes and successive halvings.
    private int RecordingGrid => QuantizeBox.SelectedIndex > 0 ? 4 << (QuantizeBox.SelectedIndex - 1) : 0;

    private void SongRecordClicked(object sender, RoutedEventArgs args)
    {
        if (_engine is not { } engine || _bank is not { } bank || _selectedTrack is not { } track ||
            CaptureInProgress || _playing || _loadingBank || _fileBusy)
            return;

        try
        {
            var loop = EnabledLoopRange(_loopRange, _loopEnabled, _songEndTick);
            ConfigureLiveMidi(engine);
            var overdub = loop.HasValue ? new SoundFontPiano(bank, engine.SampleRate, generalMidi: true) : null;
            var backing = new SoundFontPiano(bank, engine.SampleRate, generalMidi: true);
            var song = SnapshotSong();
            long takeId = engine.RecordSong(song, backing,
                CountInBox.IsChecked == true, RecordingOffset, RecordingGrid,
                loop: loop, overdubSynth: overdub, overdubSetup: _liveSetup);

            BeginTake(takeId, CapturePurpose.Song, track);
            ShowTrackRecordingPreview();
            _recordingLoop = loop;
            _playingSongEndTick = _songEndTick;
            _playbackError = null;
            UpdatePlayhead();
            WatchTransportFrames(true);
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
        _notePump?.BeginTake(takeId);
        _takeId = takeId;
        _capturePurpose = purpose;
        _recording = true;
        UpdateSettingsMetronome();
        _captureTrack = track;
        if (purpose != CapturePurpose.Calibration)
        {
            PlacementResultPanel.Visibility = Visibility.Collapsed;
            _placementSuggestedOffset = null;
            _placementTiming = purpose == CapturePurpose.PlacementTest ? new RecordingTestTiming() : null;
            if (_placementTiming is not null)
            {
                _placementOffset = RecordingOffset;
                _placementQuantized = RecordingGrid != 0;
                _placementClickContinuous = true;
            }
            _rows.Clear();
            _capturedNotes.Clear();
            _capturedSongNotes.Clear();
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
        UpdateSettingsMetronome();
        UpdateControls();
    }

    private void FinishTake(LatencyEngine engine)
    {
        _finishingTake = false;
        _recordingLoop = null;
        if (_capturePurpose == CapturePurpose.Calibration)
        {
            FinishCalibration();
            ConfigureLiveMidi(engine);
        }
        else if (_capturePurpose == CapturePurpose.Song)
        {
            CommitSongTake();
            RollView.EndRecordingPreview();
        }
        else
        {
            if (_placementTiming is { } timing)
            {
                ShowPlacementResult(timing.Summarize(playedWithClick: _placementClickContinuous && !_audioFailed));
                _placementTiming = null;
            }
            AddTakeToPianoRoll(engine.TempoMap.TicksPerQuarter);
        }
        UpdatePlayhead();
        UpdateLoopDisplay();
        UpdateControls();
    }

    private void ShowPlacementResult(RecordingTestResult? result)
    {
        PlacementResultPanel.Visibility = Visibility.Visible;
        PlacementLatencyDetailText.Visibility = PlacementTimingText.Visibility = Visibility.Collapsed;
        _placementSuggestedOffset = result?.SuggestedOffsetMilliseconds is { } suggested
            ? RoundRecordingOffset(suggested) : null;
        UpdatePlacementOffsetAction();
        PlacementOffsetText.Visibility = result is null ? Visibility.Collapsed : Visibility.Visible;
        PlacementCalibrationLink.Visibility = _placementSuggestedOffset is null ? Visibility.Visible : Visibility.Collapsed;
        if (result is null)
        {
            PlacementLatencyText.Text = "No notes received. Start another test and play a MIDI note or use Play test note.";
            return;
        }

        if (result.MedianMonitorDelayMilliseconds is { } median && result.P95MonitorDelayMilliseconds is { } p95)
        {
            PlacementLatencyText.Text = $"Estimated monitoring latency · {median:0.0} ms median";
            string percentile = result.NoteCount > 1
                ? $"95% of notes were scheduled within {p95:0.0} ms of input. "
                : "";
            PlacementLatencyDetailText.Text = percentile +
                "Timing runs from MIDI receipt to the first synth frame scheduled for output. Keypress-to-speaker latency needs an external measurement.";
            PlacementLatencyDetailText.Visibility = Visibility.Visible;
        }
        else
        {
            PlacementLatencyText.Text = "Monitoring latency unavailable for this take.";
        }

        string offset = _placementOffset.ToString("+0.0;-0.0;0.0");
        string quantization = _placementQuantized ? " (before grid snapping)" : "";
        PlacementTimingText.Text = $"Median beat placement was {DescribeBeatTiming(result.MedianRawMilliseconds)} on input " +
            $"and {DescribeBeatTiming(result.MedianAdjustedMilliseconds)} after the {offset} ms offset{quantization}.";
        PlacementTimingText.Visibility = Visibility.Visible;
        if (_placementSuggestedOffset is { } target)
        {
            double shift = RoundRecordingOffset(target - _placementOffset);
            string effect = shift == 0 ? "This matches the offset used for this take." :
                $"Future recordings will land {Math.Abs(shift):0.0} ms {(shift > 0 ? "earlier" : "later")} than with this take’s setting.";
            PlacementOffsetText.Text = $"Suggested recording offset · {target:+0.0;-0.0;0.0} ms. {effect}";
        }
        else
        {
            PlacementOffsetText.Text = _placementClickContinuous
                ? $"For an offset suggestion, record at least {TapTimingTest.MinimumTaps} evenly timed single notes, one per click."
                : "Keep the metronome on for the whole test to get an offset suggestion.";
        }
    }

    private static double RoundRecordingOffset(double value) => Math.Round(value, 1, MidpointRounding.AwayFromZero);

    private void ApplyPlacementOffsetClicked(object sender, RoutedEventArgs args)
    {
        if (!CaptureInProgress && !_fileBusy && _placementSuggestedOffset is { } offset)
            ApplyRecordingOffset(offset);
    }

    private void RecordingOffsetChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_trackEditor is not null)
        {
            UpdatePlacementOffsetAction();
            UpdateCalibrationControls();
        }
    }

    private void UpdatePlacementOffsetAction()
    {
        PlacementOffsetButton.Visibility = _placementSuggestedOffset is null ? Visibility.Collapsed : Visibility.Visible;
        bool matches = _placementSuggestedOffset is { } offset && IsSavedOffset(offset);
        PlacementOffsetButton.Content = matches ? "Offset saved" : "Apply suggested offset";
        PlacementOffsetButton.IsEnabled = _placementSuggestedOffset.HasValue && !matches && !CaptureInProgress && !_fileBusy;
    }

    private static string DescribeBeatTiming(double milliseconds)
    {
        double rounded = Math.Round(milliseconds, 1, MidpointRounding.AwayFromZero);
        if (rounded == 0)
            return "on the beat";
        return $"{Math.Abs(rounded):0.0} ms {(rounded < 0 ? "early" : "late")}";
    }

    private void ShowOffsetCalibrationClicked(object sender, RoutedEventArgs args)
    {
        if (!CalibrationInputBox.IsEnabled)
            return;
        CalibrationInputBox.Focus(FocusState.Programmatic);
        CalibrationInputBox.StartBringIntoView();
    }

    private int TakeTrackIndex(int destinationTrack) =>
        destinationTrack >= 0 ? destinationTrack : FindTrackIndex(_captureTrack);

    private void CaptureRoutedNote(SongNote note, int destinationTrack)
    {
        int trackIndex = TakeTrackIndex(destinationTrack);
        if ((uint)trackIndex >= (uint)_trackEditor.Tracks.Count)
            return;
        var track = _trackEditor.Tracks[trackIndex];
        if (destinationTrack < 0)
            note = note with { Channel = track.PrimaryChannel };
        if (!_capturedSongNotes.TryGetValue(trackIndex, out var notes))
            _capturedSongNotes.Add(trackIndex, notes = []);
        notes.Add(note);
        if (ReferenceEquals(track, _selectedTrack))
            RollView.AppendRecordingPreview(note);
    }

    private void ShowTrackRecordingPreview()
    {
        if (_selectedTrack is not { } track)
            return;
        RollView.BeginRecordingPreview(track.Document);
        if (_capturedSongNotes.TryGetValue(FindTrackIndex(track), out var notes))
        {
            foreach (var note in notes)
                RollView.AppendRecordingPreview(note);
        }
    }

    private void ShowHeldRecordingNotes(int heldCount)
    {
        int selected = FindTrackIndex(_selectedTrack);
        int visible = 0;
        for (int i = 0; i < heldCount; i++)
        {
            var note = _heldPreview[i];
            if (TakeTrackIndex(note.DestinationTrack) == selected)
                _heldPreview[visible++] = note;
        }
        RollView.SetHeldRecordingNotes(_heldPreview.AsSpan(0, visible));
    }

    private void CommitSongTake()
    {
        foreach (var (trackIndex, notes) in _capturedSongNotes)
        {
            var track = _trackEditor.Tracks[trackIndex];
            track.Document.Append(notes);
            var first = notes.MinBy(note => note.StartTick);
            if (_firstImportedNote is not { } current || first.StartTick < current.Note.StartTick)
                _firstImportedNote = (track, first);
        }
        _capturedSongNotes.Clear();
    }

    private enum CapturePurpose { PlacementTest, Song, Calibration }
}
