using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class TempoEditingTests
{
    [Fact]
    public void TempoEditUpdatesTimingAndRetainsTracksAndNotes()
    {
        var editor = CreateEditor();
        var track = editor.Tracks[0];
        var notes = track.Document.Notes.ToArray();
        int changed = 0;
        int tracksChanged = 0;
        editor.Changed += () => changed++;
        editor.TracksChanged += () => tracksChanged++;

        Assert.True(editor.SetTempoChanges([new(0, 600_000), new(960, 1_000_000)]));

        Assert.Equal(960, editor.TempoMap.TicksPerQuarter);
        Assert.Equal(76_800, editor.TempoMap.TickToSample(1920, 48_000));
        Assert.Equal(1920, editor.TempoMap.SampleToTick(76_800, 48_000), precision: 8);
        Assert.Same(editor.TempoMap, editor.SnapshotSong().TempoMap);
        Assert.Same(track, editor.Tracks[0]);
        Assert.Equal(notes, track.Document.Notes);
        Assert.Equal(1, changed);
        Assert.Equal(0, tracksChanged);
        Assert.True(editor.IsDirty);
        Assert.True(editor.CanUndoTempoEdit);
        Assert.False(editor.CanUndoTrackEdit);
    }

    [Fact]
    public void TempoUndoAndSavedStateAreIndependentOfTrackAndNoteUndo()
    {
        var editor = CreateEditor();
        var track = editor.Tracks[0];
        Assert.True(editor.SetTempoChanges([new(0, 600_000)]));
        editor.MarkSaved();
        Assert.False(editor.IsDirty);

        Assert.True(editor.RenameTrack(track, "Lead"));
        track.Document.Add(new SongNote(960, 120, 0, 64, 90));
        Assert.True(editor.SetTempoChanges([new(0, 700_000)]));
        Assert.True(editor.UndoTempoEdit());
        Assert.Equal(600_000, editor.TempoMap.Changes[0].MicrosecondsPerQuarterNote);
        Assert.Equal("Lead", track.Name);
        Assert.Equal(2, track.Document.Notes.Count);
        Assert.True(editor.IsDirty);

        Assert.True(editor.UndoTrackEdit());
        Assert.True(track.Document.Undo());
        Assert.False(editor.IsDirty);
        Assert.True(editor.UndoTempoEdit());
        Assert.True(editor.IsDirty);
        Assert.Equal(500_000, editor.TempoMap.Changes[0].MicrosecondsPerQuarterNote);
        Assert.False(editor.CanUndoTempoEdit);
        Assert.False(editor.UndoTempoEdit());
        Assert.True(editor.SetTempoChanges([new(0, 600_000)]));
        Assert.True(editor.IsDirty);
    }

    [Fact]
    public void NoOpAndInvalidTempoEditsLeaveStateAndHistoryIntact()
    {
        var editor = CreateEditor();
        var original = editor.TempoMap;
        int changed = 0;
        editor.Changed += () => changed++;

        Assert.False(editor.SetTempoChanges([new(0, 500_000)]));
        Assert.Throws<ArgumentException>(() => editor.SetTempoChanges([new(0, 500_000), new(0, 600_000)]));
        Assert.Throws<ArgumentException>(() => editor.SetTempoChanges([new(-1, 500_000)]));
        Assert.Throws<ArgumentException>(() => editor.SetTempoChanges([new(0, 0)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetTempoChanges([new(0, 0x1000000)]));
        Assert.Throws<ArgumentNullException>(() => editor.SetTempoChanges(null!));

        Assert.Same(original, editor.TempoMap);
        Assert.False(editor.IsDirty);
        Assert.False(editor.CanUndoTempoEdit);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void AddingAndRemovingLaterPointsRetainsAnOpeningTempo()
    {
        var editor = CreateEditor();
        Assert.True(editor.SetTempoChanges([new(1920, 800_000), new(960, 0xffffff)]));
        Assert.Equal(new[] { new TempoChange(0, 500_000), new TempoChange(960, 0xffffff), new TempoChange(1920, 800_000) },
            editor.TempoMap.Changes);
        Assert.True(editor.SetTempoChanges([]));
        Assert.Equal(new TempoChange(0, 500_000), Assert.Single(editor.TempoMap.Changes));
        Assert.True(editor.UndoTempoEdit());
        Assert.Equal(3, editor.TempoMap.Changes.Count);
    }

    private static SongTrackEditor CreateEditor() => new(new Song(TempoMap.Constant(120, 960),
        [new SongTrack("Piano", [new SongNote(0, 240, 0, 60, 96)])]), imported: true);

    [Fact]
    public void MovingOneGlobalMarkerChangesBothTracksTimingAndUndoesToSavedMap()
    {
        var tracks = new[]
        {
            new SongTrack("Keys", [new SongNote(960, 240, 0, 60, 80)]),
            new SongTrack("Bass", [new SongNote(960, 120, 1, 36, 72)])
        };
        var editor = new SongTrackEditor(new Song(TempoMap.Constant(120, 480), tracks));
        const int slowTempo = 666_667;
        Assert.True(editor.EditTempoMarker(480, slowTempo));
        editor.MarkSaved();
        var saved = editor.TempoMap;

        Assert.True(editor.MoveTempoMarker(480, 720));
        var song = editor.SnapshotSong();
        Assert.Equal(new TempoChange(720, slowTempo), song.TempoMap.Changes[1]);
        for (int i = 0; i < tracks.Length; i++)
        {
            var note = Assert.Single(song.Tracks[i].Notes);
            Assert.Equal(tracks[i].Notes[0], note);
            Assert.Equal(52_000, song.TempoMap.TickToSample(note.StartTick, 48_000));
        }
        Assert.True(editor.UndoTempoEdit());
        Assert.Same(saved, editor.TempoMap);
        Assert.False(editor.IsDirty);

        Assert.True(editor.EditTempoMarker(480, 600_000));
        Assert.Equal(2, editor.TempoMap.Changes.Count);
        Assert.True(editor.EditTempoMarker(480, null));
        Assert.Single(editor.TempoMap.Changes);
        Assert.True(editor.UndoTempoEdit());
        Assert.Equal(new TempoChange(480, 600_000), editor.TempoMap.Changes[1]);
    }

    [Fact]
    public void IgnoredMarkerGesturesPreserveOriginalMapAndUndoHistory()
    {
        var map = new TempoMap(480, [new(0, 500_000), new(480, 600_000), new(720, 750_000)]);
        var editor = new SongTrackEditor(new Song(map, [new SongTrack("Piano", [])]));
        Assert.False(editor.EditTempoMarker(0, null));
        Assert.False(editor.EditTempoMarker(100, null));
        Assert.False(editor.EditTempoMarker(-1, 600_000));
        Assert.False(editor.EditTempoMarker(480, 0));
        Assert.False(editor.EditTempoMarker(720, 0x1000000));
        Assert.False(editor.EditTempoMarker(720, 750_000));
        Assert.False(editor.MoveTempoMarker(0, 10));
        Assert.False(editor.MoveTempoMarker(480, 0));
        Assert.False(editor.MoveTempoMarker(480, 720));
        Assert.False(editor.MoveTempoMarker(600, 960));
        Assert.Same(map, editor.TempoMap);
        Assert.False(editor.CanUndoTempoEdit);
        Assert.False(editor.IsDirty);
    }
}
