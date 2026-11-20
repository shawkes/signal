using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Signal.Native.Engine;
using Signal.Native.MidiFiles;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private long _songEndTick;
    private MidiFileDocument? _sourceFile;
    private string? _songPath;
    private bool _fileBusy;
    private bool _allowClose;

    private string SongName => _songPath is null ? "Untitled" : Path.GetFileName(_songPath);
    private bool SongIsDirty => _trackEditor.IsDirty;

    private static Song CreateEmptySong() =>
        new(TempoMap.Constant(120), [new SongTrack("Track 1", [], [new(0, 0xc0, 0, 0)])]);

    private void ReplaceSong(Song song, MidiFileDocument? source = null, string? path = null)
    {
        var editor = new SongTrackEditor(song, imported: source is not null);
        _cueTick = 0;
        StopSongPlayback();
        if (_trackEditor is not null)
        {
            _trackEditor.Changed -= SongEdited;
            _trackEditor.TracksChanged -= TrackPanelChanged;
        }
        _trackEditor = editor;
        editor.Changed += SongEdited;
        editor.TracksChanged += TrackPanelChanged;
        _sourceFile = source;
        _songPath = path;
        _captureTrack = null;
        _firstImportedNote = null;
        _playbackError = null;
        _loopRange = null;
        _loopEnabled = false;

        RefreshTrackPanel(editor.Tracks.FirstOrDefault(track => track.Document.Notes.Count > 0) ?? editor.Tracks[0]);
        SongEdited();
    }

    private void SongEdited()
    {
        long trackEnd = _trackEditor.Tracks.Max(track => Math.Max(track.EndTick,
            track.Document.Notes.Select(item => item.Note)
                .Select(note => note.LengthTicks == 0 && note.StartTick < long.MaxValue
                    ? note.StartTick + 1
                    : note.StartTick + note.LengthTicks)
                .DefaultIfEmpty().Max()));
        _songEndTick = Song.IncludeTimingMarkers(trackEnd, _trackEditor.TempoMap, _trackEditor.MeterMap);
        RollView.SetTimelineEnd(_songEndTick);
        RollView.SetMeterMap(_trackEditor.MeterMap);
        UpdateTempoSummary();
        UpdateSongName();
        UpdateControls();
        UpdateLoopDisplay();
    }

    private void UpdateSongName()
    {
        string name = SongIsDirty ? $"{SongName} *" : SongName;
        SongNameText.Text = name;
        Title = $"Signal · {name}";
    }

    private Song SnapshotSong() => _trackEditor.SnapshotSong();

    private async void NewSongClicked(object sender, RoutedEventArgs args)
    {
        if (_fileBusy || CaptureInProgress)
            return;

        SetFileBusy(true);
        try
        {
            if (await ConfirmReplaceSongAsync())
            {
                ReplaceSong(CreateEmptySong());
                WorkspaceTabs.SelectedItem = PianoRollTab;
            }
        }
        catch (Exception error)
        {
            ShowFileError("Could not create a new song", error);
        }
        finally
        {
            SetFileBusy(false);
        }
    }

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
            {
                await ReadSongAsync(file.Path);
                WorkspaceTabs.SelectedItem = PianoRollTab;
            }
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

    private async void SaveSongClicked(object sender, RoutedEventArgs args) => await SaveFromMenuAsync(saveAs: false);

    private async void SaveSongAsClicked(object sender, RoutedEventArgs args) => await SaveFromMenuAsync(saveAs: true);

    private async Task SaveFromMenuAsync(bool saveAs)
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
            var sourceTrackIndices = _trackEditor.CreateSourceIndices();
            await Task.Run(() =>
            {
                if (source is null)
                    MidiFileDocument.WriteNew(path, song);
                else
                    source.Write(path, song, sourceTrackIndices);
            });
            _songPath = path;
            _trackEditor.MarkSaved();
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
                WorkspaceTabs.SelectedIndex = SongCaptureInProgress ? 0 : 1;
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
        NewSongMenuItem.IsEnabled = ready;
        OpenSongButton.IsEnabled = ready;
        SaveSongButton.IsEnabled = ready;
        SaveSongAsButton.IsEnabled = ready;
        TempoChangesButton.IsEnabled = ready;
        MeterButton.IsEnabled = ready;
        RollView.SetTempoEditingEnabled(ready);
    }

    private void ShowFileError(string title, Exception error)
    {
        FileErrorBar.Title = title;
        FileErrorBar.Message = error.Message;
        FileErrorBar.IsOpen = true;
        WorkspaceTabs.SelectedItem = PianoRollTab;
    }
}
