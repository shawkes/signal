namespace Signal.Native.Engine;

/// <summary>A SoundFont bank and MIDI program. Percussion presets use bank 128.</summary>
public readonly record struct TrackSound(int Bank, byte Program);

/// <summary>A track retains its identity, source index, and note undo history through track edits.</summary>
public sealed class EditableSongTrack
{
    internal EditableSongTrack(SongTrack source, int? sourceIndex, byte? primaryChannel = null)
    {
        Name = source.Name;
        Document = new PianoRollDocument(source.Notes);
        SourceIndex = sourceIndex;
        EndTick = source.EndTick;
        PlaybackMessages = source.PlaybackMessages;
        PrimaryChannel = primaryChannel ?? (source.Notes.Count > 0
            ? source.Notes[0].Channel
            : (byte)(source.PlaybackMessages.FirstOrDefault().Status & 0x0f));
    }

    public string Name { get; internal set; }
    public PianoRollDocument Document { get; }
    public int? SourceIndex { get; }
    public byte PrimaryChannel { get; }
    public bool IsDrum => PrimaryChannel == SongTrackEditor.DrumChannel;
    public long EndTick { get; }
    public IReadOnlyList<SongChannelMessage> PlaybackMessages { get; internal set; }
    internal long SavedNoteRevision { get; set; }
}

/// <summary>Edits the song's track list and starting sounds, with a separate undo history from note edits.</summary>
public sealed class SongTrackEditor
{
    public const byte DrumChannel = 9;
    private const int PercussionBank = 128;
    private const int MidiChannelCount = 16;

    private readonly List<EditableSongTrack> _tracks = [];
    private readonly Stack<(Action Revert, long Revision)> _undo = new();
    private long _revision;
    private long _nextRevision;
    private long _savedRevision;

    public SongTrackEditor(Song song, bool imported = false)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (song.Tracks.Count == 0)
            throw new ArgumentException("A song needs at least one track.", nameof(song));

