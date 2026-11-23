using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.Engine;

namespace Signal.Native.Uno.App;

public sealed partial class MainWindow
{
    private long _songEndTick;
    private bool _fileBusy;
    private bool _allowClose;

    private string SongName => Path.GetFileName(_songSession.FilePath) ?? "Untitled";
    private bool SongIsDirty => _trackEditor.IsDirty;

    private void AttachSongEditor(EditableSongTrack? selected = null)
    {
        _cueTick = 0;
        StopSongPlayback();
        if (_trackEditor is not null)
        {
            _trackEditor.Changed -= SongEdited;
            _trackEditor.NotesEdited -= RefreshPlaybackNotes;
            _trackEditor.TracksChanged -= TrackPanelChanged;
        }
        var editor = _songSession.Editor;
        _trackEditor = editor;
        editor.Changed += SongEdited;
        editor.NotesEdited += RefreshPlaybackNotes;
        editor.TracksChanged += TrackPanelChanged;
        _captureTrack = null;
        _firstImportedNote = null;
        _playbackError = null;
        _loopRange = null;
        _loopEnabled = false;

        RefreshTrackPanel(selected ?? editor.Tracks.FirstOrDefault(track => track.Document.Notes.Count > 0) ?? editor.Tracks[0]);
        SongEdited();
    }

    private void SongEdited()
    {
        _songEndTick = _trackEditor.GetTimelineEndTick();
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
        Title = $"{ApplicationTitle} · {name}";
    }

    private async void NewSongClicked(object sender, RoutedEventArgs args)
    {
        if (_fileBusy || CaptureInProgress || AudioMidiSettingsOpen)
            return;

        SetFileBusy(true);
        try
        {
            if (await ConfirmReplaceSongAsync())
            {
                _songSession.NewSong();
                AttachSongEditor();
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
        if (_fileBusy || CaptureInProgress || AudioMidiSettingsOpen)
            return;

        SetFileBusy(true);
        try
        {
            if (!await ConfirmReplaceSongAsync())
                return;

            var path = await DesktopFileDialogs.OpenAsync([".mid", ".midi"], "SignalOpenMidi");
            if (path is not null)
                await ReadSongAsync(path);
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
        await _songSession.OpenAsync(path);
        AttachSongEditor();
    }

    private async void SaveSongClicked(object sender, RoutedEventArgs args) => await SaveFromMenuAsync(saveAs: false);

    private async void SaveSongAsClicked(object sender, RoutedEventArgs args) => await SaveFromMenuAsync(saveAs: true);

    private async Task SaveFromMenuAsync(bool saveAs)
    {
        if (_fileBusy || CaptureInProgress || AudioMidiSettingsOpen)
            return;

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
            string? path = saveAs ? null : _songSession.FilePath;
            if (path is null)
            {
                path = await DesktopFileDialogs.SaveAsync(Path.GetFileNameWithoutExtension(SongName), "MIDI file",
                    [".mid", ".midi"], "SignalSaveMidi");
                if (path is null)
                    return false;
            }

            await _songSession.SaveAsync(path);
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
            XamlRoot = WorkspaceRoot.XamlRoot,
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
            if (CaptureInProgress && !SongCaptureInProgress)
                await ShowAudioMidiSettingsAsync();
            return;
        }
        if (!SongIsDirty)
            return;

        args.Cancel = true;
        SetFileBusy(true);
        try
        {
            await CloseAudioMidiSettingsAsync();
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
        ExportOggMenuItem.IsEnabled = CanExportOgg;
        TempoChangesButton.IsEnabled = ready;
        MeterButton.IsEnabled = ready;
        CropSongMenuItem.IsEnabled = ready && LastSongMeasure > 1;
        UndoCropMenuItem.IsEnabled = ready && _songSession.CanUndoCrop;
        RollView.SetTempoEditingEnabled(ready);
    }

    private void ShowFileError(string title, Exception error)
    {
        FileErrorBar.Title = title;
        FileErrorBar.Message = error.Message;
        FileErrorBar.IsOpen = true;
    }
}
