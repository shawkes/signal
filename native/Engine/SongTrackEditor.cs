namespace Signal.Native.Engine;

/// <summary>A SoundFont bank and MIDI program. Percussion presets use bank 128.</summary>
public readonly record struct TrackSound(int Bank, byte Program);

public enum ChannelControl : byte { Volume = 7, Pan = 10 }

public readonly record struct ChannelControlPoint(long Tick, byte Value);

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
    public bool IsMuted { get; set; }
    public bool IsSoloed { get; set; }
    public long EndTick { get; }
    public IReadOnlyList<SongChannelMessage> PlaybackMessages { get; internal set; }
    internal long SavedNoteRevision { get; set; }
}

/// <summary>Edits tracks, starting sounds, tempo, and time signatures, with independent undo histories.</summary>
public sealed class SongTrackEditor
{
    public const byte DrumChannel = 9;
    private const int PercussionBank = 128;
    private const int MidiChannelCount = 16;

    private readonly List<EditableSongTrack> _tracks = [];
    private readonly Stack<(Action Revert, long Revision)> _undo = new();
    private readonly Stack<(TempoMap Map, long Revision)> _tempoUndo = new();
    private readonly Stack<MeterMap> _meterUndo = new();
    private MeterMap _savedMeterMap;
    private long _revision;
    private long _nextRevision;
    private long _savedRevision;
    private long _tempoRevision;
    private long _nextTempoRevision;
    private long _savedTempoRevision;
    private bool _unsavedCrop;

    public SongTrackEditor(Song song, bool imported = false, IReadOnlyList<EditableSongTrack>? previousTracks = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (song.Tracks.Count == 0)
            throw new ArgumentException("A song needs at least one track.", nameof(song));

        TempoMap = song.TempoMap;
        MeterMap = _savedMeterMap = song.MeterMap;
        Tracks = _tracks.AsReadOnly();
        for (int index = 0; index < song.Tracks.Count; index++)
        {
            var previous = previousTracks?[index];
            Insert(index, new EditableSongTrack(song.Tracks[index], imported ? index : null, previous?.PrimaryChannel)
            {
                IsMuted = previous?.IsMuted ?? false,
                IsSoloed = previous?.IsSoloed ?? false
            });
        }
    }

    public TempoMap TempoMap { get; private set; }
    public MeterMap MeterMap { get; private set; }
    public SongMeter OpeningMeter => MeterMap.Changes[0].Meter;
    public IReadOnlyList<EditableSongTrack> Tracks { get; }
    public bool CanUndoTrackEdit => _undo.Count != 0;
    public bool CanUndoTempoEdit => _tempoUndo.Count != 0;
    public bool CanUndoMeterEdit => _meterUndo.Count != 0;
    public bool HasInstrumentChannel => FindInstrumentChannel() is not null;
    public bool IsDirty => _unsavedCrop || _revision != _savedRevision
        || _tempoRevision != _savedTempoRevision
        || !ReferenceEquals(MeterMap, _savedMeterMap)
        || _tracks.Any(track => track.Document.Revision != track.SavedNoteRevision);

    public event Action? Changed;
    public event Action? TracksChanged;

    public Song SnapshotSong() => new(TempoMap, _tracks.Select(track =>
        new SongTrack(track.Name, track.Document.CreatePlaybackSnapshot(), track.PlaybackMessages, track.EndTick)), meterMap: MeterMap);

    public int?[] CreateSourceIndices() => _tracks.Select(track => track.SourceIndex).ToArray();

    public void MarkSaved()
    {
        _unsavedCrop = false;
        _savedRevision = _revision;
        _savedTempoRevision = _tempoRevision;
        _savedMeterMap = MeterMap;
        foreach (var track in _tracks)
            track.SavedNoteRevision = track.Document.Revision;

        Changed?.Invoke();
    }

    public void MarkCropUnsaved() => _unsavedCrop = true;

    public Song SnapshotBackingSong(long endTick)
    {
        bool solo = _tracks.Any(track => track.IsSoloed);
        bool IsAudible(EditableSongTrack track) => !track.IsMuted && (!solo || track.IsSoloed);
        var audibleChannels = _tracks.Where(IsAudible).SelectMany(track => track.Document.Notes)
            .Select(item => item.Note.Channel).ToHashSet();
        return new Song(TempoMap, _tracks.Select(track => IsAudible(track)
            ? new SongTrack(track.Name, track.Document.CreatePlaybackSnapshot(), track.PlaybackMessages, endTick)
            : new SongTrack(track.Name, [], track.PlaybackMessages.Where(message =>
                (message.Status & 0xf0) == 0xb0 && message.Data1 is 7 or 10 or 39 or 42 &&
                audibleChannels.Contains((byte)(message.Status & 0x0f))), endTick)), meterMap: MeterMap);
    }

    public static byte DefaultControlValue(ChannelControl control) => control == ChannelControl.Volume ? (byte)100 : (byte)64;

    private static bool MatchesControl(SongChannelMessage message, byte channel, ChannelControl control) =>
        message.Status == (0xb0 | channel) && message.Data1 == (byte)control;

    public ChannelControlPoint[] GetChannelControlPoints(byte channel, ChannelControl control) =>
        _tracks.SelectMany(track => track.PlaybackMessages)
            .Where(message => MatchesControl(message, channel, control))
            .GroupBy(message => message.Tick)
            .OrderBy(group => group.Key)
            .Select(group => new ChannelControlPoint(group.Key, group.Last().Data2)).ToArray();

