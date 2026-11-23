using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Signal.Native.Uno.App;

public sealed partial class MainWindow
{
    private readonly ContentDialog _audioMidiSettingsDialog;
    private Task? _audioMidiSettingsTask;

    private bool AudioMidiSettingsOpen => _audioMidiSettingsTask is { IsCompleted: false };

    private async void AudioMidiSettingsClicked(object sender, RoutedEventArgs args) =>
        await ShowAudioMidiSettingsAsync();

    private Task ShowAudioMidiSettingsAsync()
    {
        if (_audioMidiSettingsTask is { IsCompleted: false } current)
            return current;

        return _closed || _fileBusy
            ? Task.CompletedTask
            : _audioMidiSettingsTask = OpenAudioMidiSettingsAsync();
    }

    private async Task OpenAudioMidiSettingsAsync()
    {
        try
        {
            _transportSpaceDown = false;
            _audioMidiSettingsDialog.XamlRoot = WorkspaceRoot.XamlRoot;
            await _audioMidiSettingsDialog.ShowAsync();
        }
        catch (Exception error)
        {
            ShowFileError("Could not open Audio & MIDI settings", error);
        }
    }

    private async Task CloseAudioMidiSettingsAsync()
    {
        if (_audioMidiSettingsTask is { IsCompleted: false } current)
        {
            _audioMidiSettingsDialog.Hide();
            await current;
        }
    }

    private void WorkspaceSizeChanged(object sender, SizeChangedEventArgs args)
    {
        AudioMidiSettingsScroll.Width = Math.Clamp(args.NewSize.Width - 120, 0, 800);
        AudioMidiSettingsScroll.MaxHeight = Math.Clamp(args.NewSize.Height - 156, 0, 600);
    }
}
