using Microsoft.UI.Xaml.Controls;
using Signal.Native.Engine;
using Signal.Native.SoundFont;

namespace Signal.Native.Windows.App;

public sealed partial class MainWindow
{
    private const int MidiChannelCount = 16;
    private LatencyEngine? _liveMidiEngine;
    private SongTrackEditor? _liveMidiEditor;
    private MeltySynth.SoundFont? _liveMidiBank;
    private LiveMidiDestination?[] _liveDestinations = [];
    private SongChannelMessage[] _liveSetup = [];
    private bool _liveMuted;

    private bool MultiMidiRouting => MidiRoutingBox.SelectedIndex == 1;

    private void MidiRoutingChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_engine is { } engine && !CaptureInProgress)
            ConfigureLiveMidi(engine);
    }

    private void ConfigureLiveMidi(LatencyEngine engine, bool muted = false)
    {
        var destinations = CreateLiveDestinations();
        var setup = CreateLiveSetup();
        bool outputChanged = !ReferenceEquals(engine, _liveMidiEngine);
        bool synthChanged = outputChanged || !ReferenceEquals(_bank, _liveMidiBank) ||
            muted != _liveMuted || !_liveSetup.AsSpan().SequenceEqual(setup);
        if (!synthChanged && ReferenceEquals(_trackEditor, _liveMidiEditor) &&
            _liveDestinations.AsSpan().SequenceEqual(destinations))
            return;

        IMonitorSynth? synth = null;
        if (synthChanged)
        {
            synth = !muted && _bank is { } bank && engine.SampleRate is >= 16000 and <= 192000
                ? new SoundFontPiano(bank, engine.SampleRate, generalMidi: true)
                : SilentMonitorSynth.Instance;
            foreach (var message in setup)
                synth.ProcessMidi(message.Status, message.Data1, message.Data2);
        }

        engine.ConfigureLiveMidi(destinations, synth);
        _liveMidiEngine = engine;
        _liveMidiEditor = _trackEditor;
        _liveMidiBank = _bank;
        _liveDestinations = destinations;
        _liveSetup = setup;
        _liveMuted = muted;
    }

    private LiveMidiDestination?[] CreateLiveDestinations()
    {
        var destinations = new LiveMidiDestination?[MidiChannelCount];
        if (!MultiMidiRouting && _selectedTrack is { } selected)
        {
            Array.Fill<LiveMidiDestination?>(destinations, new LiveMidiDestination(selected.PrimaryChannel, FindTrackIndex(selected)));
            return destinations;
        }

        var tracks = _trackEditor.Tracks;
        for (int index = 0; index < tracks.Count; index++)
        {
            var track = tracks[index];
            // Imported format 0 tracks can carry several channels. Ignore metadata-only tracks.
            int channels = track.SourceIndex is null ? 1 << track.PrimaryChannel : 0;
            foreach (var item in track.Document.Notes)
                channels |= 1 << item.Note.Channel;
            foreach (var message in track.PlaybackMessages)
                channels |= 1 << (message.Status & 0x0F);

            for (byte channel = 0; channel < MidiChannelCount; channel++)
            {
                if ((channels & (1 << channel)) == 0)
                    continue;
                if (destinations[channel] is not { } current ||
                    (track.PrimaryChannel == channel && tracks[current.TrackIndex].PrimaryChannel != channel))
                    destinations[channel] = new(channel, index);
            }
        }
        return destinations;
    }

    private SongChannelMessage[] CreateLiveSetup()
    {
        var messages = new SongChannelMessage[MidiChannelCount * 2];
        for (byte channel = 0; channel < MidiChannelCount; channel++)
        {
            var sound = _trackEditor.GetChannelSound(channel);
            int baseBank = channel == SongTrackEditor.DrumChannel ? 128 : 0;
            messages[channel * 2] = new(0, (byte)(0xB0 | channel), 0, (byte)(sound.Bank - baseBank));
            messages[channel * 2 + 1] = new(0, (byte)(0xC0 | channel), sound.Program, 0);
        }
        return messages;
    }
}
