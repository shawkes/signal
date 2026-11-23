using Signal.Native.Application;
using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.MidiFiles.Tests;

public sealed class SongSessionTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("signal-session-").FullName;

    [Fact]
    public async Task UndoCropAfterSavingRestoresTheSourceAndMakesTheSongDirty()
    {
        string originalPath = Path.Combine(_directory, "original.mid");
        string outputPath = Path.Combine(_directory, "cropped.mid");
        var note = new SongNote(1920, 240, 0, 60, 100);
        var source = new Song(TempoMap.Constant(120), [new SongTrack("Piano", [note])]);
        MidiFileDocument.WriteNew(originalPath, source);

        var session = new SongSession();
        await session.OpenAsync(originalPath);
        var originalTrack = Assert.Single(session.Editor.Tracks);
        var croppedTrack = await session.CropAsync(1920, 2160, originalTrack);
        Assert.True(session.Editor.IsDirty);
        Assert.Equal(note with { StartTick = 0 }, Assert.Single(croppedTrack.Document.Notes).Note);

        await session.SaveAsync(outputPath);
        Assert.False(session.Editor.IsDirty);
        Assert.Same(originalTrack, session.UndoCrop());
        Assert.True(session.Editor.IsDirty);
        Assert.False(session.CanUndoCrop);
        Assert.Equal(outputPath, session.FilePath);

        await session.SaveAsync(outputPath);
        var restored = MidiFileDocument.Read(outputPath);
        Assert.Equal(note, Assert.Single(Assert.Single(restored.Song.Tracks).Notes));
    }

    [Fact]
    public async Task FailedSavePreservesTheCurrentPathAndUnsavedEdits()
    {
        var session = new SongSession();
        string path = Path.Combine(_directory, "song.mid");
        await session.SaveAsync(path);
        var track = Assert.Single(session.Editor.Tracks);
        var note = new SongNote(0, 120, 0, 60, 100);
        track.Document.Add(note);

        string missingDirectory = Path.Combine(_directory, "missing", "song.mid");
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => session.SaveAsync(missingDirectory));
        Assert.Equal(path, session.FilePath);
        Assert.True(session.Editor.IsDirty);
        Assert.Equal(note, Assert.Single(track.Document.Notes).Note);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
