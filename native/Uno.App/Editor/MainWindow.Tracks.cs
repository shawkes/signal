using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Signal.Native.Engine;

namespace Signal.Native.Uno.App;

public sealed partial class MainWindow
{
    private SongTrackEditor _trackEditor = null!;
    private EditableSongTrack? _selectedTrack;
    private TrackRow[] _trackRows = [];
    private SoundChoice[] _soundChoices = [];
    private bool _changingTrackList;
    private bool _changingSoundList;

    private TempoMap SongTempo => _trackEditor.TempoMap;
    private bool CanChangeTracks => !_fileBusy && !_recording && !_finishingTake;

    private void TrackPanelChanged() => RefreshTrackPanel(_selectedTrack);

    private void RefreshTrackPanel(EditableSongTrack? preferred)
    {
        var tracks = _trackEditor.Tracks;
        int selected = FindTrackIndex(preferred);
        if (selected < 0)
            selected = Math.Clamp(TrackList.SelectedIndex, 0, tracks.Count - 1);

        var rows = tracks.Select((track, index) => new TrackRow(track,
            string.IsNullOrWhiteSpace(track.Name) ? $"Track {index + 1}" : track.Name,
            track.IsDrum ? "Channel 10 · Drums" : $"Channel {track.PrimaryChannel + 1}")).ToArray();
        _changingTrackList = true;
        try
        {
            if (!_trackRows.Select(row => (row.Track, row.Name)).SequenceEqual(rows.Select(row => (row.Track, row.Name))))
            {
                _trackRows = rows;
                TrackList.ItemsSource = rows;
            }
            TrackList.SelectedIndex = selected;
        }
        finally
        {
            _changingTrackList = false;
        }

        AttachTrack(tracks[selected]);
    }

    private int FindTrackIndex(EditableSongTrack? track)
    {
        if (track is not null)
        {
            var tracks = _trackEditor.Tracks;
            for (int index = 0; index < tracks.Count; index++)
                if (ReferenceEquals(tracks[index], track))
                    return index;
        }
        return -1;
    }

    private void SelectTrack(EditableSongTrack? track)
    {
        int index = FindTrackIndex(track);
        if (index >= 0)
        {
            TrackList.SelectedIndex = index;
            TrackList.ScrollIntoView(_trackRows[index]);
        }
    }

    private void TrackChanged(object sender, SelectionChangedEventArgs args)
    {
        int index = TrackList.SelectedIndex;
        if (!_changingTrackList && index >= 0 && index < _trackEditor.Tracks.Count)
            AttachTrack(_trackEditor.Tracks[index]);
    }

    private void AttachTrack(EditableSongTrack track)
    {
        _selectedTrack = track;
        RollView.Attach(track.Document, SongTempo.TicksPerQuarter, track.PrimaryChannel);
        if (_engine is { } engine && !CalibrationInProgress)
            ConfigureLiveMidi(engine);
        if (SongCaptureInProgress)
        {
            ShowTrackRecordingPreview();
            UpdatePlaybackControls();
        }
        RefreshTrackSound();
    }

    private void RefreshTrackSound()
    {
        if (_selectedTrack is not { } track)
            return;

        TrackChannelText.Text = ChannelLabel(track);
        TrackSoundBox.Header = track.IsDrum ? "Drum kit" : "Instrument";
        TrackSoundBox.PlaceholderText = _bank is null ? "Load a SoundFont" :
            track.IsDrum ? "No drum kits in this SoundFont" : "No instruments in this SoundFont";

        var choices = (_bank?.Presets ?? []).Where(preset =>
                preset.PatchNumber is >= 0 and <= 127 &&
                (track.IsDrum ? preset.BankNumber == 128 : preset.BankNumber is >= 0 and <= 127))
            .OrderBy(preset => preset.BankNumber)
            .ThenBy(preset => preset.PatchNumber)
            .Select(preset => new SoundChoice(new TrackSound(preset.BankNumber, (byte)preset.PatchNumber),
                preset.BankNumber is 0 or 128 ? preset.Name : $"{preset.Name}  (Bank {preset.BankNumber})"))
            .DistinctBy(choice => choice.Sound)
            .ToList();

        var current = _trackEditor.GetTrackSound(track);
        int selected = choices.FindIndex(choice => choice.Sound == current);
        if (selected < 0 && _bank is not null && choices.Count > 0)
        {
            choices.Insert(0, new SoundChoice(current, $"Bank {current.Bank} · Program {current.Program + 1} (unavailable)"));
            selected = 0;
        }

        _changingSoundList = true;
        try
        {
            if (!_soundChoices.SequenceEqual(choices))
            {
                _soundChoices = choices.ToArray();
                TrackSoundBox.ItemsSource = _soundChoices.Select(choice => choice.Label).ToArray();
            }
            TrackSoundBox.SelectedIndex = selected;
        }
        finally
        {
            _changingSoundList = false;
        }

        bool shared = _trackEditor.SharesChannel(track);
        TrackChannelHint.Text = shared ? "Tracks sharing this MIDI channel also share its sound." : "";
        TrackChannelHint.Visibility = shared ? Visibility.Visible : Visibility.Collapsed;
        RefreshChannelControls(track);
        UpdateTrackControls();
    }

