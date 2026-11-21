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
    public void CropRoundTripKeepsSetupAndInRangeMetadataAndShortensTheFile()
    {
        string input = InDirectory("full.mid");
        string output = InDirectory("cropped.mid");
        var track = new TrackChunk(
            new SequenceTrackNameEvent("Bass"),
            new SetTempoEvent(600_000),
            new TimeSignatureEvent(3, 4),
            new ProgramChangeEvent((SevenBitNumber)33),
            new MarkerEvent("Remove") { DeltaTime = 120 },
            NoteOn(36, 90, 0, 540),
            new MarkerEvent("Keep") { DeltaTime = 300 },
            new SetTempoEvent(450_000) { DeltaTime = 240 },
            NoteOff(36, 0, 0, 720),
            NoteOn(40, 90, 0, 480),
            NoteOff(40, 0, 0, 120));
        WriteFixture(input, MidiFileFormat.SingleTrack, new TicksPerQuarterNoteTimeDivision(240), track);

        var source = MidiFileDocument.Read(input);
        var cropped = source.Crop(source.Song, [0], 720, 1_440);
        cropped.Write(output, cropped.Song);
        var reopened = MidiFileDocument.Read(output).Song;

        var bass = Assert.Single(reopened.Tracks);
        Assert.Equal("Bass", bass.Name);
        Assert.Equal(720, bass.EndTick);
        Assert.Equal(new SongNote(0, 720, 0, 36, 90), Assert.Single(bass.Notes));
        Assert.Equal(new SongChannelMessage(0, 0xc0, 33, 0), Assert.Single(bass.PlaybackMessages));
        Assert.Equal(new SongMeter(3, 4), reopened.OpeningMeter);
        Assert.Equal(new[] { new TempoChange(0, 600_000), new TempoChange(480, 450_000) }, reopened.TempoMap.Changes);
        Assert.Equal("Keep", Assert.Single(ReadRaw(output).GetTrackChunks().Single().Events.OfType<MarkerEvent>()).Text);
    }

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
        Assert.Equal(new[] { new TempoChange(0, 500_000), new TempoChange(120, 500_000), new TempoChange(960, 750_000) },
            song.TempoMap.Changes);
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
        loaded.Write(output, new Song(song.TempoMap, [song.Tracks[0], editedBass, song.Tracks[2], song.Tracks[3]],
            meterMap: song.MeterMap));

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
    public void AddedMarkerRepeatingTheActiveTempoRemainsEditableAfterReopening()
    {
        string input = InDirectory("markers.mid");
        string output = InDirectory("edited-markers.mid");
        WriteFixture(input, MidiFileFormat.MultiTrack, new TicksPerQuarterNoteTimeDivision(480),
            new TrackChunk(new SetTempoEvent(620_000) { DeltaTime = 480 }),
            new TrackChunk(new SetTempoEvent(710_000) { DeltaTime = 480 }));
        var effective = (int)ReadRaw(input).GetTempoMap().GetTempoAtTime((MidiTimeSpan)480).MicrosecondsPerQuarterNote;
        var source = MidiFileDocument.Read(input);
        Assert.Equal(new[] { new TempoChange(0, 500_000), new TempoChange(480, effective) }, source.Song.TempoMap.Changes);
        var editor = new SongTrackEditor(source.Song, imported: true);
        Assert.True(editor.EditTempoMarker(960, effective));

        source.Write(output, editor.SnapshotSong(), editor.CreateSourceIndices());

        var reopened = MidiFileDocument.Read(output);
        Assert.Equal(editor.TempoMap.Changes, reopened.Song.TempoMap.Changes);
        var reopenedEditor = new SongTrackEditor(reopened.Song, imported: true);
        Assert.True(reopenedEditor.MoveTempoMarker(960, 1440));
        Assert.Equal(new TempoChange(1440, effective), reopenedEditor.TempoMap.Changes[^1]);
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

        source.Write(output, new Song(source.Song.TempoMap, [source.Song.Tracks[1]], meterMap: source.Song.MeterMap), [1]);

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
        var meters = new MeterMap(384, [new MeterChange(0, new SongMeter(3, 4)), new MeterChange(768, new SongMeter(7, 8))]);
        MidiFileDocument.WriteNew(output, new Song(tempo, [melody, new SongTrack("Second track", [])], meterMap: meters));
        var original = File.ReadAllBytes(output);
        var reopened = MidiFileDocument.Read(output);
        Assert.Equal(meters.Changes, reopened.Song.MeterMap.Changes);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditedTempoMapReplacesOriginalEventsAndPreservesOtherTiming(bool removeConductor)
    {
        string input = InDirectory("original-tempos.mid");
        string output = InDirectory("edited-tempos.mid");
        var conductor = new TrackChunk(
            new SequenceTrackNameEvent("Conductor"),
            new SetTempoEvent(500_000),
            new TimeSignatureEvent(3, 4),
            new SetTempoEvent(600_000) { DeltaTime = 480 },
            new SetTempoEvent(700_000) { DeltaTime = 480 },
            new KeySignatureEvent(2, 0));
        var melody = new TrackChunk(
            new SequenceTrackNameEvent("Piano"),
            new MarkerEvent("Phrase") { DeltaTime = 72 },
            NoteOn(64, 98, 2, 48),
            NoteOff(64, 31, 2, 240),
            new SetTempoEvent(800_000) { DeltaTime = 1080 });
        WriteFixture(input, MidiFileFormat.MultiTrack, new TicksPerQuarterNoteTimeDivision(480), conductor, melody);
        var source = MidiFileDocument.Read(input);
        var editedTempo = new Engine.TempoMap(480,
            [new TempoChange(0, 480_000), new TempoChange(600, 650_000), new TempoChange(1200, 550_000)]);
        var tracks = removeConductor ? new[] { source.Song.Tracks[1] } : source.Song.Tracks.ToArray();
        int?[] mapping = removeConductor ? [1] : [0, 1];

        source.Write(output, new Song(editedTempo, tracks, source.Song.OpeningMeter), mapping);

        var reopened = MidiFileDocument.Read(output).Song;
        Assert.Equal(editedTempo.Changes, reopened.TempoMap.Changes);
        Assert.Equal(new SongMeter(3, 4), reopened.OpeningMeter);
        Assert.Equal(tracks.Select(track => track.Name), reopened.Tracks.Select(track => track.Name));
        Assert.Equal(source.Song.Tracks[1].Notes, reopened.Tracks[^1].Notes);
        var rawTracks = ReadRaw(output).GetTrackChunks().ToArray();
        var tempos = rawTracks.SelectMany(track => track.GetTimedEvents())
            .Where(item => item.Event is SetTempoEvent)
            .Select(item => new TempoChange(item.Time, (int)((SetTempoEvent)item.Event).MicrosecondsPerQuarterNote));
        Assert.Equal(editedTempo.Changes, tempos);
        Assert.Equal(3, rawTracks[0].Events.OfType<SetTempoEvent>().Count());
        Assert.All(rawTracks.Skip(1), track => Assert.Empty(track.Events.OfType<SetTempoEvent>()));
        var allEvents = rawTracks.SelectMany(track => track.GetTimedEvents()).ToArray();
        Assert.Equal(0, allEvents.Single(item => item.Event is TimeSignatureEvent).Time);
        Assert.Equal(960, allEvents.Single(item => item.Event is KeySignatureEvent).Time);
        Assert.Equal(72, allEvents.Single(item => item.Event is MarkerEvent { Text: "Phrase" }).Time);
        var note = Assert.Single(rawTracks[^1].GetNotes());
        Assert.Equal(120, note.Time);
        Assert.Equal(240, note.Length);
        Assert.Equal((SevenBitNumber)31, note.OffVelocity);

        // Saving again from the same source also removes every later original tempo point.
        var constant = Engine.TempoMap.Constant(100);
        source.Write(output, new Song(constant, tracks, source.Song.OpeningMeter), mapping);
        var flattened = MidiFileDocument.Read(output).Song;
        Assert.Equal(constant.Changes, flattened.TempoMap.Changes);
        Assert.Single(ReadRaw(output).GetTrackChunks().SelectMany(track => track.Events).OfType<SetTempoEvent>());
        Assert.Equal(source.Song.Tracks[1].Notes, flattened.Tracks[^1].Notes);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EditsSignaturesAcrossTracksAndRetainsUneditedMetadata(bool removeConductor)
    {
        string input = InDirectory("signatures.mid");
        string output = InDirectory("edited-signatures.mid");
        var conductor = new TrackChunk(
            new TimeSignatureEvent(3, 4, 11, 7),
            new SetTempoEvent(550_000) { DeltaTime = 120 },
            new TimeSignatureEvent(3, 4, 22, 6) { DeltaTime = 600 },
            new TimeSignatureEvent(5, 4, 30, 5) { DeltaTime = 720 },
            new KeySignatureEvent(2, 0),
            new TimeSignatureEvent(4, 4) { DeltaTime = 480 });
        var melody = new TrackChunk(NoteOn(60, 90, 0, 0), NoteOff(60, 60, 0, 480),
            new TimeSignatureEvent(7, 8, 40, 4) { DeltaTime = 960 });
        WriteFixture(input, MidiFileFormat.MultiTrack, new TicksPerQuarterNoteTimeDivision(480), conductor, melody);
        var source = MidiFileDocument.Read(input);
        var sourceMap = source.Song.MeterMap.Changes;
        Assert.Equal(new long[] { 0, 720, 1440, 1920 }, sourceMap.Select(change => change.Tick));
        Assert.Equal(sourceMap[0].Meter, sourceMap[1].Meter);
        var originalAtConflict = ReadRaw(input).GetTrackChunks().SelectMany(track => track.GetTimedEvents())
            .Where(item => item.Time == 1440).Select(item => item.Event).OfType<TimeSignatureEvent>()
            .Single(signature => signature.Numerator == sourceMap[2].Meter.Numerator);
        var edited = new MeterMap(480, [
            new MeterChange(0, new SongMeter(6, 8)), sourceMap[1], new MeterChange(1440, new SongMeter(2, 4)),
            new MeterChange(2400, new SongMeter(4, 4))
        ]);
        var tracks = removeConductor ? new[] { source.Song.Tracks[1] } : source.Song.Tracks.ToArray();
        source.Write(output, new Song(source.Song.TempoMap, tracks, meterMap: edited), removeConductor ? [1] : [0, 1]);

        var reopened = MidiFileDocument.Read(output).Song;
        Assert.Equal(edited.Changes, reopened.MeterMap.Changes);
        Assert.Equal(source.Song.TempoMap.Changes, reopened.TempoMap.Changes);
        Assert.Equal(source.Song.Tracks[1].Notes, reopened.Tracks[^1].Notes);
        var rawEvents = ReadRaw(output).GetTrackChunks().SelectMany(track => track.GetTimedEvents()).ToArray();
        Assert.Equal(1440, rawEvents.Single(item => item.Event is KeySignatureEvent).Time);
        var signatures = rawEvents.Where(item => item.Event is TimeSignatureEvent)
            .ToDictionary(item => item.Time, item => (TimeSignatureEvent)item.Event);
        Assert.Equal(new long[] { 0, 720, 1440, 2400 }, signatures.Keys.Order());
        Assert.Equal((11, 7), ((int)signatures[0].ClocksPerClick, (int)signatures[0].ThirtySecondNotesPerBeat));
        Assert.Equal((22, 6), ((int)signatures[720].ClocksPerClick, (int)signatures[720].ThirtySecondNotesPerBeat));
        Assert.Equal(originalAtConflict.ClocksPerClick, signatures[1440].ClocksPerClick);
        Assert.Equal(originalAtConflict.ThirtySecondNotesPerBeat, signatures[1440].ThirtySecondNotesPerBeat);
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FinalTimingMarkerPlaysAfterTheNotesBeforeAndAfterSaving(bool meterChange)
    {
        const long markerTick = 960;
        const int rate = 48_000;
        var tempo = new Engine.TempoMap(480, meterChange ? [] : [new TempoChange(markerTick, 750_000)]);
        var meters = new MeterMap(480, meterChange ? [new MeterChange(markerTick, new SongMeter(3, 4))] : []);
        var song = new Song(tempo, [new SongTrack("Piano", [new SongNote(0, 120, 0, 60, 90)])], meterMap: meters);
        string path = InDirectory("late-marker.mid");
        MidiFileDocument.WriteNew(path, song);
        foreach (var version in new[] { song, MidiFileDocument.Read(path).Song })
        {
            var engine = new LatencyEngine(rate, SilentMonitorSynth.Instance, timestampFrequency: rate) { MetronomeEnabled = true };
            Assert.True(engine.PlaySong(version, SilentMonitorSynth.Instance));
            int firstFrames = checked((int)tempo.TickToSample(markerTick, rate));
            engine.Render(new float[firstFrames * 2], 0, new AudioClockAnchor(0, 0));
            Assert.Equal(markerTick, engine.PlaybackTick);

            int lastFrames = checked((int)tempo.TickToSample(markerTick + 1, rate)) - firstFrames;
            var tail = new float[lastFrames * 2];
            engine.Render(tail, firstFrames, new AudioClockAnchor(firstFrames, firstFrames));
            Assert.Equal(meterChange ? 0.25f : 0.16f, tail[0]);
            Assert.Null(engine.PlaybackTick);
        }
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
