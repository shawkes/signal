using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Signal.Native.Engine;
using Signal.Native.SoundFont;
using Signal.Native.Windows.Audio;
using Signal.Native.Windows.Midi;
using Windows.Graphics;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<TakeRow> _rows = [];
    private readonly List<SongNote> _capturedNotes = [];
    private readonly DispatcherQueueTimer _uiTimer;
    private readonly DispatcherQueueTimer _playheadTimer;
    private readonly string? _startupMidiPath;
    private WasapiOutput? _output;
    private LatencyEngine? _engine;
    private WinMmInput? _midiInput;
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
    private int _captureTrack;
    private long _takeId;
    private (int Track, SongNote Note)? _firstImportedNote;
    private string? _playbackError;

    public MainWindow(string? startupMidiPath = null)
    {
        InitializeComponent();
        _startupMidiPath = startupMidiPath;
        AppWindow.Resize(new SizeInt32(1120, 760));
        TakeList.ItemsSource = _rows;
        WorkspaceTabs.Loaded += OnLoaded;
        AppWindow.Closing += WindowClosing;
        Closed += OnClosed;
        _uiTimer = DispatcherQueue.CreateTimer();
        _uiTimer.Interval = TimeSpan.FromMilliseconds(125);
        _uiTimer.Tick += (_, _) => UpdateReadout();
        _playheadTimer = DispatcherQueue.CreateTimer();
        _playheadTimer.Interval = TimeSpan.FromMilliseconds(33);
        _playheadTimer.Tick += (_, _) => UpdatePlayhead();
        ReplaceSong(new Song(_songTempo, [new SongTrack("Track 1", [])]));
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        WorkspaceTabs.Loaded -= OnLoaded;
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
        var midiId = (MidiDeviceBox.SelectedItem as ComboBoxItem)?.Tag as MidiInputDevice;
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
            MidiDeviceBox.Items.Clear();
            var midiDevices = WinMmInput.EnumerateDevices();
            MidiDeviceBox.Items.Add(new ComboBoxItem
            {
                Content = midiDevices.Count == 0 ? "No MIDI input detected" : "No MIDI input selected"
            });
            MidiDeviceBox.SelectedIndex = 0;
            foreach (var device in midiDevices)
            {
                var item = new ComboBoxItem { Content = device.Name, Tag = device };
                MidiDeviceBox.Items.Add(item);
                if (device == midiId)
                    MidiDeviceBox.SelectedItem = item;
            }
            _updatingDevices = false;
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
        Volatile.Write(ref _engine, null);
        _audioFailed = false;
        AudioCountersText.Text = "";
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
                renderer!.Render(samples, firstFrame, new AudioClockAnchor(timestamp, firstFrame)), preference);
            renderer = new LatencyEngine(candidate.SampleRate, SilentMonitorSynth.Instance)
            {
                MetronomeEnabled = MetronomeSwitch.IsOn
            };
            ApplySoundFont(renderer, candidate.SampleRate);
            candidate.Start();
            _output = candidate;
            Volatile.Write(ref _engine, renderer);

            string period = FormatFrames(candidate.PeriodFrames, candidate.SampleRate);
            string capacity = FormatFrames(candidate.BufferFrames, candidate.SampleRate);
            AudioFormatText.Text = $"{candidate.Mode} · {candidate.SampleRate:N0} Hz · {candidate.Channels} ch\n"
                + $"Period {period} · Buffer {capacity}";
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
        if (!_updatingDevices)
            OpenMidi();
    }

    private void OpenMidi()
    {
        _midiInput?.Dispose();
        _midiInput = null;
        Volatile.Read(ref _engine)?.RequestMidiReset();
        MidiErrorText.Visibility = Visibility.Collapsed;
        if ((MidiDeviceBox.SelectedItem as ComboBoxItem)?.Tag is not MidiInputDevice device)
            return;

        try
        {
            _midiInput = new WinMmInput(device.Id, ReceiveMidi);
        }
        catch (Win32Exception error)
        {
            MidiErrorText.Text = error.Message;
            MidiErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ReceiveMidi(MidiMessage message) => Volatile.Read(ref _engine)?.TryEnqueue(message);

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

            SoundFontPiano? piano = _output is { SampleRate: >= 16000 and <= 192000 } output
                ? new SoundFontPiano(bank, output.SampleRate)
                : null;
            _bank = bank;
            string name = Path.GetFileName(path);
            SoundFontText.Text = _output is not null && piano is null
                ? $"{name} requires a 16–192 kHz output"
                : name;
            if (piano is not null)
                _engine?.SetSynth(piano);
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

    private void ApplySoundFont(LatencyEngine engine, int sampleRate)
    {
        if (_bank is null)
            return;

        if (sampleRate is >= 16000 and <= 192000)
            engine.SetSynth(new SoundFontPiano(_bank, sampleRate));
    }

    private void MetronomeToggled(object sender, RoutedEventArgs args)
    {
        if (_engine is not null)
            _engine.MetronomeEnabled = MetronomeSwitch.IsOn;
    }

    private async void TestNoteClicked(object sender, RoutedEventArgs args)
    {
        if (_engine is not { } engine)
            return;

        _testNoteHeld = true;
        UpdateControls();
        const byte note = 60;
        try
        {
            engine.TryEnqueue(new MidiMessage(Stopwatch.GetTimestamp(), 0x9F, note, 100));
            await Task.Delay(160);
            engine.TryEnqueue(new MidiMessage(Stopwatch.GetTimestamp(), 0x8F, note, 0));
        }
        finally
        {
            _testNoteHeld = false;
            if (!_closed)
                UpdateControls();
        }
    }

    private void RecordClicked(object sender, RoutedEventArgs args)
    {
        if (_engine is not { } engine || _finishingTake || _fileBusy)
            return;

        if (_recording)
        {
            engine.SetRecording(false, 0);
            _recording = false;
            _finishingTake = true;
        }
        else
        {
            engine.StopPlayback();
            _rows.Clear();
            _capturedNotes.Clear();
            _firstImportedNote = null;
            _captureTrack = TrackBox.SelectedIndex;
            _noteCount = 0;
            var offset = double.IsFinite(OffsetBox.Value) ? OffsetBox.Value : 10;
            var division = QuantizeBox.SelectedIndex switch { 1 => 4, 2 => 8, 3 => 16, _ => 0 };
            MetronomeSwitch.IsOn = true;
            _takeId = engine.SetRecording(true, offset, division);
            _recording = true;
        }
        UpdateReadout();
        UpdateControls();
    }

    private void ShowTakeClicked(object sender, RoutedEventArgs args)
    {
        WorkspaceTabs.SelectedItem = PianoRollTab;
        if (_firstImportedNote is { } take)
        {
            TrackBox.SelectedIndex = take.Track;
            RollView.RevealTick(take.Note.StartTick, take.Note.NoteNumber);
        }
    }

    private void PlayClicked(object sender, RoutedEventArgs args)
    {
        if (_engine is not { } engine || _bank is null)
            return;

        try
        {
            if (!engine.PlaySong(SnapshotSong(), new SoundFontPiano(_bank, engine.SampleRate, generalMidi: true)))
                return;

            _playbackError = null;
            UpdatePlayhead();
            _playheadTimer.Start();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException)
        {
            _playbackError = error.Message;
            UpdatePlaybackControls();
        }
    }

    private void StopClicked(object sender, RoutedEventArgs args) => StopSongPlayback();

    private void StopSongPlayback()
    {
        _engine?.StopPlayback();
        UpdatePlayhead();
    }

    private void UpdatePlayhead()
    {
        long? tick = _audioFailed ? null : _engine?.PlaybackTick;
        RollView.SetPlayhead(tick);
        if (tick is null)
            _playheadTimer.Stop();

        if (_playing == tick.HasValue)
            return;

        _playing = tick.HasValue;
        UpdatePlaybackControls();
    }

    private void UpdateReadout()
    {
        if (_output is not { } output || _engine is not { } engine)
            return;

        var diagnostics = output.Diagnostics;
        AudioCountersText.Text = $"Render passes {diagnostics.RenderPasses:N0} · Estimated underruns {diagnostics.Underruns:N0} · "
            + $"Clock failures {diagnostics.ClockFailures:N0} · MIDI drops {engine.DroppedMidiMessages:N0} · "
            + $"Capture drops {engine.DroppedRecordedNotes:N0}";
        if (!_audioFailed && diagnostics.Failure is { } failure)
        {
            _audioFailed = true;
            _finishingTake |= _recording;
            _recording = false;
            AudioFormatText.Text = $"Audio stopped. {failure}";
            UpdatePlayhead();
            UpdateControls();
        }

        // Observe completion before draining so the final pass also sees any held notes closed on stop.
        bool takeFinished = _finishingTake && (_audioFailed || engine.CompletedTakeId == _takeId);
        while (engine.TryReadNote(out var note))
        {
            if (note.TakeId != _takeId)
                continue;

            _noteCount++;
            SongNote songNote = note.ToSongNote();
            _capturedNotes.Add(songNote with { LengthTicks = Math.Max(1, songNote.LengthTicks) });
            if (_rows.Count == 64)
                _rows.RemoveAt(_rows.Count - 1);
            _rows.Insert(0, TakeRow.From(note, output.SampleRate));
        }
        if (takeFinished)
        {
            _finishingTake = false;
            AddTakeToPianoRoll(engine.TempoMap.TicksPerQuarter);
            UpdateControls();
        }

        if (_takeId != 0)
        {
            string noteLabel = _noteCount == 1 ? "note" : "notes";
            string status = _audioFailed ? "Audio stopped" : _recording ? "Recording" :
                _finishingTake ? "Finishing take" : "Take complete";
            TakeStatusText.Text = $"{status} · {_noteCount} {noteLabel}";
        }
    }

    private void AddTakeToPianoRoll(int ticksPerQuarter)
    {
        if (_capturedNotes.Count == 0)
            return;

        long captureBar = 4L * ticksPerQuarter;
        long songBar = 4L * _songTempo.TicksPerQuarter;
        var document = _tracks[_captureTrack].Document;
        var first = _capturedNotes.MinBy(note => note.StartTick);
        long sourceBar = first.StartTick / captureBar * captureBar;
        long rollEnd = document.Notes
            .Select(note => note.Note.StartTick + note.Note.LengthTicks)
            .DefaultIfEmpty()
            .Max();
        long targetBar = rollEnd == 0 ? 0 : ((rollEnd - 1) / songBar + 1) * songBar;
        document.Append(_capturedNotes.Select(ShiftNote));
        var imported = ShiftNote(first);
        _firstImportedNote = (_captureTrack, imported);
        _capturedNotes.Clear();
        if (WorkspaceTabs.SelectedIndex == 0 && TrackBox.SelectedIndex == _captureTrack)
            RollView.RevealTick(imported.StartTick, imported.NoteNumber);

        SongNote ShiftNote(SongNote note)
        {
            double scale = _songTempo.TicksPerQuarter / (double)ticksPerQuarter;
            return note with
            {
                StartTick = targetBar + (long)Math.Round((note.StartTick - sourceBar) * scale, MidpointRounding.AwayFromZero),
                LengthTicks = Math.Max(1, (long)Math.Round(note.LengthTicks * scale, MidpointRounding.AwayFromZero))
            };
        }
    }

    private void UpdateControls()
    {
        bool audioReady = _output is not null && !_audioFailed;
        bool pianoReady = audioReady && _bank is not null && _output!.SampleRate is >= 16000 and <= 192000;
        bool takeInProgress = _recording || _finishingTake;
        SoundFontButton.IsEnabled = !_loadingBank;
        TestNoteButton.IsEnabled = pianoReady && !_testNoteHeld;
        MetronomeSwitch.IsEnabled = audioReady;
        RecordButton.IsEnabled = audioReady && !_finishingTake && !_fileBusy;
        RecordButton.Content = _recording ? "Stop recording" : "Record test";
        ShowTakeButton.IsEnabled = _firstImportedNote is not null && !takeInProgress && !_fileBusy;
        AudioDeviceBox.IsEnabled = !takeInProgress;
        PeriodBox.IsEnabled = !takeInProgress;
        MidiDeviceBox.IsEnabled = !takeInProgress;
        RefreshButton.IsEnabled = !takeInProgress;
        OffsetBox.IsEnabled = !takeInProgress;
        QuantizeBox.IsEnabled = !takeInProgress;
        UpdateFileControls();
        UpdatePlaybackControls();
    }

    private void UpdatePlaybackControls()
    {
        bool audioReady = _output is { SampleRate: >= 16000 and <= 192000 } && !_audioFailed;
        bool takeInProgress = _recording || _finishingTake;
        PlayButton.IsEnabled = audioReady && _bank is not null && !_playing && !takeInProgress && !_fileBusy
            && _tracks.Any(track => track.Document.Notes.Count > 0);
        StopButton.IsEnabled = audioReady && _playing;
        PlaybackStatusText.Text = _playing ? "Playing song" :
            takeInProgress ? "Recording a take" :
            _playbackError ?? (!audioReady ? "Check the audio output in Recording test" :
                _loadingBank ? "Loading SoundFont…" :
                _bank is null ? "Load a SoundFont in Recording test" : "");
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _closed = true;
        _uiTimer.Stop();
        _playheadTimer.Stop();
        _midiInput?.Dispose();
        Volatile.Write(ref _engine, null);
        _output?.Dispose();
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
