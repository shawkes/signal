using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Signal.Native.Engine;
using SongTempoMap = Signal.Native.Engine.TempoMap;

namespace Signal.Native.MidiFiles;

/// <summary>A Standard MIDI File and the original events retained while its notes are edited.</summary>
public sealed class MidiFileDocument
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Lazy<EndOfTrackEvent> EndMarker = new(CreateEndMarker);
    private readonly MidiFile _source;
    private readonly TrackSource[] _tracks;
    private readonly ILookup<long, TimeSignatureEvent> _signatures;

    private MidiFileDocument(MidiFile source, Song song, TrackSource[] tracks, ILookup<long, TimeSignatureEvent> signatures)
    {
        _source = source;
        Song = song;
        _tracks = tracks;
        _signatures = signatures;
    }

    public Song Song { get; }

    public static MidiFileDocument Read(string path)
    {
        MidiFile file;
        try
        {
            file = MidiFile.Read(path, new ReadingSettings
            {
                EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store,
                SilentNoteOnPolicy = SilentNoteOnPolicy.NoteOn,
                // Preserve the bytes of text meta events through the library's string representation.
                TextEncoding = Encoding.Latin1
            });
        }
        catch (MidiException error)
        {
            throw new InvalidDataException($"Could not read the MIDI file. {error.Message}", error);
        }

        if (file.OriginalFormat == MidiFileFormat.MultiSequence)
            throw new NotSupportedException("MIDI format 2 contains independent sequences. Open a format 0 or 1 file.");
        if (file.TimeDivision is not TicksPerQuarterNoteTimeDivision { TicksPerQuarterNote: > 0 } division)
            throw new NotSupportedException("This editor requires PPQ timing. Convert SMPTE-timed MIDI to PPQ first.");

        var tempoTicks = new HashSet<long>();
        var rawSignatures = new List<(long Tick, TimeSignatureEvent Event)>();
        var tracks = file.GetTrackChunks().Select(chunk => ReadTrack(chunk, tempoTicks, rawSignatures)).ToArray();
        if (tracks.Length == 0 || (file.OriginalFormat == MidiFileFormat.SingleTrack && tracks.Length != 1))
            throw new InvalidDataException("The MIDI file does not contain the expected number of tracks.");

        var midiTempoMap = file.GetTempoMap();
        // GetTempoChanges omits events that repeat the current BPM. Keep their positions
        // editable and use the library's effective tempo when multiple events share a tick.
        var tempo = new SongTempoMap(division.TicksPerQuarterNote, tempoTicks.Select(tick =>
            new TempoChange(tick, (int)midiTempoMap.GetTempoAtTime((MidiTimeSpan)tick).MicrosecondsPerQuarterNote)));
        var signatures = rawSignatures.ToLookup(item => item.Tick, item => item.Event);
        var meters = new MeterMap(division.TicksPerQuarterNote, signatures.Select(group =>
        {
            var signature = midiTempoMap.GetTimeSignatureAtTime((MidiTimeSpan)group.Key);
            return new MeterChange(group.Key, new SongMeter(signature.Numerator, signature.Denominator));
        }));
        return new MidiFileDocument(file, new Song(tempo, tracks.Select(track => track.Model), meterMap: meters), tracks, signatures);
    }

    /// <summary>Saves an edited song using the original file's track metadata.</summary>
    public void Write(string path, Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (song.Tracks.Count != _tracks.Length)
            throw new ArgumentException("Track additions or removals require an original-track mapping.", nameof(song));

        Write(path, song, Enumerable.Range(0, _tracks.Length).Select(index => (int?)index).ToArray());
    }

    /// <summary>Saves tracks in song order. A null source entry identifies a newly created track.</summary>
    public void Write(string path, Song song, IReadOnlyList<int?> sourceTrackIndices)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(sourceTrackIndices);
        if (song.Tracks.Count == 0 || song.Tracks.Count != sourceTrackIndices.Count)
            throw new ArgumentException("Provide a source entry for each track and keep at least one track.", nameof(sourceTrackIndices));
        if (song.TempoMap.TicksPerQuarter != Song.TempoMap.TicksPerQuarter)
            throw new ArgumentException("The edited song must keep the original tick division.", nameof(song));

        var usedSources = new HashSet<int>();
        int previousSource = -1;
        bool reordered = false;
        foreach (var entry in sourceTrackIndices)
        {
            if (entry is not int index)
                continue;
            if (index < 0 || index >= _tracks.Length || !usedSources.Add(index))
                throw new ArgumentException("Original track indices must be valid and unique.", nameof(sourceTrackIndices));
            reordered |= index < previousSource;
            previousSource = index;
        }

        // Preserve the original ordering of conflicting global events at the same tick, even
        // when the track that carried one of them has been removed or moved.
        bool relocateGlobals = reordered || Enumerable.Range(0, _tracks.Length).Any(index =>
            !usedSources.Contains(index) && _tracks[index].Chunk.Events.Any(IsGlobalEvent));
        bool changeTempo = !song.TempoMap.Changes.SequenceEqual(Song.TempoMap.Changes);
        var globalEvents = new List<FileEvent>();
        var changedMeterTicks = AddMeterEdits(globalEvents, song.MeterMap);
        if (relocateGlobals)
            CopyGlobalEvents(globalEvents, changeTempo, changedMeterTicks);
        if (changeTempo)
            AddTempos(globalEvents, song.TempoMap.Changes);

        var outputTracks = new TrackChunk[song.Tracks.Count];
        for (int index = 0; index < outputTracks.Length; index++)
        {
            var edited = song.Tracks[index];
            List<FileEvent> events = index == 0 ? globalEvents : [];
            outputTracks[index] = sourceTrackIndices[index] is int sourceIndex
                ? UpdateTrack(_tracks[sourceIndex], edited, events, relocateGlobals, changeTempo, changedMeterTicks)
                : CreateTrack(edited, events);
        }

        var chunks = new List<MidiChunk>();
        int nextTrack = 0;
        foreach (var chunk in _source.Chunks)
        {
            if (chunk is not TrackChunk)
                chunks.Add(chunk.Clone());
            else if (nextTrack < outputTracks.Length)
                chunks.Add(outputTracks[nextTrack++]);
        }
        chunks.AddRange(outputTracks.Skip(nextTrack));
        var file = new MidiFile(chunks) { TimeDivision = _source.TimeDivision };
        // DryWetMIDI splits one chunk into multiple tracks when asked to write format 1.
        // Format 0 keeps a song with one surviving track as a single track.
        var format = outputTracks.Length == 1 ? MidiFileFormat.SingleTrack : MidiFileFormat.MultiTrack;
        WriteAtomically(path, file, format);
    }

    public static void WriteNew(string path, Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (song.Tracks.Count == 0)
            throw new ArgumentException("A MIDI file needs at least one track.", nameof(song));
        if (song.TempoMap.TicksPerQuarter > short.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(song), "The tick division exceeds Standard MIDI File limits.");

        var chunks = new List<TrackChunk>(song.Tracks.Count);
        for (int index = 0; index < song.Tracks.Count; index++)
        {
            var events = new List<FileEvent>();
            if (index == 0)
            {
                AddTempos(events, song.TempoMap.Changes);
                foreach (var change in song.MeterMap.Changes)
                {
                    if (change.Tick != 0 || change.Meter != SongMeter.CommonTime)
                        events.Add(new FileEvent(change.Tick, CreateSignature(change.Meter)));
                }
            }
            chunks.Add(CreateTrack(song.Tracks[index], events));
        }

        var file = new MidiFile(chunks)
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision((short)song.TempoMap.TicksPerQuarter)
        };
        WriteAtomically(path, file, chunks.Count == 1 ? MidiFileFormat.SingleTrack : MidiFileFormat.MultiTrack);
    }

    private static TrackChunk CreateTrack(SongTrack track, List<FileEvent> events)
    {
        if (!string.IsNullOrEmpty(track.Name))
            events.Insert(0, new FileEvent(0, CreateNameEvent(track.Name)));
        foreach (var message in track.PlaybackMessages)
            events.Add(new FileEvent(message.Tick, CreateChannelEvent(message)));
        AddNotes(events, track.Notes);
        return BuildTrack(events, track.EndTick);
    }

    private static bool IsGlobalEvent(MidiEvent midiEvent) =>
        midiEvent is SetTempoEvent or TimeSignatureEvent or KeySignatureEvent;

    private HashSet<long> AddMeterEdits(List<FileEvent> events, MeterMap edited)
    {
        if (edited.Changes.SequenceEqual(Song.MeterMap.Changes))
            return [];

        var original = Song.MeterMap.Changes.ToDictionary(change => change.Tick, change => change.Meter);
        var changes = edited.Changes.ToDictionary(change => change.Tick, change => change.Meter);
        var changedTicks = original.Keys.Union(changes.Keys)
            .Where(tick => original.GetValueOrDefault(tick) != changes.GetValueOrDefault(tick)).ToHashSet();
        foreach (long tick in changedTicks)
        {
            if (!changes.TryGetValue(tick, out var meter))
                continue;
            var previousMeter = original.GetValueOrDefault(tick);
            var source = _signatures[tick].LastOrDefault(item =>
                item.Numerator == previousMeter?.Numerator && item.Denominator == previousMeter?.Denominator);
            var signature = CreateSignature(meter);
            if (source is not null)
            {
                signature.ClocksPerClick = source.ClocksPerClick;
                signature.ThirtySecondNotesPerBeat = source.ThirtySecondNotesPerBeat;
            }
            events.Add(new FileEvent(tick, signature));
        }
        return changedTicks;
    }

    private static TimeSignatureEvent CreateSignature(SongMeter meter) =>
        new(checked((byte)meter.Numerator), checked((byte)meter.Denominator));

    private static bool IsEditedGlobal(MidiEvent midiEvent, long tick, bool replaceTempo, HashSet<long> changedMeterTicks) =>
        (replaceTempo && midiEvent is SetTempoEvent) || (midiEvent is TimeSignatureEvent && changedMeterTicks.Contains(tick));

    private void CopyGlobalEvents(List<FileEvent> result, bool replaceTempo, HashSet<long> changedMeterTicks)
    {
        foreach (var track in _tracks)
        {
            long tick = 0;
            foreach (var midiEvent in track.Chunk.Events)
            {
                tick = checked(tick + midiEvent.DeltaTime);
                if (IsGlobalEvent(midiEvent) && !IsEditedGlobal(midiEvent, tick, replaceTempo, changedMeterTicks))
                    result.Add(new FileEvent(tick, midiEvent.Clone()));
            }
        }
    }

    private static TrackSource ReadTrack(TrackChunk chunk, HashSet<long> tempoTicks,
        List<(long Tick, TimeSignatureEvent Event)> signatures)
    {
        var trackName = chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault();
        string name = trackName is null ? "" : DecodeName(trackName.Text);
        var notes = new List<SourceNote>();
        var held = new Dictionary<(byte Channel, byte Pitch), Queue<(long Tick, NoteOnEvent On)>>();
        var messages = new List<SongChannelMessage>();
        long tick = 0;
        foreach (var midiEvent in chunk.Events)
        {
            tick = checked(tick + midiEvent.DeltaTime);
            if (midiEvent is SetTempoEvent)
                tempoTicks.Add(tick);
            else if (midiEvent is TimeSignatureEvent signature)
                signatures.Add((tick, signature));
            else if (midiEvent is NoteEvent noteEvent)
            {
                // Pair edits with the original event instances so all unmatched events can stay in the file.
                var key = (Channel: (byte)noteEvent.Channel, Pitch: (byte)noteEvent.NoteNumber);
                if (noteEvent is NoteOnEvent on && on.Velocity != SevenBitNumber.MinValue)
                {
                    if (!held.TryGetValue(key, out var starts))
                        held.Add(key, starts = new Queue<(long, NoteOnEvent)>());
                    starts.Enqueue((tick, on));
                }
                else if (held.TryGetValue(key, out var starts) && starts.TryDequeue(out var start))
                {
                    var note = new SongNote(start.Tick, tick - start.Tick, key.Channel, key.Pitch, (byte)start.On.Velocity);
                    notes.Add(new SourceNote(note, start.On, noteEvent));
                }
            }
            else if (midiEvent is ChannelEvent channelEvent && GetChannelMessage(tick, channelEvent) is { } message)
                messages.Add(message);
        }

        var sourceNotes = notes.OrderBy(note => note.Note.StartTick).ToArray();
        return new TrackSource(chunk, new SongTrack(name, sourceNotes.Select(note => note.Note), messages, tick), sourceNotes);
    }

    private static TrackChunk UpdateTrack(TrackSource source, SongTrack edited, List<FileEvent> events,
        bool relocateGlobals, bool replaceTempo, HashSet<long> changedMeterTicks)
    {
        var remaining = edited.Notes.CountBy(note => note).ToDictionary();
        var removed = new HashSet<MidiEvent>(ReferenceEqualityComparer.Instance);
        foreach (var note in source.Notes)
        {
            if (remaining.GetValueOrDefault(note.Note) > 0)
                remaining[note.Note]--;
            else
            {
                removed.Add(note.On);
                removed.Add(note.Off);
            }
        }

        var chunk = source.Chunk;
        var channelEdits = GetChannelEdits(source, edited);
        bool replaceName = edited.Name != source.Model.Name;
        var originalName = chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault();
        if (replaceName && originalName is null && !string.IsNullOrEmpty(edited.Name))
            events.Insert(0, new FileEvent(0, CreateNameEvent(edited.Name)));

        long tick = 0;
        foreach (var midiEvent in chunk.Events)
        {
            tick = checked(tick + midiEvent.DeltaTime);
            if (midiEvent is EndOfTrackEvent || removed.Contains(midiEvent) ||
                (relocateGlobals && IsGlobalEvent(midiEvent)) || IsEditedGlobal(midiEvent, tick, replaceTempo, changedMeterTicks))
                continue;
            if (midiEvent is ChannelEvent channelEvent && channelEdits.TryGetValue(tick, out var edit))
            {
                // Emit an edited bank/program sequence before attacks at this tick. Other ticks
                // retain their original event order, including controller and note interleaving.
                edit.Emit(events);
                if (GetChannelMessage(tick, channelEvent) is not null)
                    continue;
            }
            if (replaceName && ReferenceEquals(midiEvent, originalName))
            {
                if (!string.IsNullOrEmpty(edited.Name))
                    events.Add(new FileEvent(tick, CreateNameEvent(edited.Name)));
            }
            else
            {
                events.Add(new FileEvent(tick, midiEvent.Clone()));
            }
        }

        foreach (var edit in channelEdits.Values)
            edit.Emit(events);
        AddNotes(events, remaining.SelectMany(entry => Enumerable.Repeat(entry.Key, entry.Value)));
        return BuildTrack(events, Math.Max(source.Model.EndTick, edited.EndTick));
    }

    private static Dictionary<long, ChannelEdit> GetChannelEdits(TrackSource source, SongTrack edited)
    {
        if (source.Model.PlaybackMessages.SequenceEqual(edited.PlaybackMessages))
            return [];

        var before = source.Model.PlaybackMessages.ToLookup(message => message.Tick);
        var after = edited.PlaybackMessages.ToLookup(message => message.Tick);
        var changedTicks = before.Select(group => group.Key).Union(after.Select(group => group.Key))
            .Where(tick => !before[tick].SequenceEqual(after[tick])).ToHashSet();

        var rawEvents = new Dictionary<SongChannelMessage, Queue<MidiEvent>>();
        long time = 0;
        foreach (var midiEvent in source.Chunk.Events)
        {
            time = checked(time + midiEvent.DeltaTime);
            if (changedTicks.Contains(time) && midiEvent is ChannelEvent channel &&
                GetChannelMessage(time, channel) is { } message)
            {
                if (!rawEvents.TryGetValue(message, out var matching))
                    rawEvents.Add(message, matching = new Queue<MidiEvent>());
                matching.Enqueue(midiEvent);
            }
        }

        return changedTicks.ToDictionary(tick => tick, tick => new ChannelEdit(after[tick]
            .Select(message => new FileEvent(tick,
                rawEvents.TryGetValue(message, out var matching) && matching.TryDequeue(out var original)
                    ? original.Clone()
                    : CreateChannelEvent(message))).ToArray()));
    }

    private static void AddTempos(List<FileEvent> events, IReadOnlyList<TempoChange> tempos)
    {
        foreach (var tempo in tempos)
            events.Add(new FileEvent(tempo.Tick, new SetTempoEvent(tempo.MicrosecondsPerQuarterNote)));
    }

    private static void AddNotes(List<FileEvent> events, IEnumerable<SongNote> notes)
    {
        foreach (var note in notes)
        {
            if (note.StartTick < 0 || note.LengthTicks < 0 || note.Velocity == 0)
                throw new ArgumentOutOfRangeException(nameof(notes), "A MIDI note needs valid timing and nonzero attack velocity.");

            var channel = (FourBitNumber)note.Channel;
            var key = (SevenBitNumber)note.NoteNumber;
            events.Add(new FileEvent(note.StartTick, new NoteOnEvent(key, (SevenBitNumber)note.Velocity) { Channel = channel }));
            events.Add(new FileEvent(checked(note.StartTick + note.LengthTicks),
                new NoteOffEvent(key, SevenBitNumber.MinValue) { Channel = channel }, IsRelease: note.LengthTicks > 0));
        }
    }

    private static TrackChunk BuildTrack(List<FileEvent> events, long endTick)
    {
        var track = new TrackChunk();
        long previousTick = 0;
        // Keep the original ordering at a tick. New positive-length releases precede retriggers.
        foreach (var item in events.OrderBy(item => item.Tick).ThenBy(item => item.IsRelease ? 0 : 1))
        {
            item.Event.DeltaTime = item.Tick - previousTick;
            track.Events.Add(item.Event);
            previousTick = item.Tick;
        }
        if (endTick > previousTick)
        {
            var end = EndMarker.Value.Clone();
            end.DeltaTime = endTick - previousTick;
            track.Events.Add(end);
        }
        return track;
    }

    private static EndOfTrackEvent CreateEndMarker()
    {
        // DryWetMIDI writes this marker automatically and only exposes its constructor internally.
        using var stream = new MemoryStream();
        new MidiFile(new TrackChunk()).Write(stream, MidiFileFormat.SingleTrack);
        stream.Position = 0;
        return MidiFile.Read(stream, new ReadingSettings { EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store })
            .GetTrackChunks().Single().Events.OfType<EndOfTrackEvent>().Single();
    }

    private static SongChannelMessage? GetChannelMessage(long tick, ChannelEvent midiEvent)
    {
        byte channel = (byte)midiEvent.Channel;
        return midiEvent switch
        {
            NoteAftertouchEvent pressure => new SongChannelMessage(tick, (byte)(0xA0 | channel),
                (byte)pressure.NoteNumber, (byte)pressure.AftertouchValue),
            ControlChangeEvent control => new SongChannelMessage(tick, (byte)(0xB0 | channel),
                (byte)control.ControlNumber, (byte)control.ControlValue),
            ProgramChangeEvent program => new SongChannelMessage(tick, (byte)(0xC0 | channel), (byte)program.ProgramNumber, 0),
            ChannelAftertouchEvent pressure => new SongChannelMessage(tick, (byte)(0xD0 | channel),
                (byte)pressure.AftertouchValue, 0),
            PitchBendEvent pitch => new SongChannelMessage(tick, (byte)(0xE0 | channel),
                (byte)(pitch.PitchValue & 0x7F), (byte)(pitch.PitchValue >> 7)),
            _ => null
        };
    }

    private static ChannelEvent CreateChannelEvent(SongChannelMessage message)
    {
        if (message.Tick < 0)
            throw new ArgumentOutOfRangeException(nameof(message), "A MIDI message needs a nonnegative tick.");
        var data1 = (SevenBitNumber)message.Data1;
        var data2 = (SevenBitNumber)message.Data2;
        ChannelEvent midiEvent = (message.Status & 0xF0) switch
        {
            0xA0 => new NoteAftertouchEvent(data1, data2),
            0xB0 => new ControlChangeEvent(data1, data2),
            0xC0 => new ProgramChangeEvent(data1),
            0xD0 => new ChannelAftertouchEvent(data1),
            0xE0 => new PitchBendEvent((ushort)(message.Data1 | (message.Data2 << 7))),
            _ => throw new ArgumentException("The playback message must be a non-note channel event.", nameof(message))
        };
        midiEvent.Channel = (FourBitNumber)(message.Status & 0xF);
        return midiEvent;
    }

    private static string DecodeName(string text)
    {
        try
        {
            return StrictUtf8.GetString(Encoding.Latin1.GetBytes(text));
        }
        catch (DecoderFallbackException)
        {
            return text;
        }
    }

    private static SequenceTrackNameEvent CreateNameEvent(string name) =>
        new(Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(name)));

    private static void WriteAtomically(string path, MidiFile file, MidiFileFormat format)
    {
        var destination = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".signal-{Guid.NewGuid():N}.tmp");
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                file.Write(stream, format, new WritingSettings { TextEncoding = Encoding.Latin1 });
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
            created = false;
        }
        finally
        {
            if (created)
                File.Delete(temporary);
        }
    }

    private sealed class ChannelEdit(FileEvent[] messages)
    {
        private bool _emitted;

        public void Emit(List<FileEvent> events)
        {
            if (_emitted)
                return;
            events.AddRange(messages);
            _emitted = true;
        }
    }

    private sealed record TrackSource(TrackChunk Chunk, SongTrack Model, SourceNote[] Notes);
    private readonly record struct SourceNote(SongNote Note, MidiEvent On, MidiEvent Off);
    private readonly record struct FileEvent(long Tick, MidiEvent Event, bool IsRelease = false);
}