    private void TrackSoundChanged(object sender, SelectionChangedEventArgs args)
    {
        int index = TrackSoundBox.SelectedIndex;
        if (_changingSoundList || !CanChangeTracks || (uint)index >= (uint)_soundChoices.Length ||
            _selectedTrack is not { } track)
            return;

        _trackEditor.SetTrackSound(track, _soundChoices[index].Sound);
    }

    private void AddInstrumentClicked(object sender, RoutedEventArgs args) =>
        SelectTrack(_trackEditor.AddInstrumentTrack());

    private void AddDrumClicked(object sender, RoutedEventArgs args) =>
        SelectTrack(_trackEditor.AddDrumTrack());

    private void RemoveTrackClicked(object sender, RoutedEventArgs args)
    {
        if (_selectedTrack is { } track)
            _trackEditor.RemoveTrack(track);
    }

    private void UndoTrackClicked(object sender, RoutedEventArgs args) => _trackEditor.UndoTrackEdit();

    private void DuplicateTrackClicked(object sender, RoutedEventArgs args)
    {
        if (CanChangeTracks && sender is MenuFlyoutItem { Tag: EditableSongTrack track })
            SelectTrack(_trackEditor.DuplicateTrack(track));
    }

    private async void RenameTrackClicked(object sender, RoutedEventArgs args)
    {
        if (_selectedTrack is not { } track)
            return;

        SetFileBusy(true);
        try
        {
            var name = new TextBox { Header = "Track name", Text = track.Name };
            var dialog = new ContentDialog
            {
                XamlRoot = WorkspaceRoot.XamlRoot,
                Title = "Rename track",
                Content = name,
                PrimaryButtonText = "Rename",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text)
            };
            name.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(name.Text);
            name.Loaded += (_, _) => name.SelectAll();
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                _trackEditor.RenameTrack(track, name.Text);
        }
        catch (Exception error)
        {
            ShowFileError("Could not rename the track", error);
        }
        finally
        {
            SetFileBusy(false);
        }
    }

    private void UpdateTrackControls()
    {
        bool canChange = CanChangeTracks;
        TrackList.IsEnabled = !_fileBusy;
        MidiRoutingBox.IsEnabled = !CaptureInProgress && !_fileBusy;
        AddInstrumentButton.IsEnabled = canChange && _trackEditor.HasInstrumentChannel;
        AddDrumButton.IsEnabled = canChange;
        UndoTrackButton.IsEnabled = canChange && _trackEditor.CanUndoTrackEdit;
        RenameTrackButton.IsEnabled = canChange && _selectedTrack is not null;
        RemoveTrackButton.IsEnabled = canChange && _selectedTrack is not null && _trackEditor.Tracks.Count > 1;
        TrackSoundBox.IsEnabled = canChange && !_loadingBank && _soundChoices.Length > 0;
        TrackControlsButton.IsEnabled = CanSeek;
        UpdateTrackMixRows();
    }

    private void MuteTrackClicked(object sender, RoutedEventArgs args) => SetTrackMix((ToggleButton)sender, mute: true);

    private void SoloTrackClicked(object sender, RoutedEventArgs args) => SetTrackMix((ToggleButton)sender, mute: false);

    private void UpdateTrackMixRows()
    {
        bool anySolo = _trackRows.Any(row => row.Track.IsSoloed);
        foreach (var row in _trackRows)
            row.UpdateMix(anySolo, CanSeek);
    }

    private void SetTrackMix(ToggleButton button, bool mute)
    {
        if (!CanSeek || button.Tag is not EditableSongTrack track || FindTrackIndex(track) < 0)
            return;

        if (mute)
            track.IsMuted = button.IsChecked == true;
        else
            track.IsSoloed = button.IsChecked == true;
        UpdateTrackMixRows();
        if (_engine is { PlaybackTick: long tick } engine && _bank is not null)
            StartSongPlayback(engine, tick, seek: true, refreshMix: true);
    }

    private static string ChannelLabel(EditableSongTrack track) =>
        track.IsDrum ? "MIDI channel 10 · Drums" : $"MIDI channel {track.PrimaryChannel + 1}";

    private readonly record struct SoundChoice(TrackSound Sound, string Label);
}

public sealed class TrackRow(EditableSongTrack track, string name, string channel) : INotifyPropertyChanged
{
    public EditableSongTrack Track { get; } = track;
    public string Name { get; } = name;
    public string MuteLabel => $"Mute {Name}";
    public string SoloLabel => $"Solo {Name}";
    public string Detail { get; private set; } = "";
    public bool IsMuted { get; private set; }
    public bool IsSoloed { get; private set; }
    public bool CanMix { get; private set; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void UpdateMix(bool anySolo, bool canMix)
    {
        bool muted = Track.IsMuted;
        bool soloed = Track.IsSoloed;
        string state = muted ? "Muted" : anySolo && !soloed ? "Silenced" : soloed ? "Solo" : "Audible";
        string detail = $"{state} · {channel}";
        if (Detail == detail && IsMuted == muted && IsSoloed == soloed && CanMix == canMix)
            return;

        Detail = detail;
        IsMuted = muted;
        IsSoloed = soloed;
        CanMix = canMix;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
