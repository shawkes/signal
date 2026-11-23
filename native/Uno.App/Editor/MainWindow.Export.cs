using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.SoundFont;

namespace Signal.Native.Uno.App;

public sealed partial class MainWindow
{
    private bool CanExportOgg => CanSeek && !_loadingBank && _bank is not null &&
        _trackEditor.Tracks.Any(track => track.Document.Notes.Count > 0);

    private async void ExportOggClicked(object sender, RoutedEventArgs args)
    {
        if (!CanExportOgg || _bank is not { } bank)
            return;

        SetFileBusy(true);
        try
        {
            float[] qualities = [0.2f, 0.4f, 0.5f, 0.65f, 0.8f];
            int[] sampleRates = [44_100, 48_000];
            var rate = new ComboBox { Header = "Sample rate", ItemsSource = new[] { "44.1 kHz", "48 kHz" }, SelectedIndex = 0 };
            var quality = new ComboBox { Header = "Nominal bitrate (variable)" };
            void RefreshBitrates(int selected)
            {
                quality.ItemsSource = qualities.Select(value =>
                    $"{OggSongExporter.NominalBitrate(sampleRates[rate.SelectedIndex], value)} kbps").ToArray();
                quality.SelectedIndex = selected >= 0 ? selected : 2;
            }
            RefreshBitrates(2);
            rate.SelectionChanged += (_, _) => RefreshBitrates(quality.SelectedIndex);
            var options = new StackPanel { Spacing = 12 };
            options.Children.Add(rate);
            options.Children.Add(quality);
            var setup = new ContentDialog
            {
                XamlRoot = WorkspaceRoot.XamlRoot,
                Title = "Export Ogg Vorbis",
                Content = options,
                PrimaryButtonText = "Choose file…",
                CloseButtonText = "Cancel"
            };
            if (await setup.ShowAsync() != ContentDialogResult.Primary)
                return;
            int sampleRate = sampleRates[rate.SelectedIndex];
            float qualityValue = qualities[quality.SelectedIndex];

            var path = await DesktopFileDialogs.SaveAsync(Path.GetFileNameWithoutExtension(SongName),
                "Ogg Vorbis audio", [".ogg"], "SignalAudioExport");
            if (path is null)
                return;

            var song = _trackEditor.SnapshotBackingSong(_songEndTick);
            if (!song.Tracks.Any(track => track.Notes.Count > 0))
                throw new InvalidOperationException("Unmute a track with notes or change the solo selection before exporting.");

            using var cancellation = new CancellationTokenSource();
            var dialog = new ContentDialog
            {
                XamlRoot = WorkspaceRoot.XamlRoot,
                Title = "Exporting Ogg audio",
                Content = new ProgressBar { IsIndeterminate = true },
                CloseButtonText = "Cancel"
            };
            bool finished = false;
            dialog.Closing += (_, _) =>
            {
                if (!finished)
                    cancellation.Cancel();
            };
            var open = dialog.ShowAsync();
            try
            {
                await Task.Run(() => OggSongExporter.Export(path, song, bank, cancellation.Token, sampleRate, qualityValue));
            }
            finally
            {
                finished = true;
                if (!cancellation.IsCancellationRequested)
                    dialog.Hide();
                await open;
            }
        }
        catch (OperationCanceledException)
        {
            // The export discards its temporary file on cancellation.
        }
        catch (Exception error)
        {
            ShowFileError("Could not export Ogg audio", error);
        }
        finally
        {
            SetFileBusy(false);
        }
    }
}