        TempoMap = song.TempoMap;
        Tracks = _tracks.AsReadOnly();
        for (int index = 0; index < song.Tracks.Count; index++)
            Insert(index, new EditableSongTrack(song.Tracks[index], imported ? index : null));
    }

    public TempoMap TempoMap { get; }
    public IReadOnlyList<EditableSongTrack> Tracks { get; }
    public bool CanUndoTrackEdit => _undo.Count != 0;
    public bool HasInstrumentChannel => FindInstrumentChannel() is not null;
    public bool IsDirty => _revision != _savedRevision
        || _tracks.Any(track => track.Document.Revision != track.SavedNoteRevision);

    public event Action? Changed;
    public event Action? TracksChanged;

    public Song SnapshotSong() => new(TempoMap, _tracks.Select(track =>
        new SongTrack(track.Name, track.Document.CreatePlaybackSnapshot(), track.PlaybackMessages, track.EndTick)));

    public int?[] CreateSourceIndices() => _tracks.Select(track => track.SourceIndex).ToArray();

    public void MarkSaved()
    {
        _savedRevision = _revision;
        foreach (var track in _tracks)
            track.SavedNoteRevision = track.Document.Revision;

        Changed?.Invoke();
    }

    public EditableSongTrack AddInstrumentTrack()
    {
        byte channel = FindInstrumentChannel()
            ?? throw new InvalidOperationException("All 15 instrument MIDI channels are in use.");
        return AddTrack("Instrument", channel, CreateStartingMessages(channel, new TrackSound(0, 0)));
    }

    public EditableSongTrack AddDrumTrack()
    {
        var sound = GetChannelSound(DrumChannel);
        // Another track can already control the percussion bank. Stamp its current kit so the
        // new track still identifies as percussion when saved before any notes are added.
        var messages = IsChannelUsed(DrumChannel)
            ? [new SongChannelMessage(0, 0xc0 | DrumChannel, sound.Program, 0)]
            : CreateStartingMessages(DrumChannel, sound);
        return AddTrack("Drums", DrumChannel, messages);
    }

    public bool RemoveTrack(EditableSongTrack track)
    {
        int index = _tracks.IndexOf(track);
        if (_tracks.Count == 1 || index < 0)
            return false;

        RemoveAt(index);
        Commit(() => Insert(index, track));
        return true;
    }

    public bool RenameTrack(EditableSongTrack track, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (!_tracks.Contains(track) || track.Name == name)
            return false;

        string previous = track.Name;
        track.Name = name;
        Commit(() => track.Name = previous);
        return true;
    }

    public bool SharesChannel(EditableSongTrack track) =>
        _tracks.Any(other => other != track && UsesChannel(other, track.PrimaryChannel));

    public TrackSound GetTrackSound(EditableSongTrack track) => GetChannelSound(track.PrimaryChannel);

    public bool SetTrackSound(EditableSongTrack track, TrackSound sound)
    {
        if (!_tracks.Contains(track))
            return false;

        byte channel = track.PrimaryChannel;
        if (sound.Program > 127 || (track.IsDrum ? sound.Bank != PercussionBank : sound.Bank is < 0 or > 127))
            throw new ArgumentOutOfRangeException(nameof(sound), "Choose a program and bank for the track's MIDI channel.");

        if (GetChannelSound(channel) == sound)
            return false;

        var previous = new List<(EditableSongTrack Track, IReadOnlyList<SongChannelMessage> Messages)>();
        foreach (var current in _tracks)
        {
            var messages = current.PlaybackMessages;
            if (current != track && !messages.Any(message => IsStartingSoundMessage(message, channel)))
                continue;

            previous.Add((current, messages));
            var retained = messages.Where(message => !IsStartingSoundMessage(message, channel));
            if (current == track)
            {
                retained = CreateStartingMessages(channel, sound).Concat(retained);
            }
            else if (current.PrimaryChannel == channel && current.Document.Notes.Count == 0)
            {
                // Keep the empty track's channel discoverable after MIDI save and reopen.
                // This program matches the shared channel and introduces no competing patch.
                retained = retained.Prepend(new SongChannelMessage(0, (byte)(0xc0 | channel), sound.Program, 0));
            }
            current.PlaybackMessages = Array.AsReadOnly(retained.ToArray());
        }

        Commit(() =>
        {
            foreach (var (changed, messages) in previous)
                changed.PlaybackMessages = messages;
        });
        return true;
    }

    public bool UndoTrackEdit()
    {
        if (!_undo.TryPop(out var edit))
            return false;

        edit.Revert();
        _revision = edit.Revision;
        NotifyTrackEdit();
        return true;
    }

    private EditableSongTrack AddTrack(string prefix, byte channel, SongChannelMessage[] messages)
    {
        int suffix = 1;
        string name = prefix;
        while (_tracks.Any(track => string.Equals(track.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{prefix} {++suffix}";

        var track = new EditableSongTrack(new SongTrack(name, [], messages), sourceIndex: null, primaryChannel: channel);
        int index = _tracks.Count;
        Insert(index, track);
        Commit(() => RemoveAt(index));
        return track;
    }

    private TrackSound GetChannelSound(byte channel)
    {
        int baseBank = channel == DrumChannel ? PercussionBank : 0;
        int bank = baseBank;
        byte program = 0;
        foreach (var track in _tracks)
        {
            foreach (var message in track.PlaybackMessages)
            {
                if (!IsStartingSoundMessage(message, channel))
                    continue;

                if ((message.Status & 0xf0) == 0xc0)
                    program = message.Data1;
                else if (message.Data1 == 0)
                    bank = baseBank + message.Data2;
            }
        }
        return new TrackSound(bank, program);
    }

    private byte? FindInstrumentChannel()
    {
        int used = 1 << DrumChannel;
        foreach (var track in _tracks)
        {
            if (track.SourceIndex is null)
                used |= 1 << track.PrimaryChannel;
            foreach (var note in track.Document.Notes)
                used |= 1 << note.Note.Channel;
            foreach (var message in track.PlaybackMessages)
                used |= 1 << (message.Status & 0x0f);
        }

        for (byte channel = 0; channel < MidiChannelCount; channel++)
        {
            if ((used & (1 << channel)) == 0)
                return channel;
        }
        return null;
    }

    private bool IsChannelUsed(byte channel) => _tracks.Any(track => UsesChannel(track, channel));

    private static bool UsesChannel(EditableSongTrack track, byte channel) =>
        (track.SourceIndex is null && track.PrimaryChannel == channel)
        || track.Document.Notes.Any(note => note.Note.Channel == channel)
        || track.PlaybackMessages.Any(message => (message.Status & 0x0f) == channel);

    private static bool IsStartingSoundMessage(SongChannelMessage message, byte channel) =>
        message.Tick == 0 && (message.Status & 0x0f) == channel
        && ((message.Status & 0xf0) == 0xc0 || ((message.Status & 0xf0) == 0xb0 && message.Data1 is 0 or 32));

    private static SongChannelMessage[] CreateStartingMessages(byte channel, TrackSound sound) =>
    [
        new(0, (byte)(0xb0 | channel), 0, (byte)(channel == DrumChannel ? sound.Bank - PercussionBank : sound.Bank)),
        new(0, (byte)(0xc0 | channel), sound.Program, 0)
    ];

    private void Insert(int index, EditableSongTrack track)
    {
        _tracks.Insert(index, track);
        track.Document.Changed += NotesChanged;
    }

    private void RemoveAt(int index)
    {
        var track = _tracks[index];
        track.Document.Changed -= NotesChanged;
        _tracks.RemoveAt(index);
    }

    private void Commit(Action revert)
    {
        _undo.Push((revert, _revision));
        _revision = ++_nextRevision;
        NotifyTrackEdit();
    }

    private void NotifyTrackEdit()
    {
        TracksChanged?.Invoke();
        Changed?.Invoke();
    }

    private void NotesChanged() => Changed?.Invoke();
}
