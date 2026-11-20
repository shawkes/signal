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

    private MidiFileDocument(MidiFile source, Song song, TrackSource[] tracks)
    {
        _source = source;
        Song = song;
        _tracks = tracks;
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

        var tracks = file.GetTrackChunks().Select(ReadTrack).ToArray();
        if (tracks.Length == 0 || (file.OriginalFormat == MidiFileFormat.SingleTrack && tracks.Length != 1))
            throw new InvalidDataException("The MIDI file does not contain the expected number of tracks.");

        var tempo = new SongTempoMap(division.TicksPerQuarterNote,
            file.GetTempoMap().GetTempoChanges().Select(change =>
                new TempoChange(change.Time, (int)change.Value.MicrosecondsPerQuarterNote)));
        return new MidiFileDocument(file, new Song(tempo, tracks.Select(track => track.Model)), tracks);
    }

    /// <summary>Saves an edited song using the original file's tracks, metadata and SMF format.</summary>
    public void Write(string path, Song song)
    {
        ArgumentNullException.ThrowIfNull(song);
        if (song.Tracks.Count != _tracks.Length || song.TempoMap.TicksPerQuarter != Song.TempoMap.TicksPerQuarter)
            throw new ArgumentException("The edited song must keep the original tracks and tick division.", nameof(song));

        bool changeTempo = !song.TempoMap.Changes.SequenceEqual(Song.TempoMap.Changes);
        var chunks = new List<MidiChunk>();
        int trackIndex = 0;
        foreach (var chunk in _source.Chunks)
        {
            if (chunk is TrackChunk track)
            {
                var edited = song.Tracks[trackIndex];
                var original = _tracks[trackIndex];
                if (!original.Model.PlaybackMessages.SequenceEqual(edited.PlaybackMessages))
                    throw new ArgumentException("The edited song must keep the original channel messages.", nameof(song));

                var tempos = changeTempo && trackIndex == 0 ? song.TempoMap.Changes : [];
                chunks.Add(UpdateTrack(track, original, edited, changeTempo, tempos));
                trackIndex++;
            }
            else
            {
                chunks.Add(chunk.Clone());
            }
        }
        var file = new MidiFile(chunks) { TimeDivision = _source.TimeDivision };
        WriteAtomically(path, file, _source.OriginalFormat);
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
            var track = song.Tracks[index];
            var events = new List<FileEvent>();
            if (!string.IsNullOrEmpty(track.Name))
                events.Add(new FileEvent(0, CreateNameEvent(track.Name)));
            if (index == 0)
                AddTempos(events, song.TempoMap.Changes);
            foreach (var message in track.PlaybackMessages)
                events.Add(new FileEvent(message.Tick, CreateChannelEvent(message)));
            AddNotes(events, track.Notes);
            chunks.Add(BuildTrack(events, track.EndTick));
        }

        var file = new MidiFile(chunks)
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision((short)song.TempoMap.TicksPerQuarter)
        };
        WriteAtomically(path, file, chunks.Count == 1 ? MidiFileFormat.SingleTrack : MidiFileFormat.MultiTrack);
    }

    private static TrackSource ReadTrack(TrackChunk chunk)
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
            if (midiEvent is NoteEvent noteEvent)
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
        return new TrackSource(new SongTrack(name, sourceNotes.Select(note => note.Note), messages, tick), sourceNotes);
    }

    private static TrackChunk UpdateTrack(TrackChunk chunk, TrackSource source, SongTrack edited, bool replaceTempo,
        IReadOnlyList<TempoChange> tempos)
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

        var events = new List<FileEvent>();
        bool replaceName = edited.Name != source.Model.Name;
        var originalName = chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault();
        if (replaceName && originalName is null && !string.IsNullOrEmpty(edited.Name))
            events.Add(new FileEvent(0, CreateNameEvent(edited.Name)));
        AddTempos(events, tempos);

        long tick = 0;
        foreach (var midiEvent in chunk.Events)
        {
            tick = checked(tick + midiEvent.DeltaTime);
            if (midiEvent is EndOfTrackEvent || removed.Contains(midiEvent) || (replaceTempo && midiEvent is SetTempoEvent))
                continue;
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

        AddNotes(events, remaining.SelectMany(entry => Enumerable.Repeat(entry.Key, entry.Value)));
        return BuildTrack(events, Math.Max(source.Model.EndTick, edited.EndTick));
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

    private sealed record TrackSource(SongTrack Model, SourceNote[] Notes);
    private readonly record struct SourceNote(SongNote Note, MidiEvent On, MidiEvent Off);
    private readonly record struct FileEvent(long Tick, MidiEvent Event, bool IsRelease = false);
}
