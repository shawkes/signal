using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Signal.Native.Engine;
using Windows.System;

namespace Signal.Native.Uno.App;

public sealed partial class MainWindow
{
    private const int MidiTapInput = 0;
    private const int SpaceTapInput = 1;
    private int _activeTapInput = -1;
    private TapTimingTest? _tapTest;
    private string _tapInputLabel = "";

    private void CalibrationInputChanged(object sender, SelectionChangedEventArgs args)
    {
        if (CalibrationButton is not null)
            UpdateCalibrationControls();
    }

    private void CalibrationClicked(object sender, RoutedEventArgs args)
    {
        if (_engine is not { } engine || _fileBusy || _playing || _finishingTake)
            return;

        if (_recording)
        {
            if (_capturePurpose == CapturePurpose.Calibration)
                StopTake(engine);
            return;
        }

        int input = CalibrationInputBox.SelectedIndex;
        if (input != SpaceTapInput && (input != MidiTapInput || _midiInput is null))
            return;

        _tapTest = new TapTimingTest();
        _tapInputLabel = input == SpaceTapInput ? "Space key" : "MIDI keyboard";
        Volatile.Write(ref _activeTapInput, input);
        ConfigureLiveMidi(engine, muted: true);
        engine.RequestMidiReset();
        BeginTake(engine.SetRecording(true, 0, playTestMetronome: true), CapturePurpose.Calibration);
        UpdateControls();
        if (input == SpaceTapInput)
            CalibrationButton.Focus(FocusState.Programmatic);
    }

    private void CalibrationKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != VirtualKey.Space || _activeTapInput != SpaceTapInput)
            return;

        args.Handled = true;
        if (!_recording || args.KeyStatus.WasKeyDown || _engine is not { } engine)
            return;

        long timestamp = Stopwatch.GetTimestamp();
        engine.TryEnqueue(new MidiMessage(timestamp, 0x90, 60, 100));
        engine.TryEnqueue(new MidiMessage(timestamp, 0x80, 60, 0));
    }

    private void CalibrationKeyUp(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key == VirtualKey.Space && _activeTapInput == SpaceTapInput)
            args.Handled = true;
    }

    private void FinishCalibration()
    {
        Volatile.Write(ref _activeTapInput, -1);
    }

    private void ApplyCalibrationClicked(object sender, RoutedEventArgs args)
    {
        if (!CaptureInProgress && _tapTest?.OffsetMilliseconds is { } offset)
        {
            ApplyRecordingOffset(RoundRecordingOffset(offset));
        }
    }

    private void UpdateCalibrationControls()
    {
        bool audioReady = _output is not null && !_audioFailed;
        bool inputReady = CalibrationInputBox.SelectedIndex == SpaceTapInput || _midiInput is not null;
        bool canStart = audioReady && inputReady && !CaptureInProgress && !_playing && !_fileBusy && !_loadingBank;
        CalibrationInputBox.IsEnabled = !CaptureInProgress && !_fileBusy;
        CalibrationButton.IsEnabled = canStart || (audioReady && CalibrationInProgress && _recording);
        CalibrationButton.Content = CalibrationInProgress ? "Stop tap test" : "Start tap test";
        bool offsetMatches = _tapTest?.OffsetMilliseconds is { } value && IsSavedOffset(RoundRecordingOffset(value));
        ApplyCalibrationButton.IsEnabled = !CaptureInProgress && !_fileBusy && !offsetMatches &&
            _tapTest?.OffsetMilliseconds is not null;

        if (CalibrationInProgress && _tapTest is { } active)
        {
            CalibrationStatusText.Text = _finishingTake ? "Finishing test…" :
                active.PracticeRemaining > 0 ? $"Practice taps remaining {active.PracticeRemaining}" :
                $"Measured taps {active.MeasuredTaps} of {TapTimingTest.TargetTaps}";
        }
        else if (_tapTest?.OffsetMilliseconds is { } estimate)
        {
            string recommendation = offsetMatches ? "Saved recording offset" : "Suggested recording offset";
            CalibrationStatusText.Text =
                $"The median tap landed {DescribeBeatTiming(estimate)} ({_tapTest.MeasuredTaps} taps, {_tapInputLabel}). " +
                $"{recommendation} {estimate:+0.0;-0.0;0.0} ms.";
        }
        else if (_tapTest is not null)
        {
            CalibrationStatusText.Text = $"At least {TapTimingTest.MinimumTaps} measured taps are needed for an estimate.";
        }
        else
        {
            CalibrationStatusText.Text = inputReady ? "" : "Select a MIDI input above to use your controller.";
        }
    }
}
