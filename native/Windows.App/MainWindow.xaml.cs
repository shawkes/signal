using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using Signal.Native.Engine;
using Signal.Native.SoundFont;
using Signal.Native.Windows.App.Settings;
using Signal.Native.Windows.Audio;
using Signal.Native.Windows.Midi;
using Windows.Graphics;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<TakeRow> _rows = [];
    private readonly List<SongNote> _capturedNotes = [];
    private readonly DispatcherQueueTimer _uiTimer;
    private bool _watchingTransportFrames;
    private readonly string? _startupMidiPath;
    private WasapiOutput? _output;
    private RecordedNotePump? _notePump;
    private LatencyEngine? _engine;
    private WinMmInput? _midiInput;
    private readonly RecordingPreferences _preferences = RecordingPreferences.ForCurrentUser();
    private MidiInputPreference? _preferredMidiInput;
    private double? _savedRecordingOffset;
    private MeltySynth.SoundFont? _bank;
    private bool _updatingDevices = true;
    private bool _closed;
    private bool _recording;
    private bool _finishingTake;
    private bool _playing;
    private bool _testNoteHeld;
    private bool _loadingBank;
    private bool _audioFailed;
    private int _noteCount;
    private EditableSongTrack? _captureTrack;
    private long _takeId;
    private (EditableSongTrack Track, SongNote Note)? _firstImportedNote;
    private string? _playbackError;

    public MainWindow(string? startupMidiPath = null)
    {
        InitializeComponent();
        OffsetBox.NumberFormatter = new global::Windows.Globalization.NumberFormatting.DecimalFormatter { FractionDigits = 1 };
        _savedRecordingOffset = _preferences.ReadOffset();
        if (_savedRecordingOffset is { } offset)
            OffsetBox.Value = offset;
        QuantizeBox.ItemsSource = RecordingDivisions.Select(division => division == 0 ? "Off" : $"1/{division}").ToArray();
        QuantizeBox.SelectedIndex = Math.Max(0, Array.IndexOf(RecordingDivisions, _preferences.ReadQuantizationDivision() ?? 0));
        QuantizeBox.SelectionChanged += RecordingQuantizeChanged;
        _preferredMidiInput = _preferences.ReadMidiInput();
        _startupMidiPath = startupMidiPath;
        AppWindow.Resize(new SizeInt32(1200, 780));
        TakeList.ItemsSource = _rows;
        RollView.LoopRangeSelected += LoopRangeSelected;
        RollView.PlayheadPositionSelected += SeekPlayhead;
        RollView.TempoMarkerEdited += EditTempoMarker;
        RollView.TempoMarkerMoved += MoveTempoMarker;
        RollView.TempoUndoRequested += UndoTempoMarkerEdit;
        WorkspaceTabs.Loaded += OnLoaded;
        AppWindow.Closing += WindowClosing;
        Closed += OnClosed;
        _uiTimer = DispatcherQueue.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(125);
        _uiTimer.Tick += (_, _) => UpdateReadout();
        ReplaceSong(CreateEmptySong());
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        WorkspaceTabs.Loaded -= OnLoaded;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (RollView.IsLoaded && ReferenceEquals(WorkspaceTabs.SelectedItem, PianoRollTab))
                RollView.Focus(FocusState.Programmatic);
        });
        RefreshDevices();
        OpenAudio();
        OpenMidi();
        _uiTimer.Start();
        if (_startupMidiPath is { } path)
        {
            SetFileBusy(true);
            try
            {
                await ReadSongAsync(Path.GetFullPath(path));
            }
            catch (Exception error)
            {
                ShowFileError("Could not open MIDI", error);
            }
            finally
            {
                SetFileBusy(false);
            }
        }
        if (!_closed)
            await LoadBankAsync(Path.Combine(AppContext.BaseDirectory, "A320U.sf2"));
    }

    private void RefreshDevices()
    {
        var audioId = (AudioDeviceBox.SelectedItem as ComboBoxItem)?.Tag as string;
        _updatingDevices = true;
        try
        {
            AudioDeviceBox.Items.Clear();
            AudioDeviceBox.Items.Add(new ComboBoxItem { Content = "System default" });
            AudioDeviceBox.SelectedIndex = 0;
            foreach (var device in WasapiOutput.EnumerateDevices())
            {
                var item = new ComboBoxItem { Content = device.Name, Tag = device.Id };
                AudioDeviceBox.Items.Add(item);
                if (device.Id == audioId)
                    AudioDeviceBox.SelectedItem = item;
            }
        }
        catch (Exception error) when (error is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            AudioFormatText.Text = $"Audio device enumeration failed. {error.Message}";
        }
        finally
        {
            try
            {
                PopulateMidiInputs(WinMmInput.EnumerateDevices());
            }
            finally
            {
                _updatingDevices = false;
            }
        }
    }

    private void PopulateMidiInputs(IReadOnlyList<MidiInputDevice> devices)
    {
        MidiDeviceBox.Items.Clear();
        var restored = _preferredMidiInput?.Find(devices);
        MidiDeviceBox.Items.Add(new ComboBoxItem
        {
            Content = devices.Count == 0 ? "No MIDI input detected" : "No MIDI input selected"
        });
        MidiDeviceBox.SelectedIndex = 0;
        foreach (var device in devices)
        {
            var item = new ComboBoxItem { Content = device.Name, Tag = device };
            MidiDeviceBox.Items.Add(item);
            if (device == restored)
                MidiDeviceBox.SelectedItem = item;
        }
        if (restored is null && _preferredMidiInput is { } missing)
        {
            var unavailable = new ComboBoxItem { Content = $"{missing.Name} (unavailable)", IsEnabled = false };
            MidiDeviceBox.Items.Add(unavailable);
            MidiDeviceBox.SelectedItem = unavailable;
        }
    }

    private void RefreshDevicesClicked(object sender, RoutedEventArgs args)
    {
        RefreshDevices();
        OpenAudio();
        OpenMidi();
    }

    private void AudioSettingsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_updatingDevices)
            OpenAudio();
    }

    private void OpenAudio()
    {
        _playbackError = null;
        _output?.Dispose();
        _output = null;
        _notePump?.Dispose();
        _notePump = null;
        Volatile.Write(ref _engine, null);
        _audioFailed = false;
        ShowAudioCounters(null, null);
        WasapiOutput? candidate = null;
        try
        {
            var preference = PeriodBox.SelectedIndex switch
            {
                1 => AudioPeriodPreference.Conservative,
                2 => AudioPeriodPreference.Default,
                _ => AudioPeriodPreference.Minimum
            };
            var id = (AudioDeviceBox.SelectedItem as ComboBoxItem)?.Tag as string;
            LatencyEngine? renderer = null;
            candidate = new WasapiOutput(id, (samples, firstFrame, timestamp) =>
                renderer!.Render(samples, firstFrame, new AudioClockAnchor(timestamp, firstFrame), Stopwatch.GetTimestamp()), preference);
            renderer = new LatencyEngine(candidate.SampleRate, SilentMonitorSynth.Instance)
            {
                MetronomeEnabled = SongMetronomeButton.IsChecked == true
            };
            ConfigureLiveMidi(renderer);
            candidate.Start();
            string period = FormatFrames(candidate.PeriodFrames, candidate.SampleRate);
            string capacity = FormatFrames(candidate.BufferFrames, candidate.SampleRate);
            AudioFormatText.Text = $"{candidate.Mode} · {candidate.SampleRate:N0} Hz · {candidate.Channels} ch\n"
                + $"Period {period} · Buffer {capacity}";
            _notePump = new RecordedNotePump(renderer);
            _output = candidate;
            Volatile.Write(ref _engine, renderer);
        }
        catch (Exception error)
        {
            candidate?.Dispose();
            AudioFormatText.Text = $"Audio unavailable. {error.Message}";
        }
        UpdatePlayhead();
        UpdateControls();
    }

    private void MidiInputChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_updatingDevices || MidiDeviceBox.SelectedItem is not ComboBoxItem item)
            return;

        _preferredMidiInput = item.Tag is MidiInputDevice device ? new(device.Name, device.InterfacePath) : null;
        TrySavePreference(() => _preferences.SaveMidiInput(_preferredMidiInput));
        OpenMidi();
    }

    private bool IsSavedOffset(double offset) => _savedRecordingOffset == offset && RecordingOffset == offset;

    private void ApplyRecordingOffset(double offset)
    {
        if (OffsetBox.Value != offset)
        {
            OffsetBox.Value = offset;
            return;
        }
        SaveRecordingOffset(offset);
        UpdatePlacementOffsetAction();
        UpdateCalibrationControls();
    }

    private void SaveRecordingOffset(double offset)
    {
        if (offset is >= -500 and <= 500 && _savedRecordingOffset != offset &&
            TrySavePreference(() => _preferences.SaveOffset(offset)))
            _savedRecordingOffset = offset;
    }

    private bool TrySavePreference(Action save)
    {
        try
        {
            save();
            PreferenceErrorText.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            PreferenceErrorText.Text = $"Could not save this preference for the next launch. {error.Message}";
            PreferenceErrorText.Visibility = Visibility.Visible;
            return false;
        }
    }

    private void OpenMidi()
    {
        _midiInput?.Dispose();
        _midiInput = null;
        Volatile.Read(ref _engine)?.RequestMidiReset();
        MidiErrorText.Visibility = Visibility.Collapsed;
        if ((MidiDeviceBox.SelectedItem as ComboBoxItem)?.Tag is not MidiInputDevice device)
        {
            UpdateMidiMonitorInput();
            UpdateCalibrationControls();
            return;
        }

        try
        {
            _midiInput = new WinMmInput(device.Id, ReceiveMidi);
        }
        catch (Win32Exception error)
        {
            MidiErrorText.Text = error.Message;
            MidiErrorText.Visibility = Visibility.Visible;
        }
        UpdateMidiMonitorInput();
        UpdateCalibrationControls();
    }

    private void ReceiveMidi(MidiMessage message)
    {
        if (message.Status < 0xF0 && Volatile.Read(ref _engine) is { } engine)
        {
            int tapInput = Volatile.Read(ref _activeTapInput);
            if (tapInput == MidiTapInput)
                engine.TryEnqueue(message);
            else if (tapInput != SpaceTapInput)
                engine.TryEnqueueLiveMidi(message);
        }
        Volatile.Read(ref _midiMonitorBuffer)?.Capture(message);
    }

    private async void LoadSoundFontClicked(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker(AppWindow.Id);
            picker.FileTypeFilter.Add(".sf2");
            var file = await picker.PickSingleFileAsync();
            if (file is not null && !_closed)
                await LoadBankAsync(file.Path);
        }
        catch (Exception error)
        {
            SoundFontText.Text = $"Could not open SoundFont picker. {error.Message}";
        }
    }

    private async Task LoadBankAsync(string path)
    {
        _loadingBank = true;
        UpdateControls();
        try
        {
            var bank = await Task.Run(() => new MeltySynth.SoundFont(path));
            if (_closed)
                return;

            _bank = bank;
            if (_engine is { } engine)
                ConfigureLiveMidi(engine);
            RefreshTrackSound();
            string name = Path.GetFileName(path);
            SoundFontText.Text = _output is { SampleRate: < 16000 or > 192000 }
                ? $"{name} requires a 16–192 kHz output"
                : name;
        }
        catch (Exception error)
        {
            SoundFontText.Text = $"Could not load {Path.GetFileName(path)}.";
            ToolTipService.SetToolTip(SoundFontText, error.Message);
        }
        finally
        {
            _loadingBank = false;
            if (!_closed)
                UpdateControls();
        }
    }

    private void SongMetronomeClicked(object sender, RoutedEventArgs args)
    {
        if (_engine is { } engine)
            engine.MetronomeEnabled = SongMetronomeButton.IsChecked == true;
    }

    private async void TestNoteClicked(object sender, RoutedEventArgs args)
    {
        if (_engine is not { } engine || CaptureInProgress || _selectedTrack is not { } track)
            return;

        _testNoteHeld = true;
        UpdateControls();
        const byte note = 60;
        try
        {
            engine.TryEnqueue(new MidiMessage(Stopwatch.GetTimestamp(), (byte)(0x90 | track.PrimaryChannel), note, 100));
            await Task.Delay(160);
            engine.TryEnqueue(new MidiMessage(Stopwatch.GetTimestamp(), (byte)(0x80 | track.PrimaryChannel), note, 0));
        }
        finally
        {
            _testNoteHeld = false;
            if (!_closed)
                UpdateControls();
        }
    }

    private void ShowTakeClicked(object sender, RoutedEventArgs args)
    {
        WorkspaceTabs.SelectedItem = PianoRollTab;
        if (_firstImportedNote is { } take)
        {
            SelectTrack(take.Track);
            RollView.RevealTick(take.Note.StartTick, take.Note.NoteNumber);
        }
    }

    private void PlayClicked(object sender, RoutedEventArgs args) => ToggleSongPlayback();

    private void ToggleSongPlayback()
    {
        if (!PlayButton.IsEnabled || _engine is not { } engine || _bank is null || !CanSeek)
            return;

        if (engine.PlaybackTick is { } tick)
        {
            _pausedTick = tick;
            engine.StopPlayback();
            UpdatePlayhead();
            return;
        }

        StartSongPlayback(engine, _pausedTick ?? _cueTick);
    }

    private void StartSongPlayback(LatencyEngine engine, long tick, bool seek = false, bool refreshMix = false)
    {
        try
        {
            bool reuse = seek && !refreshMix && _playingSong is not null;
            long endTick = reuse ? _playingSongEndTick : _songEndTick;
            var song = reuse ? _playingSong! : _trackEditor.SnapshotBackingSong(endTick);
            var loop = EnabledLoopRange(_loopRange, _loopEnabled, endTick);
            long start = seek ? Math.Min(tick, Math.Max(0, endTick - 1)) : tick < endTick ? tick : 0;
            if (!engine.PlaySong(song, new SoundFontPiano(_bank!, engine.SampleRate, generalMidi: true), loop, start,
                allowSilent: true))
                return;

            _pausedTick = null;
            _playingSong = song;
            _playingSongEndTick = endTick;
            _playbackError = null;
            UpdatePlayhead(reveal: seek);
            WatchTransportFrames(true);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            _playbackError = error.Message;
            UpdatePlaybackControls();
        }
    }

    private long _cueTick;
    private long? _pausedTick;
    private Song? _playingSong;
    private bool CanSeek => !CaptureInProgress && !_fileBusy;

    private void SeekPlayhead(long tick)
    {
        if (!CanSeek)
            return;

        var playingEngine = _engine is { PlaybackTick: not null } engine && _bank is not null ? engine : null;
        _cueTick = Math.Clamp(tick, 0, playingEngine is null ? _songEndTick : _playingSongEndTick);
        if (playingEngine is not null)
            StartSongPlayback(playingEngine, _cueTick, seek: true);
        else
        {
            if (_pausedTick.HasValue)
                _pausedTick = _cueTick;
            UpdatePlayhead(reveal: true);
        }
    }

    private void StopClicked(object sender, RoutedEventArgs args)
    {
        if (SongCaptureInProgress && _engine is { } engine)
            StopTake(engine);
        else if (CanSeek)
            RewindSong();
    }

    private void JumpToStartClicked(object sender, RoutedEventArgs args)
    {
        if (CanSeek)
            RewindSong();
    }

    private void RewindSong()
    {
        _cueTick = 0;
        StopSongPlayback(reveal: true);
    }

    private void StopSongPlayback(bool reveal = false)
    {
        _pausedTick = null;
        _playingSong = null;
        _engine?.StopPlayback();
        UpdatePlayhead(reveal);
        if (_trackEditor is not null)
            UpdatePlaybackControls();
    }

    private void WatchTransportFrames(bool enabled)
    {
        if (_watchingTransportFrames == enabled)
            return;
        _watchingTransportFrames = enabled;
        if (enabled)
            CompositionTarget.Rendering += OnTransportFrame;
        else
            CompositionTarget.Rendering -= OnTransportFrame;
    }

    private void OnTransportFrame(object? sender, object args) => UpdatePlayhead();

    private void UpdatePlayhead(bool reveal = false)
    {
        long? tick;
        int? countIn = null;
        long? pass = null;
        if (SongCaptureInProgress && _engine is { } recordingEngine)
        {
            var position = _audioFailed ? default :
                recordingEngine.ReadRecordingDisplay(_takeId, Stopwatch.GetTimestamp(), _heldPreview);
            // Keep a released key visible until the completed-note consumer has delivered it.
            bool heldReady = _audioFailed || (_notePump?.HasCaughtUp ?? true);
            DrainRecordedNotes(recordingEngine);
            if (heldReady)
                ShowHeldRecordingNotes(position.HeldNoteCount);
            tick = position.Tick;
            if (_recording)
            {
                countIn = position.CountInBeatsRemaining;
                pass = position.LoopPass;
            }
        }
        else
        {
            tick = _audioFailed ? null : _engine?.PlaybackTick;
        }
        RollView.SetPlayhead(SongCaptureInProgress ? tick : tick ?? _pausedTick ?? _cueTick, reveal);
        if (_trackEditor is not null)
            UpdateMeterButton(tick);
        if (tick is null && !SongCaptureInProgress)
            WatchTransportFrames(false);

        if (_playing == tick.HasValue && _countInRemaining == countIn && _overdubPass == pass)
            return;

        bool playbackChanged = _playing != tick.HasValue;
        _playing = tick.HasValue;
        _countInRemaining = countIn;
        _overdubPass = pass;
        UpdatePlaybackControls();
        UpdateCalibrationControls();
        if (playbackChanged)
            UpdateLoopDisplay();
    }

    private void UpdateReadout()
    {
        if (_output is not { } output || _engine is not { } engine)
            return;

        var diagnostics = output.Diagnostics;
        ShowAudioCounters(diagnostics, engine);
        if (!_audioFailed && diagnostics.Failure is { } failure)
        {
            _audioFailed = true;
            // Rendering has failed and cannot acknowledge a stop. Collect every already published release.
            _notePump?.Dispose();
            _finishingTake |= _recording;
            _recording = false;
            AudioFormatText.Text = $"Audio stopped. {failure}";
            UpdatePlayhead();
            UpdateControls();
        }

        // Observe completion before draining so the final pass also sees any held notes closed on stop.
        bool takeFinished = _finishingTake && (_audioFailed || _notePump?.CompletedTakeId == _takeId);
        DrainRecordedNotes(engine);
        if (takeFinished)
            FinishTake(engine);
        if (CalibrationInProgress)
        {
            if (_recording && _tapTest?.Complete == true)
                StopTake(engine);
            UpdateCalibrationControls();
        }

        if (_takeId != 0 && _capturePurpose != CapturePurpose.Calibration)
        {
            string noteLabel = _noteCount == 1 ? "note" : "notes";
            string status = _audioFailed ? "Audio stopped" : _countInRemaining is { } beat ? $"Count-in {beat}" :
                _recording ? "Recording" :
                _finishingTake ? "Finishing take" : "Take complete";
            TakeStatusText.Text = $"{status} · {_noteCount} {noteLabel}";
        }
    }

    private void DrainRecordedNotes(LatencyEngine engine)
    {
        if (_notePump is not { } pump)
            return;
        while (pump.TryReadNote(out var note))
        {
            if (note.TakeId != _takeId)
                continue;

            if (_capturePurpose == CapturePurpose.Calibration)
            {
                _tapTest?.AddTap(note.NearestBeat, note.RawMillisecondsFromBeat);
                continue;
            }

            _noteCount++;
            _placementTiming?.Add(note);
            SongNote songNote = note.ToSongNote() with { LengthTicks = Math.Max(1, note.LengthTicks) };
            if (_capturePurpose == CapturePurpose.Song)
                CaptureRoutedNote(songNote, note.DestinationTrack);
            else
                _capturedNotes.Add(songNote);
            if (_rows.Count == 64)
                _rows.RemoveAt(_rows.Count - 1);
            _rows.Insert(0, TakeRow.From(note, engine.SampleRate));
        }
    }

    private void ShowAudioCounters(AudioDiagnostics? diagnostics, LatencyEngine? engine)
    {
        SetValue(RenderPassesValue, diagnostics?.RenderPasses);
        SetValue(UnderrunsValue, diagnostics?.Underruns);
        SetValue(ClockFailuresValue, diagnostics?.ClockFailures);
        SetValue(MidiDropsValue, engine?.DroppedMidiMessages);
        SetValue(CaptureDropsValue, engine?.DroppedRecordedNotes);

        static void SetValue(TextBlock field, long? count)
        {
            string value = count?.ToString("N0") ?? "—";
            if (field.Text != value)
                field.Text = value;
        }
    }

    private void AddTakeToPianoRoll(int ticksPerQuarter)
    {
        if (_capturedNotes.Count == 0 || _captureTrack is not { } track)
            return;

        var document = track.Document;
        var first = _capturedNotes.MinBy(note => note.StartTick);
        long captureBar = 4L * ticksPerQuarter;
        long sourceBar = first.StartTick / captureBar * captureBar;
        long rollEnd = document.Notes
            .Select(note => note.Note.StartTick + note.Note.LengthTicks)
            .DefaultIfEmpty()
            .Max();
        long targetBar = rollEnd == 0 ? 0 : _trackEditor.MeterMap.NextBarTick(rollEnd - 1);
        double scale = SongTempo.TicksPerQuarter / (double)ticksPerQuarter;
        document.Append(_capturedNotes.Select(ShiftNote));
        var imported = ShiftNote(first);
        _firstImportedNote = (track, imported);
        _capturedNotes.Clear();
        if (WorkspaceTabs.SelectedIndex == 0 &&
            ReferenceEquals(_selectedTrack, track))
            RollView.RevealTick(imported.StartTick, imported.NoteNumber);

        SongNote ShiftNote(SongNote note)
        {
            return note with
            {
                StartTick = targetBar + (long)Math.Round((note.StartTick - sourceBar) * scale, MidpointRounding.AwayFromZero),
                LengthTicks = Math.Max(1, (long)Math.Round(note.LengthTicks * scale, MidpointRounding.AwayFromZero)),
                Channel = track.PrimaryChannel
            };
        }
    }

    private void UpdateControls()
    {
        bool audioReady = _output is not null && !_audioFailed;
        bool pianoReady = audioReady && _bank is not null && _output!.SampleRate is >= 16000 and <= 192000;
        bool takeInProgress = CaptureInProgress;
        bool placementTest = _recording && _capturePurpose == CapturePurpose.PlacementTest;
        SoundFontButton.IsEnabled = !_loadingBank && !takeInProgress;
        TestNoteButton.IsEnabled = pianoReady && !_testNoteHeld && !takeInProgress;
        TestMetronomeBox.IsEnabled = audioReady && !takeInProgress;
        SongMetronomeButton.IsEnabled = audioReady && !CalibrationInProgress;
        RecordButton.IsEnabled = audioReady && !_fileBusy && (!takeInProgress || placementTest);
        RecordButton.Content = placementTest ? "Stop recording" : "Record test";
        ShowTakeButton.IsEnabled = _firstImportedNote is { } take && FindTrackIndex(take.Track) >= 0 &&
            !takeInProgress && !_fileBusy;
        AudioDeviceBox.IsEnabled = !takeInProgress;
        PeriodBox.IsEnabled = !takeInProgress;
        MidiDeviceBox.IsEnabled = !takeInProgress;
        RefreshButton.IsEnabled = !takeInProgress;
        OffsetBox.IsEnabled = !takeInProgress;
        UpdatePlacementOffsetAction();
        PlacementCalibrationLink.IsEnabled = !takeInProgress && !_fileBusy;
        QuantizeBox.IsEnabled = !takeInProgress;
        UpdateFileControls();
        UpdateTrackControls();
        UpdatePlaybackControls();
        UpdateCalibrationControls();
    }

    private void UpdatePlaybackControls()
    {
        bool audioReady = _output is { SampleRate: >= 16000 and <= 192000 } && !_audioFailed;
        bool takeInProgress = CaptureInProgress;
        bool playingSong = _playing && !takeInProgress;
        PlayIcon.Symbol = playingSong ? Symbol.Pause : Symbol.Play;
        PlayLabel.Text = playingSong ? "Pause" : _pausedTick.HasValue ? "Resume" : "Play song";
        AutomationProperties.SetName(PlayButton, PlayLabel.Text);
        ToolTipService.SetToolTip(PlayButton, playingSong ? "Pause (Space)" : "Play all tracks from the playhead (Space)");
        PlayButton.IsEnabled = audioReady && _bank is not null && !takeInProgress && !_fileBusy &&
            (playingSong || _trackEditor.Tracks.Any(track => track.Document.Notes.Count > 0));
        SongRecordButton.IsEnabled = audioReady && _bank is not null && _selectedTrack is not null &&
            !_loadingBank && !_playing && !takeInProgress && !_fileBusy;
        CountInBox.IsEnabled = !takeInProgress && !_fileBusy;
        StopButton.IsEnabled = audioReady && (_playing || _pausedTick.HasValue || (SongCaptureInProgress && _recording)) && !_finishingTake;
        JumpToStartButton.IsEnabled = CanSeek;
        LoopButton.IsEnabled = !_fileBusy && !takeInProgress;
        ClearLoopRangeButton.IsEnabled = !_fileBusy && !takeInProgress;
        RollView.SetLoopRangeEditable(!takeInProgress);
        string status = _playbackError ?? (_finishingTake ? "Finishing take…" :
            _countInRemaining is { } beat ? $"Count-in {beat}" :
            SongCaptureInProgress ? SongCaptureStatus :
            _playing ? "Playing song" : _pausedTick.HasValue ? "Paused" :
            CalibrationInProgress ? "Tap test in progress" :
            takeInProgress ? "Recording a test take" :
            !audioReady ? "Check the audio output in Recording settings" :
                _loadingBank ? "Loading SoundFont…" :
                _bank is null ? "Load a SoundFont in Recording settings" : "");
        PlaybackStatusText.Text = status;
        PlaybackStatusText.Visibility = status.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _uiTimer.Stop();
        WatchTransportFrames(false);
        _midiMonitor?.Close();
        _midiInput?.Dispose();
        Volatile.Write(ref _engine, null);
        _output?.Dispose();
        _notePump?.Dispose();
    }

    private static string FormatFrames(int frames, int sampleRate) =>
        $"{frames:N0} frames ({frames * 1000.0 / sampleRate:0.00} ms)";
}

public sealed record TakeRow(string Pitch, string Beat, string Raw, string Adjusted, string Tick, string Length)
{
    private static readonly string[] NoteNames = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

    public static TakeRow From(RecordedNote note, int sampleRate) => new(
        $"{NoteNames[note.NoteNumber % 12]}{note.NoteNumber / 12 - 1}",
        (note.NearestBeat + 1).ToString(),
        note.RawMillisecondsFromBeat.ToString("+0.00;-0.00;0.00"),
        note.MillisecondsFromBeat.ToString("+0.00;-0.00;0.00"),
        note.StartTick.ToString(),
        (note.PlayedLengthFrames * 1000.0 / sampleRate).ToString("0.00"));
}