    public byte GetOpeningControlValue(byte channel, ChannelControl control)
    {
        byte value = DefaultControlValue(control);
        foreach (var track in _tracks)
            foreach (var message in track.PlaybackMessages)
                if (message.Tick == 0 && MatchesControl(message, channel, control))
                    value = message.Data2;
        return value;
    }

    public bool EditChannelControl(EditableSongTrack track, ChannelControl control, long tick, byte? value,
        long? previousTick = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(tick);
        ArgumentOutOfRangeException.ThrowIfNegative(previousTick ?? 0);
        if (value > 127)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (!_tracks.Contains(track))
            return false;

        byte channel = track.PrimaryChannel;
        bool Match(SongChannelMessage message) => MatchesControl(message, channel, control) &&
            (message.Tick == tick || message.Tick == previousTick);
        var current = _tracks.SelectMany(item => item.PlaybackMessages).Where(Match).ToArray();
        if ((!value.HasValue && current.Length == 0) ||
            (value.HasValue && current.Length == 1 && current[0].Tick == tick && current[0].Data2 == value))
            return false;

        var previous = new List<(EditableSongTrack Track, IReadOnlyList<SongChannelMessage> Messages)>();
        foreach (var item in _tracks)
        {
            var messages = item.PlaybackMessages;
            bool add = ReferenceEquals(item, track) && value.HasValue;
            if (!add && !messages.Any(Match))
                continue;
            previous.Add((item, messages));
            var retained = messages.Where(message => !Match(message));
            if (add)
                retained = retained.Append(new(tick, (byte)(0xb0 | channel), (byte)control, value!.Value));
            item.PlaybackMessages = Array.AsReadOnly(retained.ToArray());
        }
        Commit(() =>
        {
            foreach (var (item, messages) in previous)
                item.PlaybackMessages = messages;
        });
        return true;
    }

    /// <summary>Replaces the tempo points as one edit, retaining the song's tick resolution.</summary>
    public bool SetTempoChanges(IEnumerable<TempoChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var map = new TempoMap(TempoMap.TicksPerQuarter, changes);
        if (map.Changes.Any(change => change.MicrosecondsPerQuarterNote > 0xffffff))
            throw new ArgumentOutOfRangeException(nameof(changes), "MIDI tempos must fit in three bytes.");
        if (map.Changes.SequenceEqual(TempoMap.Changes))
            return false;

        _tempoUndo.Push((TempoMap, _tempoRevision));
        TempoMap = map;
        _tempoRevision = ++_nextTempoRevision;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Sets the song tempo at a tick, or removes a later change when the tempo is null.</summary>
    public bool EditTempoMarker(long tick, int? microseconds)
    {
        if (tick < 0 || microseconds is <= 0 or > 0xffffff || (tick == 0 && microseconds is null))
            return false;

        var changes = TempoMap.Changes.Where(change => change.Tick != tick);
        if (microseconds is int tempo)
            changes = changes.Append(new TempoChange(tick, tempo));
        return SetTempoChanges(changes);
    }

    /// <summary>Moves one later tempo change to an unoccupied tick.</summary>
    public bool MoveTempoMarker(long fromTick, long toTick)
    {
        if (fromTick <= 0 || toTick <= 0 || TempoMap.Changes.Any(change => change.Tick == toTick))
            return false;

        return SetTempoChanges(TempoMap.Changes.Select(change =>
            change.Tick == fromTick ? change with { Tick = toTick } : change));
    }

    public bool UndoTempoEdit()
    {
        if (!_tempoUndo.TryPop(out var edit))
            return false;

        TempoMap = edit.Map;
        _tempoRevision = edit.Revision;
        Changed?.Invoke();
        return true;
    }

    public bool SetMeterChanges(IEnumerable<MeterChange> changes)
    {
        var map = new MeterMap(TempoMap.TicksPerQuarter, changes);
        if (map.Changes.Any(change => change.Meter.Denominator > 128))
            throw new ArgumentOutOfRangeException(nameof(changes), "The MIDI file adapter supports beat units through 1/128.");
        if (map.Changes.SequenceEqual(MeterMap.Changes))
            return false;
        _meterUndo.Push(MeterMap);
        MeterMap = map;
        Changed?.Invoke();
        return true;
    }

    public bool UndoMeterEdit()
    {
        if (!_meterUndo.TryPop(out var previous))
            return false;
        MeterMap = previous;
        Changed?.Invoke();
        return true;
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

    public EditableSongTrack? DuplicateTrack(EditableSongTrack track)
    {
        int index = _tracks.IndexOf(track);
        if (index < 0)
            return null;

        string prefix = string.IsNullOrWhiteSpace(track.Name) ? $"Track {index + 1} copy" : $"{track.Name} copy";
        var copy = new EditableSongTrack(new SongTrack(UniqueTrackName(prefix),
            track.Document.CreatePlaybackSnapshot(), track.PlaybackMessages, track.EndTick),
            sourceIndex: null, primaryChannel: track.PrimaryChannel);
        int insertion = index + 1;
        Insert(insertion, copy);
        Commit(() => RemoveAt(insertion));
        return copy;
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
        var track = new EditableSongTrack(new SongTrack(UniqueTrackName(prefix), [], messages), sourceIndex: null, primaryChannel: channel);
        int index = _tracks.Count;
        Insert(index, track);
        Commit(() => RemoveAt(index));
        return track;
    }

    private string UniqueTrackName(string prefix)
    {
        int suffix = 1;
        string name = prefix;
        while (_tracks.Any(track => string.Equals(track.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{prefix} {++suffix}";
        return name;
    }

    public TrackSound GetChannelSound(byte channel)
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
