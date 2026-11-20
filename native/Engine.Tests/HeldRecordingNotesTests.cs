using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class HeldRecordingNotesTests
{
    private const int Rate = 1000;
    private const long Origin = 1000;
    private readonly LatencyEngine _engine = new(Rate, SilentMonitorSynth.Instance, timestampFrequency: Rate);
    private readonly HeldRecordingNote[] _held = new HeldRecordingNote[16];

    [Fact]
    public void HeldPreviewMatchesQuantizedReleaseAcrossTempoChange()
    {
        var tempo = new TempoMap(500, [new(0, 500_000), new(200, 1_000_000)]);
        long id = _engine.RecordSong(new Song(tempo, []), SilentMonitorSynth.Instance, false,
            offsetMilliseconds: 10, quantizationDivision: 64, timestamp: Origin);
        Midi(135, 0x91, 64, 100);
        Render(0, 450);
        Assert.Equal(0, _engine.CopyHeldRecordingNotes(id + 1, _held));
        Assert.Equal(1, _engine.CopyHeldRecordingNotes(id, _held));
        Assert.Equal(new HeldRecordingNote(1, 64, 0, 125, 190), _held[0]);
        Assert.False(_engine.TryReadNote(out _));

        Midi(450, 0x81, 64, 0);
        Render(450, 1);
        Assert.Equal(0, _engine.CopyHeldRecordingNotes(id, _held));
        Assert.True(_engine.TryReadNote(out var note));
        Assert.Equal((_held[0].StartTick, _held[0].LengthTicks), (note.StartTick, note.LengthTicks));
    }

    [Fact]
    public void LoopPreviewWrapsAndRemainsBoundedAcrossPasses()
    {
        var song = new Song(TempoMap.Constant(120, 500), []);
        long id = _engine.RecordSong(song, SilentMonitorSynth.Instance, false, 0, timestamp: Origin,
            loop: new LoopRange(100, 600));
        Midi(440, 0x90, 60, 100);
        Render(0, 620);
        Assert.Equal(2, _engine.CopyHeldRecordingNotes(id, _held));
        Assert.Equal(new HeldRecordingNote(0, 60, 0, 540, 60), _held[0]);
        Assert.Equal(new HeldRecordingNote(0, 60, 1, 100, 120), _held[1]);
        Render(620, 1200);
        Assert.Equal(1, _engine.CopyHeldRecordingNotes(id, _held));
        Assert.Equal(new HeldRecordingNote(0, 60, 0, 100, 500), _held[0]);

        _engine.StopSongRecording(Origin + 1820);
        Render(1820, 1);
        Assert.Equal(0, _engine.CopyHeldRecordingNotes(id, _held));
        Assert.True(_engine.TryReadNote(out var released));
        Assert.Equal(540, released.StartTick);
    }

    [Fact]
    public void CountInOmitsPracticeKeysAndAllNotesOffClearsOnlyItsChannel()
    {
        var song = new Song(TempoMap.Constant(120, 500), [], new SongMeter(1, 4));
        long id = _engine.RecordSong(song, SilentMonitorSynth.Instance, true, 0, timestamp: Origin);
        Midi(10, 0x90, 55, 100);
        Render(0, 50);
        Assert.Equal(0, _engine.CopyHeldRecordingNotes(id, _held));
        Midi(500, 0x90, 60, 100);
        Midi(510, 0x91, 60, 100);
        Render(50, 480);
        Assert.Equal(2, _engine.CopyHeldRecordingNotes(id, _held));
        Midi(530, 0xb0, 123, 0);
        Render(530, 1);
        Assert.Equal(1, _engine.CopyHeldRecordingNotes(id, _held));
        Assert.Equal(new HeldRecordingNote(1, 60, 0, 10, 21), _held[0]);
    }

    private void Midi(long elapsed, byte status, byte note, byte velocity) =>
        Assert.True(_engine.TryEnqueue(new MidiMessage(Origin + elapsed, status, note, velocity)));

    private void Render(long elapsed, int frames) =>
        _engine.Render(new float[2 * frames], Origin + elapsed, new AudioClockAnchor(Origin, Origin));
}
