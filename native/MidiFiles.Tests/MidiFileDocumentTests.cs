using System.Text;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.MidiFiles.Tests;

public sealed class MidiFileDocumentTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("signal-midi-").FullName;

    [Fact]
    public void Format1EditsPreserveTrackBoundariesAndOriginalTimedEvents()
    {
        string input = InDirectory("original.mid");
        string output = InDirectory("edited.mid");
        var conductor = new TrackChunk(
            new SequenceTrackNameEvent("Conductor"),
            new SetTempoEvent(500_000),
            new TimeSignatureEvent(3, 4),
            new SetTempoEvent(500_000) { DeltaTime = 120 },
            new SetTempoEvent(750_000) { DeltaTime = 840 });
        var bass = new TrackChunk(
            new SequenceTrackNameEvent("Bäss"),
            new ProgramChangeEvent((SevenBitNumber)33) { Channel = (FourBitNumber)4 },
            new ControlChangeEvent((SevenBitNumber)64, (SevenBitNumber)127) { Channel = (FourBitNumber)4 },
            NoteOn(36, 90, 4, 20),
            NoteOff(36, 30, 4, 60),
            new MarkerEvent("Verse") { DeltaTime = 40 },
            NoteOn(40, 80, 4, 360),
            NoteOff(40, 37, 4, 90),
            new PitchBendEvent(9010) { Channel = (FourBitNumber)4, DeltaTime = 30 },
            new NormalSysExEvent([0x7E, 0x7F, 0x09, 0x01, 0xF7]) { DeltaTime = 120 });
        var drums = new TrackChunk(
            new SequenceTrackNameEvent("Kit"),
            NoteOn(42, 60, 9, 960),
            NoteOff(42, 0, 9, 6));
        WriteFixture(input, MidiFileFormat.MultiTrack, new TicksPerQuarterNoteTimeDivision(960),
            conductor, bass, drums, new TrackChunk());

        var withEnd = ReadRaw(input);
        withEnd.GetTrackChunks().ElementAt(1).Events.OfType<EndOfTrackEvent>().Single().DeltaTime = 360;
        withEnd.Write(input, overwriteFile: true, settings: new WritingSettings { TextEncoding = Encoding.UTF8 });

        var loaded = MidiFileDocument.Read(input);
        var song = loaded.Song;
        Assert.Equal(960, song.TempoMap.TicksPerQuarter);
        Assert.Equal(new[] { new TempoChange(0, 500_000), new TempoChange(960, 750_000) }, song.TempoMap.Changes);
        Assert.Equal(1.25, song.TempoMap.TickToSeconds(1920), precision: 8);
        Assert.Equal(new[] { "Conductor", "Bäss", "Kit", "" }, song.Tracks.Select(track => track.Name));
        Assert.Equal(new[]
        {
            new SongChannelMessage(0, 0xC4, 33, 0),
            new SongChannelMessage(0, 0xB4, 64, 127),
            new SongChannelMessage(600, 0xE4, 50, 70)
        }, song.Tracks[1].PlaybackMessages);

        var bassNotes = song.Tracks[1].Notes;
        var moved = bassNotes[0] with { StartTick = 50, NoteNumber = 38, LengthTicks = 100 };
        var editedBass = new SongTrack(song.Tracks[1].Name, [moved, bassNotes[1]],
            song.Tracks[1].PlaybackMessages, song.Tracks[1].EndTick);
        loaded.Write(output, new Song(song.TempoMap, [song.Tracks[0], editedBass, song.Tracks[2], song.Tracks[3]]));

        var result = MidiFileDocument.Read(output).Song;
        Assert.Equal(song.TempoMap.Changes, result.TempoMap.Changes);
        Assert.Equal(song.Tracks.Select(track => track.Name), result.Tracks.Select(track => track.Name));
        Assert.Equal(new[] { moved, bassNotes[1] }, result.Tracks[1].Notes);
        Assert.Equal(song.Tracks[2].Notes, result.Tracks[2].Notes);
        Assert.Empty(result.Tracks[3].Notes);
        Assert.Equal(song.Tracks[1].EndTick, result.Tracks[1].EndTick);

        var raw = ReadRaw(output);
        var rawTracks = raw.GetTrackChunks().ToArray();
        Assert.Equal(MidiFileFormat.MultiTrack, raw.OriginalFormat);
        Assert.Equal(3, rawTracks[0].Events.OfType<SetTempoEvent>().Count());
        Assert.Equal(3, rawTracks[0].Events.OfType<TimeSignatureEvent>().Single().Numerator);
        var bassEvents = rawTracks[1].GetTimedEvents();
        Assert.Equal(120, bassEvents.Single(item => item.Event is MarkerEvent { Text: "Verse" }).Time);
        Assert.Equal(720, bassEvents.Single(item => item.Event is NormalSysExEvent).Time);
        Assert.Contains(rawTracks[1].GetNotes(), note => note.NoteNumber == (SevenBitNumber)40 &&
            note.OffVelocity == (SevenBitNumber)37);
    }

    [Fact]
    public void Format0KeepsZeroLengthNotesAndIgnoresUnpairedEventsForEditing()
    {
        string input = InDirectory("original.mid");
        WriteFixture(input, MidiFileFormat.SingleTrack, new TicksPerQuarterNoteTimeDivision(97),
            new TrackChunk(NoteOn(50, 95, 3, 12), NoteOn(50, 0, 3, 0),
                NoteOn(61, 90, 3, 5), NoteOff(61, 20, 3, 10), NoteOn(72, 90, 3, 5)));

        var loaded = MidiFileDocument.Read(input);
        var track = Assert.Single(loaded.Song.Tracks);
        Assert.Equal(97, loaded.Song.TempoMap.TicksPerQuarter);
        Assert.Equal(SongMeter.CommonTime, loaded.Song.OpeningMeter);
        Assert.Equal(new TempoChange(0, 500_000), Assert.Single(loaded.Song.TempoMap.Changes));
        Assert.Equal(new SongNote(12, 0, 3, 50, 95), track.Notes[0]);
        var edited = new SongTrack(track.Name, [track.Notes[0]], track.PlaybackMessages, track.EndTick);
        loaded.Write(input, new Song(loaded.Song.TempoMap, [edited]));

        var raw = ReadRaw(input);
        Assert.Equal(MidiFileFormat.SingleTrack, raw.OriginalFormat);
        var events = Assert.Single(raw.GetTrackChunks()).GetTimedEvents();
        Assert.Equal(12, events.Single(item => item.Event is NoteOnEvent release &&
            release.NoteNumber == (SevenBitNumber)50 && release.Velocity == SevenBitNumber.MinValue).Time);
        Assert.Equal(32, events.Single(item => item.Event is NoteOnEvent { NoteNumber: var number } &&
            number == (SevenBitNumber)72).Time);
        var reopened = MidiFileDocument.Read(input);
        Assert.Equal(track.Notes[0], Assert.Single(Assert.Single(reopened.Song.Tracks).Notes));
    }

    [Fact]
    public void AddingADrumTrackUpgradesFormat0AndWritesEditedSoundBeforeTheFirstNote()
    {
        string input = InDirectory("original.mid");
        string output = InDirectory("tracks.mid");
        WriteFixture(input, MidiFileFormat.SingleTrack, new TicksPerQuarterNoteTimeDivision(960),
            new TrackChunk(
                new SequenceTrackNameEvent("Piano"),
                new SetTempoEvent(500_000),
                new ControlChangeEvent((SevenBitNumber)0, (SevenBitNumber)2),
                new ProgramChangeEvent((SevenBitNumber)5),
                new ControlChangeEvent((SevenBitNumber)64, (SevenBitNumber)127),
                NoteOn(60, 92, 0, 0),
                NoteOff(60, 37, 0, 120),
                new ProgramChangeEvent((SevenBitNumber)10) { DeltaTime = 120 },
                new SetTempoEvent(740_000) { DeltaTime = 720 }));

        var source = MidiFileDocument.Read(input);
        var original = Assert.Single(source.Song.Tracks);
        var lead = new SongTrack("Mélodie", original.Notes,
            [
                new SongChannelMessage(0, 0xB0, 0, 9),
                new SongChannelMessage(0, 0xC0, 5, 0),
                new SongChannelMessage(0, 0xB0, 64, 127),
                new SongChannelMessage(240, 0xC0, 10, 0)
            ], original.EndTick);
        var drums = new SongTrack("Drums", [new SongNote(0, 80, 9, 38, 110)],
            [new SongChannelMessage(0, 0xB9, 0, 0), new SongChannelMessage(0, 0xC9, 16, 0)]);

        source.Write(output, new Song(source.Song.TempoMap, [lead, drums]), [0, null]);

        var raw = ReadRaw(output);
        Assert.Equal(MidiFileFormat.MultiTrack, raw.OriginalFormat);
        var leadEvents = raw.GetTrackChunks().First().GetTimedEvents();
        Assert.Collection(leadEvents.Where(item => item.Time == 0 && item.Event is ChannelEvent).Select(item => item.Event),
            item => Assert.Equal((SevenBitNumber)9, Assert.IsType<ControlChangeEvent>(item).ControlValue),
            item => Assert.Equal((SevenBitNumber)5, Assert.IsType<ProgramChangeEvent>(item).ProgramNumber),
            item => Assert.Equal((SevenBitNumber)64, Assert.IsType<ControlChangeEvent>(item).ControlNumber),
            item => Assert.IsType<NoteOnEvent>(item));
        Assert.Equal((SevenBitNumber)37, Assert.IsType<NoteOffEvent>(leadEvents.Single(item => item.Time == 120).Event).Velocity);

        var reopened = MidiFileDocument.Read(output).Song;
        Assert.Equal(960, reopened.TempoMap.TicksPerQuarter);
        Assert.Equal(source.Song.TempoMap.Changes, reopened.TempoMap.Changes);
        Assert.Equal(new[] { lead.Name, drums.Name }, reopened.Tracks.Select(track => track.Name));
        Assert.Equal(lead.PlaybackMessages, reopened.Tracks[0].PlaybackMessages);
        Assert.Equal(drums.PlaybackMessages, reopened.Tracks[1].PlaybackMessages);
        Assert.Equal(drums.Notes, reopened.Tracks[1].Notes);
    }

    [Fact]
    public void DeletingATempoBearingTrackRetainsGlobalTimingAndOnlyTheSelectedParts()
    {
        string input = InDirectory("original.mid");
        string output = InDirectory("tracks.mid");
        var removed = new TrackChunk(
            new SequenceTrackNameEvent("Removed conductor and notes"),
            new SetTempoEvent(510_000),
            new TimeSignatureEvent(3, 4),
            NoteOn(40, 90, 1, 10),
            NoteOff(40, 0, 1, 20),
            new MarkerEvent("Removed local marker"),
            new SetTempoEvent(660_000) { DeltaTime = 450 },
            new KeySignatureEvent(2, 0) { DeltaTime = 240 });
        var retained = new TrackChunk(
            new SequenceTrackNameEvent("Retained"),
            new MarkerEvent("Retained local marker") { DeltaTime = 72 },
            new SetTempoEvent(720_000) { DeltaTime = 408 },
            NoteOn(62, 92, 3, 0),
            NoteOff(62, 31, 3, 240));
        WriteFixture(input, MidiFileFormat.MultiTrack, new TicksPerQuarterNoteTimeDivision(480), removed, retained);
        var source = MidiFileDocument.Read(input);

        source.Write(output, new Song(source.Song.TempoMap, [source.Song.Tracks[1]]), [1]);

        var reopened = MidiFileDocument.Read(output).Song;
        Assert.Equal(source.Song.TempoMap.Changes, reopened.TempoMap.Changes);
        var track = Assert.Single(reopened.Tracks);
        Assert.Equal("Retained", track.Name);
        Assert.Equal(source.Song.Tracks[1].Notes, track.Notes);
        var raw = ReadRaw(output);
        Assert.Equal(MidiFileFormat.SingleTrack, raw.OriginalFormat);
        var events = Assert.Single(raw.GetTrackChunks()).GetTimedEvents();
        Assert.Equal(720, events.Single(item => item.Event is KeySignatureEvent).Time);
        Assert.Equal(0, events.Single(item => item.Event is TimeSignatureEvent).Time);
        Assert.Equal(new[] { 510_000L, 660_000L, 720_000L }, events.Select(item => item.Event)
            .OfType<SetTempoEvent>().Select(tempo => tempo.MicrosecondsPerQuarterNote));
        Assert.Equal("Retained local marker", Assert.Single(events.Select(item => item.Event).OfType<MarkerEvent>()).Text);
    }

    [Fact]
    public void WritesNewSongsAndLeavesDestinationAloneOnFailure()
    {
        string output = InDirectory("new.mid");
        var tempo = new Engine.TempoMap(384, [new TempoChange(0, 480_000), new TempoChange(384, 620_000)]);
        var melody = new SongTrack("Mélodie", [new SongNote(42, 130, 1, 62, 90)],
            [new SongChannelMessage(0, 0xC1, 9, 0)], endTick: 960);
        var meter = new SongMeter(3, 4);
        MidiFileDocument.WriteNew(output, new Song(tempo, [melody, new SongTrack("Second track", [])], meter));
        var original = File.ReadAllBytes(output);
        var reopened = MidiFileDocument.Read(output);
        Assert.Equal(meter, reopened.Song.OpeningMeter);
        Assert.Equal(tempo.Changes, reopened.Song.TempoMap.Changes);
        Assert.Equal(2, reopened.Song.Tracks.Count);
        Assert.Equal(melody.Name, reopened.Song.Tracks[0].Name);
        Assert.Equal(melody.Notes, reopened.Song.Tracks[0].Notes);
        Assert.Equal(melody.PlaybackMessages, reopened.Song.Tracks[0].PlaybackMessages);
        Assert.Equal(960, reopened.Song.Tracks[0].EndTick);

        var invalid = new Song(tempo, [new SongTrack("Invalid", [new SongNote(0, -1, 0, 62, 80)])]);
        Assert.Throws<ArgumentOutOfRangeException>(() => MidiFileDocument.WriteNew(output, invalid));
        Assert.Equal(original, File.ReadAllBytes(output));
        Assert.Single(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public void OpeningMeterSurvivesEditorSnapshotsAndOriginalMidiMeterEventsRemainIntact()
    {
        string input = InDirectory("meter.mid");
        string output = InDirectory("edited-meter.mid");
        var conductor = new TrackChunk(
            new TimeSignatureEvent(6, 8, 36, 8),
            new TimeSignatureEvent(3, 4) { DeltaTime = 1440 });
        var melody = new TrackChunk(NoteOn(62, 90, 0, 0), NoteOff(62, 0, 0, 480));
        WriteFixture(input, MidiFileFormat.MultiTrack, new TicksPerQuarterNoteTimeDivision(480), conductor, melody);

        var document = MidiFileDocument.Read(input);
        var editor = new SongTrackEditor(document.Song, imported: true);
        Assert.Equal(new SongMeter(6, 8), editor.OpeningMeter);
        Assert.True(editor.RemoveTrack(editor.Tracks[0]));

        var snapshot = editor.SnapshotSong();
        Assert.Equal(editor.OpeningMeter, snapshot.OpeningMeter);
        document.Write(output, snapshot, editor.CreateSourceIndices());
        Assert.Equal(editor.OpeningMeter, MidiFileDocument.Read(output).Song.OpeningMeter);

        var events = Assert.Single(ReadRaw(output).GetTrackChunks()).GetTimedEvents()
            .Where(item => item.Event is TimeSignatureEvent);
        Assert.Collection(events,
            item =>
            {
                var signature = Assert.IsType<TimeSignatureEvent>(item.Event);
                Assert.Equal(0, item.Time);
                Assert.Equal(6, signature.Numerator);
                Assert.Equal(8, signature.Denominator);
                Assert.Equal(36, signature.ClocksPerClick);
            },
            item =>
            {
                var signature = Assert.IsType<TimeSignatureEvent>(item.Event);
                Assert.Equal(1440, item.Time);
                Assert.Equal(3, signature.Numerator);
                Assert.Equal(4, signature.Denominator);
            });
    }

    [Fact]
    public void RejectsFormat2AndSmpteTiming()
    {
        string format2 = InDirectory("format-2.mid");
        string smpte = InDirectory("smpte.mid");
        WriteFixture(format2, MidiFileFormat.MultiSequence, new TicksPerQuarterNoteTimeDivision(480), new TrackChunk());
        WriteFixture(smpte, MidiFileFormat.SingleTrack, new SmpteTimeDivision(SmpteFormat.Thirty, 80), new TrackChunk());
        Assert.Contains("format 2", Assert.Throws<NotSupportedException>(() => MidiFileDocument.Read(format2)).Message);
        Assert.Contains("SMPTE", Assert.Throws<NotSupportedException>(() => MidiFileDocument.Read(smpte)).Message);
    }

    private string InDirectory(string name) => Path.Combine(_directory, name);

    private static void WriteFixture(string path, MidiFileFormat format, TimeDivision timeDivision, params TrackChunk[] tracks) =>
        new MidiFile(tracks) { TimeDivision = timeDivision }
            .Write(path, format: format, settings: new WritingSettings { TextEncoding = Encoding.UTF8 });

    private static MidiFile ReadRaw(string path) =>
        MidiFile.Read(path, new ReadingSettings
        {
            EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store,
            SilentNoteOnPolicy = SilentNoteOnPolicy.NoteOn,
            TextEncoding = Encoding.UTF8
        });

    private static NoteOnEvent NoteOn(int note, int velocity, int channel, long delta) =>
        new((SevenBitNumber)note, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel, DeltaTime = delta };

    private static NoteOffEvent NoteOff(int note, int velocity, int channel, long delta) =>
        new((SevenBitNumber)note, (SevenBitNumber)velocity) { Channel = (FourBitNumber)channel, DeltaTime = delta };

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_directory))
            File.Delete(file);
        Directory.Delete(_directory);
    }
}
