using Microsoft.UI.Xaml.Controls;
using Signal.Native.Engine;
using Signal.Native.SoundFont;

namespace Signal.Native.Uno.App;

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

    private void PreviewEditedNote(SongNote note) => PlayNotePreview(note, audition: false);

    private void AuditionKey(SongNote note) => PlayNotePreview(note, audition: true);

    private void PlayNotePreview(SongNote note, bool audition)
    {
        if (CaptureInProgress || _loadingBank || _bank is not { } bank || _engine is not { } engine)
            return;

        var synth = new SoundFontPiano(bank, engine.SampleRate, generalMidi: true);
        var sound = _trackEditor.GetChannelSound(note.Channel);
        int baseBank = note.Channel == SongTrackEditor.DrumChannel ? 128 : 0;
        synth.ProcessMidi((byte)(0xb0 | note.Channel), 0, (byte)(sound.Bank - baseBank));
        synth.ProcessMidi((byte)(0xc0 | note.Channel), sound.Program, 0);
        var volume = _trackEditor.GetOpeningControlValue(note.Channel, ChannelControl.Volume);
        synth.ProcessMidi((byte)(0xb0 | note.Channel), (byte)ChannelControl.Volume,
            audition && volume == 0 ? SongTrackEditor.DefaultControlValue(ChannelControl.Volume) : volume);
        synth.ProcessMidi((byte)(0xb0 | note.Channel), (byte)ChannelControl.Pan,
            _trackEditor.GetOpeningControlValue(note.Channel, ChannelControl.Pan));
        engine.PreviewNote(note, synth);
    }

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
        var messages = new SongChannelMessage[MidiChannelCount * 4];
        for (byte channel = 0; channel < MidiChannelCount; channel++)
        {
            var sound = _trackEditor.GetChannelSound(channel);
            int baseBank = channel == SongTrackEditor.DrumChannel ? 128 : 0;
            int index = channel * 4;
            messages[index] = new(0, (byte)(0xB0 | channel), 0, (byte)(sound.Bank - baseBank));
            messages[index + 1] = new(0, (byte)(0xC0 | channel), sound.Program, 0);
            messages[index + 2] = new(0, (byte)(0xB0 | channel), (byte)ChannelControl.Volume,
                _trackEditor.GetOpeningControlValue(channel, ChannelControl.Volume));
            messages[index + 3] = new(0, (byte)(0xB0 | channel), (byte)ChannelControl.Pan,
                _trackEditor.GetOpeningControlValue(channel, ChannelControl.Pan));
        }
        return messages;
    }
}
