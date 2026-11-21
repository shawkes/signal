using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class TrackMixTests
{
    [Fact]
    public void SoloAndMuteFilterTheBackingWithoutChangingTheSavedSong()
    {
        var bass = new SongNote(0, 100, 0, 36, 90);
        var drums = new SongNote(500, 100, 9, 42, 90);
        var editor = new SongTrackEditor(new Song(TempoMap.Constant(120),
            [new SongTrack("Bass", [bass]), new SongTrack("Drums", [drums])]));

        editor.Tracks[0].IsSoloed = true;
        Assert.Equal(bass, Assert.Single(editor.SnapshotBackingSong(600).Tracks.SelectMany(track => track.Notes)));
        editor.Tracks[1].IsSoloed = true;
        editor.Tracks[0].IsMuted = true;
        var backing = editor.SnapshotBackingSong(600);
        Assert.Equal(drums, Assert.Single(backing.Tracks.SelectMany(track => track.Notes)));
        Assert.All(backing.Tracks, track => Assert.Equal(600, track.EndTick));
        Assert.Equal(new[] { bass, drums }, editor.SnapshotSong().Tracks.SelectMany(track => track.Notes));
        Assert.False(editor.IsDirty);
    }
}
