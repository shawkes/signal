using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Signal.Native.Application.MidiMonitor;
using Signal.Native.Engine;
using Windows.Graphics;

namespace Signal.Native.Uno.App.MidiMonitor;

public sealed partial class MidiMonitorWindow : Window
{
    private const int HistoryLimit = 256;
    private readonly MidiMonitorBuffer _buffer;
    private readonly DispatcherQueueTimer _timer;
    private List<MessageRow> _history = [];
    private long _startedAt = Stopwatch.GetTimestamp();
    private long _received;
    private long _skippedAtClear;

    internal MidiMonitorWindow(MidiMonitorBuffer buffer)
    {
        _buffer = buffer;
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(740, 440));
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => RefreshMessages();
        Closed += (_, _) => _timer.Stop();
        _timer.Start();
    }

    internal void SetInput(string label)
    {
        InputText.Text = label;
        ClearMessages();
    }

    private void PauseClicked(object sender, RoutedEventArgs args)
    {
        bool paused = PauseButton.IsChecked == true;
        _buffer.Paused = paused;
        PauseButton.Content = paused ? "Resume" : "Pause";
        RefreshMessages();
    }

    private void ClearClicked(object sender, RoutedEventArgs args) => ClearMessages();

    private void ActiveSensingClicked(object sender, RoutedEventArgs args)
    {
        _buffer.ShowActiveSensing = ActiveSensingBox.IsChecked == true;
        if (!_buffer.ShowActiveSensing && _history.RemoveAll(row => row.IsActiveSensing) != 0)
            ShowHistory();
        RefreshMessages();
    }

    private void ClearMessages()
    {
        _startedAt = Stopwatch.GetTimestamp();
        _skippedAtClear = _buffer.Skipped;
        _received = 0;
        _history = [];
        ShowHistory();
        RefreshMessages();
    }

    private void RefreshMessages()
    {
        List<MessageRow>? batch = null;
        for (int i = 0; i < MidiMonitorBuffer.Capacity && _buffer.TryRead(out var message); i++)
        {
            bool isActiveSensing = message.Status == MidiMonitorBuffer.ActiveSensingStatus;
            if (message.Timestamp < _startedAt || (isActiveSensing && !_buffer.ShowActiveSensing))
                continue;
            double milliseconds = Stopwatch.GetElapsedTime(_startedAt, message.Timestamp).TotalMilliseconds;
            (batch ??= []).Add(new(Describe(message, milliseconds), isActiveSensing));
        }

        if (batch is not null)
        {
            _received += batch.Count;
            batch.Reverse();
            batch.AddRange(_history);
            if (batch.Count > HistoryLimit)
                batch.RemoveRange(HistoryLimit, batch.Count - HistoryLimit);
            _history = batch;
            ShowHistory();
        }

        long skipped = _buffer.Skipped - _skippedAtClear;
        string status = _received == 0 ? "Waiting for MIDI messages" : $"{_received:N0} received · newest first";
        if (skipped != 0)
            status += $" · {skipped:N0} skipped";
        if (StatusText.Text != status)
            StatusText.Text = status;
    }

    private void ShowHistory() => MessageList.ItemsSource = _history.Select(row => row.Text).ToArray();

    private readonly record struct MessageRow(string Text, bool IsActiveSensing);

    private static string Describe(MidiMessage message, double milliseconds)
    {
        int command = message.Status & 0xF0;
        string channel = command == 0xF0 ? "—" : ((message.Status & 0x0F) + 1).ToString();
        int length = message.Status switch
        {
            >= 0xC0 and < 0xE0 or 0xF1 or 0xF3 => 2,
            < 0xF0 or 0xF2 => 3,
            _ => 1
        };
        string bytes = length switch
        {
            1 => $"{message.Status:X2}",
            2 => $"{message.Status:X2} {message.Data1:X2}",
            _ => $"{message.Status:X2} {message.Data1:X2} {message.Data2:X2}"
        };
        string name = command switch
        {
            0x80 or 0x90 when command == 0x80 || message.Data2 == 0 =>
                $"Note off · key {message.Data1} · velocity {message.Data2}",
            0x90 => $"Note on · key {message.Data1} · velocity {message.Data2}",
            0xA0 => $"Polyphonic pressure · key {message.Data1} · {message.Data2}",
            0xB0 => $"Control change · CC {message.Data1} · value {message.Data2}",
            0xC0 => $"Program change · {message.Data1}",
            0xD0 => $"Channel pressure · {message.Data1}",
            0xE0 => $"Pitch bend · {(message.Data1 | (message.Data2 << 7)) - 8192}",
            _ => message.Status switch
            {
                0xF1 => $"Time code quarter frame · {message.Data1}",
                0xF2 => $"Song position · {message.Data1 | (message.Data2 << 7)}",
                0xF3 => $"Song select · {message.Data1}",
                0xF6 => "Tune request",
                0xF8 => "Timing clock",
                0xFA => "Start",
                0xFB => "Continue",
                0xFC => "Stop",
                MidiMonitorBuffer.ActiveSensingStatus => "Active sensing",
                0xFF => "System reset",
                _ => "System message"
            }
        };
        return $"{milliseconds,12:0.000}   {channel,2}     {bytes,-8}   {name}";
    }
}
