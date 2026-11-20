using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class SongTrackEditorTests
{
    [Fact]
    public void UndoRestoresRemovedTrackIdentityNotesAndOriginalFileIndex()
    {
        var editor = Open(
            new SongTrack("Conductor", []),
            new SongTrack("Piano", [new SongNote(24, 36, 0, 60, 90)]));
        var piano = editor.Tracks[1];
        long noteId = piano.Document.Add(new SongNote(90, 36, 0, 62, 91));
        editor.MarkSaved();

        Assert.True(editor.RenameTrack(piano, " Piano solo "));
        var instrument = editor.AddInstrumentTrack();
        Assert.Equal(1, instrument.PrimaryChannel);
        Assert.True(editor.RemoveTrack(piano));
        Assert.Equal(new int?[] { 0, null }, editor.CreateSourceIndices());
        Assert.True(editor.IsDirty);

        Assert.True(editor.UndoTrackEdit());
        Assert.Same(piano, editor.Tracks[1]);
        Assert.Equal("Piano solo", piano.Name);
        Assert.Equal(noteId, piano.Document.Notes[1].Id);
        Assert.True(piano.Document.CanUndo);
        Assert.Equal(new int?[] { 0, 1, null }, editor.CreateSourceIndices());
        Assert.True(editor.UndoTrackEdit());
        Assert.True(editor.UndoTrackEdit());
        Assert.Equal("Piano", piano.Name);
        Assert.Equal(new int?[] { 0, 1 }, editor.CreateSourceIndices());
        Assert.False(editor.IsDirty);

        Assert.True(piano.Document.Undo());
        Assert.True(editor.IsDirty);
        Assert.False(editor.CanUndoTrackEdit);
    }

    [Fact]
    public void ChangingASharedChannelSoundRetainsOtherChannelsAndLaterAutomation()
    {
        SongChannelMessage[] original =
        [
            new(0, 0xb2, 0, 1),
            new(0, 0xb2, 32, 1),
            new(0, 0xc2, 17, 0),
            new(0, 0xc3, 33, 0),
            new(0, 0xb2, 64, 127),
            new(480, 0xc2, 48, 0)
        ];
        var editor = Open(
            new SongTrack("Setup", [], original),
            new SongTrack("Keys", [new SongNote(0, 120, 2, 60, 90)]));

        var setup = editor.Tracks[0];
        var keys = editor.Tracks[1];
        Assert.True(editor.SharesChannel(keys));
        Assert.Equal(new TrackSound(1, 17), editor.GetTrackSound(keys));
        Assert.True(editor.SetTrackSound(keys, new TrackSound(4, 9)));
        Assert.Equal(new TrackSound(4, 9), editor.GetTrackSound(setup));
        Assert.Equal(new SongChannelMessage(0, 0xc2, 9, 0), setup.PlaybackMessages[0]);
        Assert.Equal(original.Skip(3), setup.PlaybackMessages.Skip(1));
        Assert.Equal(new SongChannelMessage(0, 0xb2, 0, 4), keys.PlaybackMessages[0]);
        Assert.Equal(new SongChannelMessage(0, 0xc2, 9, 0), keys.PlaybackMessages[1]);
        Assert.False(editor.SetTrackSound(keys, new TrackSound(4, 9)));

        Assert.True(editor.UndoTrackEdit());
        Assert.Equal(original, setup.PlaybackMessages);
        Assert.Empty(keys.PlaybackMessages);
        Assert.False(editor.IsDirty);
    }

    [Fact]
    public void AdditionalDrumTracksShareTheCurrentKitAndPersistTheirOwnChannel()
    {
        var editor = new SongTrackEditor(new Song(TempoMap.Constant(120), [new SongTrack("Track 1", [])]));
        var firstDrums = editor.AddDrumTrack();
        Assert.True(firstDrums.IsDrum);
        Assert.Equal(SongTrackEditor.DrumChannel, firstDrums.PrimaryChannel);
        Assert.True(editor.SetTrackSound(firstDrums, new TrackSound(128, 8)));

        var secondDrums = editor.AddDrumTrack();
        Assert.True(editor.SharesChannel(secondDrums));
        Assert.Equal(new TrackSound(128, 8), editor.GetTrackSound(secondDrums));
        Assert.Equal(new SongChannelMessage(0, 0xc9, 8, 0), Assert.Single(secondDrums.PlaybackMessages));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetTrackSound(secondDrums, new TrackSound(0, 8)));

        Assert.True(editor.SetTrackSound(secondDrums, new TrackSound(128, 25)));
        Assert.Equal(new TrackSound(128, 25), editor.GetTrackSound(firstDrums));
        Assert.Equal(new SongChannelMessage(0, 0xc9, 25, 0), Assert.Single(firstDrums.PlaybackMessages));
        Assert.Equal(new SongChannelMessage(0, 0xb9, 0, 0), secondDrums.PlaybackMessages[0]);
        Assert.Equal(new SongChannelMessage(0, 0xc9, 25, 0), secondDrums.PlaybackMessages[1]);

        var reopened = new SongTrackEditor(editor.SnapshotSong(), imported: true);
        Assert.All(reopened.Tracks.Skip(1), track => Assert.True(track.IsDrum));
    }

    [Fact]
    public void EmptyAuthoredTracksReserveChannelsAndTrackRemovalCanFreeOne()
    {
        var editor = new SongTrackEditor(new Song(TempoMap.Constant(120), [new SongTrack("Track 1", [])]));
        Assert.False(editor.RemoveTrack(editor.Tracks[0]));
        for (byte channel = 1; channel < 16; channel++)
        {
            if (channel != SongTrackEditor.DrumChannel)
                Assert.Equal(channel, editor.AddInstrumentTrack().PrimaryChannel);
        }

        Assert.False(editor.HasInstrumentChannel);
        Assert.Throws<InvalidOperationException>(() => editor.AddInstrumentTrack());
        Assert.Equal(SongTrackEditor.DrumChannel, editor.AddDrumTrack().PrimaryChannel);
        Assert.True(editor.RemoveTrack(editor.Tracks[4]));
        Assert.True(editor.HasInstrumentChannel);
        Assert.Equal(4, editor.AddInstrumentTrack().PrimaryChannel);
    }

    private static SongTrackEditor Open(params SongTrack[] tracks) =>
        new(new Song(TempoMap.Constant(120), tracks), imported: true);
}
