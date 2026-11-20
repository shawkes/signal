using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class PianoRollDocumentTests
{
    [Fact]
    public void EditsPreserveNoteIdentityAndUndoInReverseOrder()
    {
        var document = new PianoRollDocument();
        var original = new SongNote(120, 227, 3, 61, 103);
        long id = document.Add(original);
        long adjacentId = document.Add(original with { StartTick = 480 });

        Assert.True(document.Move(id, 240, 65));
        Assert.Equal(new PianoRollNote(id, original with { StartTick = 240, NoteNumber = 65 }), document.Notes[0]);
        Assert.True(document.Resize(id, 210, 257));
        Assert.Equal(new SongNote(210, 257, 3, 65, 103), document.Notes[0].Note);
        Assert.True(document.Delete(id));
        Assert.Equal(adjacentId, Assert.Single(document.Notes).Id);

        Assert.True(document.Undo());
        Assert.Equal(new PianoRollNote(id, new SongNote(210, 257, 3, 65, 103)), document.Notes[0]);
        Assert.True(document.Undo());
        Assert.Equal(new SongNote(240, 227, 3, 65, 103), document.Notes[0].Note);
        Assert.True(document.Undo());
        Assert.Equal(new PianoRollNote(id, original), document.Notes[0]);
        Assert.True(document.Undo());
        Assert.Equal(id, Assert.Single(document.Notes).Id);
        Assert.True(document.Undo());
        Assert.Empty(document.Notes);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void AppendedTakeIsOneEditAndPlaybackSnapshotSurvivesUndo()
    {
        var document = new PianoRollDocument();
        SongNote[] take = [new(0, 120, 0, 60, 90), new(120, 120, 0, 62, 92)];
        int changes = 0;
        document.Changed += () => changes++;

        document.Append(take);
        var snapshot = document.CreatePlaybackSnapshot();
        Assert.Equal(take, snapshot);
        long lastTakeNoteId = document.Notes[^1].Id;
        Assert.Equal(1, changes);

        Assert.True(document.Undo());
        Assert.Empty(document.Notes);
        Assert.False(document.CanUndo);
        Assert.Equal(take, snapshot);
        Assert.Equal(2, changes);
        Assert.True(document.Add(take[0]) > lastTakeNoteId);
    }

    [Fact]
    public void AddRangeReturnsTheFirstNewIdAndDoesNotReuseUndoneIds()
    {
        var note = new SongNote(300, 120, 0, 60, 90);
        var document = new PianoRollDocument([note]);
        var added = new[] { note with { StartTick = 120 }, note with { StartTick = 240 } };
        int changes = 0;
        document.Changed += () => changes++;

        Assert.Equal(0, document.AddRange([]));
        Assert.Equal(0, document.Revision);
        long firstId = document.AddRange(added);
        Assert.Equal(new PianoRollNote(firstId, added[0]), document.Notes[1]);
        Assert.Equal(new PianoRollNote(firstId + 1, added[1]), document.Notes[2]);
        Assert.Equal(1, changes);

        Assert.True(document.Undo());
        Assert.Equal(note, Assert.Single(document.Notes).Note);
        Assert.Equal(0, document.Revision);
        Assert.Equal(2, changes);
        Assert.True(document.AddRange(added) > firstId + 1);
    }

    [Fact]
    public void ReplaceManyRetainsIdentityAndOrderAndUndoesAsOneEdit()
    {
        var first = new SongNote(30, 20, 1, 61, 90);
        var document = new PianoRollDocument([first, first with { StartTick = 10 }, first with { NoteNumber = 63 }]);
        Assert.True(document.Move(document.Notes[0].Id, 40, 61));
        var before = document.Notes.ToArray();
        long saved = document.Revision;
        var moved = before[0] with { Note = before[0].Note with { StartTick = 53, NoteNumber = 65 } };
        var resized = before[2] with { Note = before[2].Note with { LengthTicks = 29 } };
        int changes = 0;
        document.Changed += () => changes++;

        Assert.True(document.ReplaceMany([resized, before[1], moved]));
        Assert.Equal(new[] { moved, before[1], resized }, document.Notes);
        Assert.True(document.Revision > saved);
        Assert.Equal(1, changes);

        Assert.True(document.Undo());
        Assert.Equal(before, document.Notes);
        Assert.Equal(saved, document.Revision);
        Assert.Equal(2, changes);
        Assert.True(document.CanUndo);
    }

    [Fact]
    public void DeleteManyRestoresNonAdjacentNotesInOriginalOrderOnUndo()
    {
        var note = new SongNote(0, 120, 2, 65, 91);
        var document = new PianoRollDocument(new long[] { 0, 700, 200, 500, 400, 300 }
            .Select(tick => note with { StartTick = tick }));
        var before = document.Notes.ToArray();
        int changes = 0;
        document.Changed += () => changes++;

        Assert.True(document.DeleteMany([before[4].Id, before[0].Id, before[2].Id]));
        Assert.Equal(new[] { before[1], before[3], before[5] }, document.Notes);
        Assert.Equal(1, changes);
        Assert.True(document.Revision > 0);

        Assert.True(document.Undo());
        Assert.Equal(before, document.Notes);
        Assert.Equal(0, document.Revision);
        Assert.Equal(2, changes);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void RejectedGroupEditsLeaveNotesAndHistoryUntouched()
    {
        var note = new SongNote(0, 120, 0, 60, 90);
        var document = new PianoRollDocument([note, note with { StartTick = 120 }]);
        var before = document.Notes.ToArray();
        var changed = before[0] with { Note = note with { StartTick = 15 } };
        long staleId = before[^1].Id + 1;
        int changes = 0;
        document.Changed += () => changes++;

        Assert.False(document.ReplaceMany([]));
        Assert.False(document.ReplaceMany(before));
        Assert.False(document.ReplaceMany([changed, changed with { Id = staleId }]));
        Assert.False(document.ReplaceMany([changed, before[0]]));
        var invalid = before[1] with { Note = note with { Velocity = 0 } };
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReplaceMany([changed, invalid]));
        Assert.False(document.DeleteMany([]));
        Assert.False(document.DeleteMany([before[0].Id, staleId]));
        Assert.False(document.DeleteMany([before[0].Id, before[0].Id]));

        Assert.Equal(before, document.Notes);
        Assert.Equal(0, document.Revision);
        Assert.Equal(0, changes);
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void InvalidBatchAndRedundantEditsLeaveHistoryUntouched()
    {
        var document = new PianoRollDocument();
        var note = new SongNote(0, 120, 0, 60, 90);
        long id = document.Add(note);
        int changes = 0;
        document.Changed += () => changes++;

        document.Append([]);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Append([note, note with { LengthTicks = -1 }]));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Resize(id, long.MaxValue, 2));
        Assert.False(document.Move(id, note.StartTick, note.NoteNumber));
        Assert.False(document.Delete(id + 1));
        Assert.Equal(0, changes);
        Assert.Equal(note, Assert.Single(document.Notes).Note);
        Assert.True(document.Undo());
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void SeededNotesHaveNoUndoHistoryAndUndoRestoresTheSavedRevision()
    {
        var original = new SongNote(37, 0, 4, 64, 90);
        var document = new PianoRollDocument([original]);
        var note = Assert.Single(document.Notes);
        Assert.Equal(original, note.Note);
        Assert.False(document.CanUndo);
        Assert.Equal(0, document.Revision);

        Assert.True(document.Resize(note.Id, 37, 20));
        long saved = document.Revision;
        Assert.True(document.Move(note.Id, 40, 65));
        long abandonedEdit = document.Revision;
        Assert.True(document.Undo());
        Assert.Equal(saved, document.Revision);
        Assert.True(document.Move(note.Id, 42, 67));
        Assert.True(document.Revision > abandonedEdit);
    }
}
