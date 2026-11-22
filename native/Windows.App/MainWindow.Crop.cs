using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.Engine;
using Signal.Native.MidiFiles;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private readonly Stack<CropUndo> _cropUndo = new();
    private long _songSaveCount;
    private long LastSongMeasure => _trackEditor.MeterMap.PositionAt(Math.Max(0, _songEndTick - 1)).Bar;

    private async void CropSongClicked(object sender, RoutedEventArgs args)
    {
        if (!CanSeek || LastSongMeasure <= 1)
            return;

        SetFileBusy(true);
        try
        {
            long lastMeasure = LastSongMeasure;
            var first = new TextBox { Header = "First measure to keep", Text = "1" };
            var last = new TextBox { Header = "Last measure to keep", Text = lastMeasure.ToString() };
            var content = new StackPanel { Spacing = 8 };
            content.Children.Add(new TextBlock
            {
                Text = $"Enter a range within measures 1–{lastMeasure}. The first kept measure becomes measure 1.",
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(first);
            content.Children.Add(last);
            var dialog = new ContentDialog
            {
                XamlRoot = WorkspaceRoot.XamlRoot,
                Title = "Crop song",
                Content = content,
                PrimaryButtonText = "Crop",
                CloseButtonText = "Cancel",
                IsPrimaryButtonEnabled = false
            };

            long start = 0, end = 0;
            bool ReadRange()
            {
                if (!long.TryParse(first.Text, out long firstBar) || !long.TryParse(last.Text, out long lastBar) ||
                    firstBar < 1 || firstBar > lastBar || lastBar > lastMeasure)
                    return false;

                var meter = _trackEditor.MeterMap;
                start = meter.TickAtBar(firstBar);
                end = lastBar == lastMeasure ? _songEndTick : meter.TickAtBar(lastBar + 1);
                return start < end && (start > 0 || end < _songEndTick);
            }

            first.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = ReadRange();
            last.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = ReadRange();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !ReadRange())
                return;

            var song = SnapshotSong();
            var indices = _trackEditor.CreateSourceIndices();
            var source = _sourceFile;
            var croppedSource = source is null ? null : await Task.Run(() => source.Crop(song, indices, start, end));
            var croppedSong = SongCrop.Keep(song, start, end);
            var editor = new SongTrackEditor(croppedSong, croppedSource is not null, _trackEditor.Tracks);
            editor.MarkCropUnsaved();
            int selected = Math.Max(0, FindTrackIndex(_selectedTrack));
            _cropUndo.Push(new CropUndo(_trackEditor, source, _selectedTrack, _songSaveCount));
            InstallSongEditor(editor, croppedSource, _songPath, editor.Tracks[selected]);
        }
        catch (Exception error)
        {
            ShowFileError("Could not crop the song", error);
        }
        finally
        {
            SetFileBusy(false);
        }
    }

    private async void UndoCropClicked(object sender, RoutedEventArgs args)
    {
        if (!CanSeek || _cropUndo.Count == 0)
            return;

        SetFileBusy(true);
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = WorkspaceRoot.XamlRoot,
                Title = "Undo crop?",
                Content = "Restore the song as it was before the last crop. This also removes any edits made after that crop.",
                PrimaryButtonText = "Undo crop",
                CloseButtonText = "Cancel"
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            var previous = _cropUndo.Pop();
            if (previous.SaveCount != _songSaveCount)
                previous.Editor.MarkCropUnsaved();
            InstallSongEditor(previous.Editor, previous.Source, _songPath, previous.SelectedTrack);
        }
        catch (Exception error)
        {
            ShowFileError("Could not undo the crop", error);
        }
        finally
        {
            SetFileBusy(false);
        }
    }

    private sealed record CropUndo(SongTrackEditor Editor, MidiFileDocument? Source,
        EditableSongTrack? SelectedTrack, long SaveCount);
}
