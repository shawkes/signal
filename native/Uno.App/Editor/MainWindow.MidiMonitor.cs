using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.Application.MidiMonitor;
using Signal.Native.Uno.App.MidiMonitor;

namespace Signal.Native.Uno.App;

public sealed partial class MainWindow
{
    private MidiMonitorWindow? _midiMonitor;
    private MidiMonitorBuffer? _midiMonitorBuffer;

    private void OpenMidiMonitorClicked(object sender, RoutedEventArgs args)
    {
        if (_midiMonitor is null)
        {
            var buffer = new MidiMonitorBuffer();
            _midiMonitor = new MidiMonitorWindow(buffer);
            ApplyMidiMonitorTheme();
            _midiMonitor.Closed += (_, _) =>
            {
                Volatile.Write(ref _midiMonitorBuffer, null);
                _midiMonitor = null;
            };
            UpdateMidiMonitorInput();
            Volatile.Write(ref _midiMonitorBuffer, buffer);
        }
        _midiMonitor.Activate();
    }

    private void UpdateMidiMonitorInput()
    {
        if (_midiMonitor is not { } monitor)
            return;

        string label = _midiInput is not null && MidiDeviceBox.SelectedItem is ComboBoxItem item
            ? $"Input · {item.Content}"
            : MidiErrorText.Visibility == Visibility.Visible
                ? MidiErrorText.Text
                : "Choose a MIDI input in Settings → Audio & MIDI.";
        monitor.SetInput(label);
    }
}
