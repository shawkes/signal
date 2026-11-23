using Signal.Native.Engine;
using Signal.Native.MidiFiles;

namespace Signal.Native.Application;

/// <summary>Owns the editable song, its MIDI source, and the history of whole-song crops.</summary>
/// <remarks>Use from one editor context and await file operations before editing or replacing the song.</remarks>
public sealed class SongSession
{
    private readonly Stack<CropUndo> _cropUndo = new();
    private MidiFileDocument? _source;
    private long _saveCount;

    public SongTrackEditor Editor { get; private set; } = CreateEmptyEditor();
    public string? FilePath { get; private set; }
    public bool CanUndoCrop => _cropUndo.Count > 0;

    public void NewSong() => ReplaceSong(CreateEmptyEditor(), null, null);

    public async Task OpenAsync(string path)
    {
        var source = await Task.Run(() => MidiFileDocument.Read(path));
        ReplaceSong(new SongTrackEditor(source.Song, imported: true), source, path);
    }

    public async Task SaveAsync(string path)
    {
        var song = Editor.SnapshotSong();
        var source = _source;
        var sourceIndices = Editor.CreateSourceIndices();
        await Task.Run(() =>
        {
            if (source is null)
                MidiFileDocument.WriteNew(path, song);
            else
                source.Write(path, song, sourceIndices);
        });
        FilePath = path;
        _saveCount++;
        Editor.MarkSaved();
    }

    public async Task<EditableSongTrack> CropAsync(long startTick, long endTick, EditableSongTrack? selectedTrack)
    {
        var previous = Editor;
        var song = previous.SnapshotSong();
        var source = _source;
        var sourceIndices = previous.CreateSourceIndices();
        var croppedSong = SongCrop.Keep(song, startTick, endTick);
        var croppedSource = source is null
            ? null
            : await Task.Run(() => source.Crop(song, sourceIndices, startTick, endTick));
        var editor = new SongTrackEditor(croppedSong, croppedSource is not null, previous.Tracks);
        editor.MarkCropUnsaved();

        int selectedIndex = 0;
        for (int index = 0; index < previous.Tracks.Count; index++)
        {
            if (ReferenceEquals(previous.Tracks[index], selectedTrack))
            {
                selectedIndex = index;
                break;
            }
        }

        _cropUndo.Push(new(previous, source, selectedTrack, _saveCount));
        Editor = editor;
        _source = croppedSource;
        return editor.Tracks[selectedIndex];
    }

    public EditableSongTrack? UndoCrop()
    {
        var previous = _cropUndo.Pop();
        if (previous.SaveCount != _saveCount)
            previous.Editor.MarkCropUnsaved();
        Editor = previous.Editor;
        _source = previous.Source;
        return previous.SelectedTrack;
    }

    private void ReplaceSong(SongTrackEditor editor, MidiFileDocument? source, string? path)
    {
        Editor = editor;
        _source = source;
        FilePath = path;
        _cropUndo.Clear();
        _saveCount = 0;
    }

    private static SongTrackEditor CreateEmptyEditor() =>
        new(new Song(TempoMap.Constant(120), [new SongTrack("Track 1", [], [new(0, 0xc0, 0, 0)])]));

    private sealed record CropUndo(SongTrackEditor Editor, MidiFileDocument? Source,
        EditableSongTrack? SelectedTrack, long SaveCount);
}
