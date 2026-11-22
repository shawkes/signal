using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.Windows.App.Tempo;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private void UpdateTempoSummary()
    {
        RollView.CanUndoTempoEdit = _trackEditor.CanUndoTempoEdit;
        RollView.SetTempoMap(SongTempo);
        double bpm = 60_000_000.0 / SongTempo.Changes[0].MicrosecondsPerQuarterNote;
        int changes = SongTempo.Changes.Count - 1;
        UpdateMeterButton(_engine?.PlaybackTick);
        SongTempoText.Text = $"{bpm:0.#} BPM" +
            (changes == 0 ? "" : $"  ·  {changes} tempo {(changes == 1 ? "change" : "changes")}");
    }

    private void EditTempoMarker(long tick, int? microseconds)
    {
        if (!_fileBusy && !CaptureInProgress)
            _trackEditor.EditTempoMarker(tick, microseconds);
    }

    private void MoveTempoMarker(long fromTick, long toTick)
    {
        if (!_fileBusy && !CaptureInProgress)
            _trackEditor.MoveTempoMarker(fromTick, toTick);
    }

    private void UndoTempoMarkerEdit()
    {
        if (!_fileBusy && !CaptureInProgress)
            _trackEditor.UndoTempoEdit();
    }

    private async void TempoChangesClicked(object sender, RoutedEventArgs args)
    {
        if (_fileBusy || CaptureInProgress)
            return;

        StopSongPlayback();
        SetFileBusy(true);
        try
        {
            var view = new TempoChangesView(SongTempo);
            var dialog = new ContentDialog
            {
                XamlRoot = WorkspaceRoot.XamlRoot,
                Title = "Tempo changes",
                Content = view,
                PrimaryButtonText = "Apply",
                SecondaryButtonText = "Undo tempo edit",
                IsSecondaryButtonEnabled = _trackEditor.CanUndoTempoEdit,
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.None
            };
            dialog.PrimaryButtonClick += (_, click) => click.Cancel = !view.TryFinishEdit();
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
                _trackEditor.SetTempoChanges(view.Changes);
            else if (result == ContentDialogResult.Secondary)
                _trackEditor.UndoTempoEdit();
        }
        catch (Exception error)
        {
            ShowFileError("Could not edit tempo changes", error);
        }
        finally
        {
            if (!_closed)
                SetFileBusy(false);
        }
    }
}
