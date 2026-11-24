using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.Engine;

namespace Signal.Native.Uno.App;

public sealed partial class MainWindow
{
    private const int DefaultOutputGainDb = 6;
    private readonly ContentDialog _audioMidiSettingsDialog;
    private Task? _audioMidiSettingsTask;
    private int _savedOutputGainDb;
    private bool _savedCompressorEnabled;

    private bool AudioMidiSettingsOpen => _audioMidiSettingsTask is { IsCompleted: false };

    private void InitializeOutputSettings()
    {
        _savedOutputGainDb = _preferences.ReadOutputGain() ?? DefaultOutputGainDb;
        OutputGainSlider.Minimum = LatencyEngine.MinimumOutputGainDb;
        OutputGainSlider.Maximum = LatencyEngine.MaximumOutputGainDb;
        OutputGainSlider.Value = _savedOutputGainDb;
        UpdateOutputGain(_savedOutputGainDb);
        OutputGainSlider.ValueChanged += (_, args) => UpdateOutputGain((int)args.NewValue);

        _savedCompressorEnabled = _preferences.ReadCompressorEnabled();
        CompressorBox.IsChecked = _savedCompressorEnabled;
        CompressorBox.Checked += CompressorChanged;
        CompressorBox.Unchecked += CompressorChanged;
    }

    private void UpdateOutputGain(int gain)
    {
        OutputGainValue.Text = $"{gain:+0;-0;0} dB";
        if (Volatile.Read(ref _engine) is { } engine)
            engine.OutputGainDb = gain;
    }

    private void CompressorChanged(object sender, RoutedEventArgs args)
    {
        if (Volatile.Read(ref _engine) is { } engine)
            engine.CompressorEnabled = CompressorBox.IsChecked == true;
    }

    private void SaveOutputPreferences()
    {
        int gain = (int)OutputGainSlider.Value;
        if (gain != _savedOutputGainDb && TrySavePreference(() => _preferences.SaveOutputGain(gain)))
            _savedOutputGainDb = gain;

        bool enabled = CompressorBox.IsChecked == true;
        if (enabled != _savedCompressorEnabled && TrySavePreference(() => _preferences.SaveCompressorEnabled(enabled)))
            _savedCompressorEnabled = enabled;
    }

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
            SaveOutputPreferences();
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
