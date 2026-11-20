using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Signal.Native.Engine;
using Signal.Native.MidiFiles;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private readonly List<EditableTrack> _tracks = [];
    private TempoMap _songTempo = TempoMap.Constant(120);
    private long _songEndTick;
    private MidiFileDocument? _sourceFile;
    private string? _songPath;
    private bool _changingTrackList;
    private bool _fileBusy;
    private bool _allowClose;

    private string SongName => _songPath is null ? "Untitled" : Path.GetFileName(_songPath);
    private bool SongIsDirty => _tracks.Any(track => track.Document.Revision != track.SavedRevision);

    private void ReplaceSong(Song song, MidiFileDocument? source = null, string? path = null)
    {
        if (song.Tracks.Count == 0)
            throw new InvalidDataException("The MIDI file has no tracks.");

        var tracks = song.Tracks.Select(track => new EditableTrack(track)).ToArray();
        StopSongPlayback();
        foreach (var track in _tracks)
            track.Document.Changed -= SongEdited;
        _tracks.Clear();
        _tracks.AddRange(tracks);
        _songTempo = song.TempoMap;
        _sourceFile = source;
        _songPath = path;
        _firstImportedNote = null;
        _playbackError = null;
        _loopRange = null;
        _loopEnabled = false;

        _changingTrackList = true;
        try
        {
            TrackBox.Items.Clear();
            for (int i = 0; i < _tracks.Count; i++)
            {
                string name = _tracks[i].Source.Name;
                string number = $"Track {i + 1}";
                TrackBox.Items.Add(string.IsNullOrWhiteSpace(name) || name == number ? number : $"{number} · {name}");
                _tracks[i].Document.Changed += SongEdited;
            }
            TrackBox.SelectedIndex = Math.Max(0, _tracks.FindIndex(track => track.Document.Notes.Count > 0));
        }
        finally
        {
            _changingTrackList = false;
        }

        AttachSelectedTrack();
        SongEdited();
        double initialBpm = 60_000_000.0 / _songTempo.Changes[0].MicrosecondsPerQuarterNote;
        int changes = _songTempo.Changes.Count - 1;
        SongTempoText.Text = changes == 0 ? $"{initialBpm:0.#} BPM" :
            $"{initialBpm:0.#} BPM initially  ·  {changes} tempo {(changes == 1 ? "change" : "changes")}";
        UpdateControls();
    }

    private void TrackChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_changingTrackList && TrackBox.SelectedIndex >= 0)
            AttachSelectedTrack();
    }

    private void AttachSelectedTrack()
    {
        var track = _tracks[TrackBox.SelectedIndex];
        RollView.Attach(track.Document, _songTempo.TicksPerQuarter, track.DefaultChannel);
    }

    private void SongEdited()
    {
        _songEndTick = _tracks.Max(track => Math.Max(track.Source.EndTick,
            track.Document.Notes.Select(item => item.Note)
                .Select(note => note.LengthTicks == 0 && note.StartTick < long.MaxValue
                    ? note.StartTick + 1
                    : note.StartTick + note.LengthTicks)
                .DefaultIfEmpty().Max()));
        RollView.SetTimelineEnd(_songEndTick);
        UpdateSongName();
        UpdatePlaybackControls();
        UpdateLoopDisplay();
    }

    private void UpdateSongName()
    {
        string name = SongIsDirty ? $"{SongName} *" : SongName;
        SongNameText.Text = name;
        Title = $"Signal · {name}";
    }

    private Song SnapshotSong() => new(_songTempo, _tracks.Select(track =>
        new SongTrack(track.Source.Name, track.Document.CreatePlaybackSnapshot(),
            track.Source.PlaybackMessages, track.Source.EndTick)));

    private async void OpenSongClicked(object sender, RoutedEventArgs args)
    {
        SetFileBusy(true);
        try
        {
            if (!await ConfirmReplaceSongAsync())
                return;

            var picker = new FileOpenPicker(AppWindow.Id) { SettingsIdentifier = "SignalMidiFiles" };
            picker.FileTypeFilter.Add(".mid");
            picker.FileTypeFilter.Add(".midi");
            var file = await picker.PickSingleFileAsync();
            if (file is not null)
                await ReadSongAsync(file.Path);
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

    private async Task ReadSongAsync(string path)
    {
        var document = await Task.Run(() => MidiFileDocument.Read(path));
        ReplaceSong(document.Song, document, path);
    }

    private async void SaveSongClicked(object sender, RoutedEventArgs args) => await SaveFromToolbarAsync(saveAs: false);

    private async void SaveSongAsClicked(object sender, RoutedEventArgs args) => await SaveFromToolbarAsync(saveAs: true);

    private async Task SaveFromToolbarAsync(bool saveAs)
    {
        SetFileBusy(true);
        try
        {
            await SaveSongAsync(saveAs);
        }
        finally
        {
            SetFileBusy(false);
        }
    }

    private async Task<bool> SaveSongAsync(bool saveAs)
    {
        try
        {
            string? path = saveAs ? null : _songPath;
            if (path is null)
            {
                var picker = new FileSavePicker(AppWindow.Id)
                {
                    SuggestedFileName = Path.GetFileNameWithoutExtension(SongName),
                    DefaultFileExtension = ".mid",
                    SettingsIdentifier = "SignalMidiFiles",
                    ShowOverwritePrompt = true
                };
                picker.FileTypeChoices.Add("MIDI file", [".mid", ".midi"]);
                var file = await picker.PickSaveFileAsync();
                if (file is null)
                    return false;

                path = file.Path;
            }

            Song song = SnapshotSong();
            var source = _sourceFile;
            await Task.Run(() =>
            {
                if (source is null)
                    MidiFileDocument.WriteNew(path, song);
                else
                    source.Write(path, song);
            });
            _songPath = path;
            foreach (var track in _tracks)
                track.SavedRevision = track.Document.Revision;
            UpdateSongName();
            return true;
        }
        catch (Exception error)
        {
            ShowFileError("Could not save MIDI", error);
            return false;
        }
    }

    private async Task<bool> ConfirmReplaceSongAsync()
    {
        if (!SongIsDirty)
            return true;

        var dialog = new ContentDialog
        {
            XamlRoot = WorkspaceTabs.XamlRoot,
            Title = "Save changes?",
            Content = $"Save your changes to {SongName} before leaving this song?",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => await SaveSongAsync(saveAs: false),
            ContentDialogResult.Secondary => true,
            _ => false
        };
    }

    private async void WindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose)
            return;

        if (_recording || _finishingTake || _fileBusy)
        {
            args.Cancel = true;
            if (_recording || _finishingTake)
                WorkspaceTabs.SelectedIndex = 1;
            return;
        }
        if (!SongIsDirty)
            return;

        args.Cancel = true;
        WorkspaceTabs.SelectedItem = PianoRollTab;
        SetFileBusy(true);
        try
        {
            if (await ConfirmReplaceSongAsync())
            {
                _allowClose = true;
                Close();
            }
        }
        catch (Exception error)
        {
            ShowFileError("Could not close the song", error);
        }
        finally
        {
            if (!_closed)
                SetFileBusy(false);
        }
    }

    private void SetFileBusy(bool busy)
    {
        _fileBusy = busy;
        if (busy)
            FileErrorBar.IsOpen = false;
        RollView.IsEditingEnabled = !busy;
        UpdateControls();
    }

    private void UpdateFileControls()
    {
        bool ready = !_fileBusy && !_recording && !_finishingTake;
        OpenSongButton.IsEnabled = ready;
        SaveSongButton.IsEnabled = ready;
        SaveSongAsButton.IsEnabled = ready;
        TrackBox.IsEnabled = !_fileBusy;
    }

    private void ShowFileError(string title, Exception error)
    {
        FileErrorBar.Title = title;
        FileErrorBar.Message = error.Message;
        FileErrorBar.IsOpen = true;
    }

    private sealed class EditableTrack(SongTrack source)
    {
        public SongTrack Source { get; } = source;
        public PianoRollDocument Document { get; } = new(source.Notes);
        public long SavedRevision { get; set; }
        public byte DefaultChannel { get; } = source.Notes.Count > 0 ? source.Notes[0].Channel :
            (byte)(source.PlaybackMessages.FirstOrDefault().Status & 0x0F);
    }
}
