using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.Uno.App.Controls;

namespace Signal.Native.Uno.App;

public sealed partial class MainWindow
{
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
            content.Children.Add(NumericScrub.Wrap(first, () => new(1, lastMeasure, 1)));
            content.Children.Add(NumericScrub.Wrap(last, () => new(1, lastMeasure, lastMeasure)));
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

            var selected = await _songSession.CropAsync(start, end, _selectedTrack);
            AttachSongEditor(selected);
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
        if (!CanSeek || !_songSession.CanUndoCrop)
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

            AttachSongEditor(_songSession.UndoCrop());
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
}
