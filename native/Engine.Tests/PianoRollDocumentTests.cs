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
    public void InvalidBatchAndRedundantEditsLeaveHistoryUntouched()
    {
        var document = new PianoRollDocument();
        var note = new SongNote(0, 120, 0, 60, 90);
        long id = document.Add(note);
        int changes = 0;
        document.Changed += () => changes++;

        document.Append([]);
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Append([note, note with { LengthTicks = 0 }]));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.Resize(id, long.MaxValue, 2));
        Assert.False(document.Move(id, note.StartTick, note.NoteNumber));
        Assert.False(document.Delete(id + 1));
        Assert.Equal(0, changes);
        Assert.Equal(note, Assert.Single(document.Notes).Note);
        Assert.True(document.Undo());
        Assert.False(document.CanUndo);
    }
}
