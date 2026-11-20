using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class FirstDownbeatTests
{
    // At this resolution and the opening tempo of 120 BPM, a sample and tick each occupy 1 ms.
    private const int Rate = 1000;
    private const long Origin = 5000;
    private readonly LatencyEngine _engine = new(Rate, SilentMonitorSynth.Instance, timestampFrequency: Rate);
    private readonly HeldRecordingNote[] _held = new HeldRecordingNote[4];

    [Fact]
    public void NegativeOffsetKeepsAnOpeningTapEvenIfItWasReleasedDuringCountIn()
    {
        long take = Start(countIn: true);
        Midi(387, 0x90, 58); // One millisecond outside the compensation window.
        Midi(388, 0x90, 60);
        Midi(438, 0x80, 60);
        Midi(450, 0x90, 64);
        Render(0, 499);
        Assert.False(_engine.TryReadNote(out _));
        Assert.Equal(0, _engine.CopyHeldRecordingNotes(take, _held));

        Render(499, 1);
        Render(500, 10);
        Assert.True(_engine.TryReadNote(out var tap));
        Assert.Equal((0L, 50L), (tap.StartTick, tap.LengthTicks));
        Assert.Equal((-112d, 0d), (tap.RawMillisecondsFromBeat, tap.MillisecondsFromBeat));
        Assert.Equal(1, _engine.CopyHeldRecordingNotes(take, _held));
        Assert.Equal(new HeldRecordingNote(0, 64, 0, 62, 60), _held[0]);
        Midi(510, 0x80, 64);
        Midi(515, 0x80, 58);
        Render(510, 20);
        Assert.True(_engine.TryReadNote(out var held));
        Assert.Equal((62L, 60L), (held.StartTick, held.LengthTicks));
        Assert.False(_engine.TryReadNote(out _));
    }

    [Fact]
    public void CancelInLastPartOfCountInDiscardsCompletedAndHeldOpeningNotes()
    {
        long take = Start(countIn: true);
        Midi(388, 0x90, 60);
        Midi(420, 0x80, 60);
        Midi(440, 0x90, 64);
        Render(0, 480);
        _engine.StopSongRecording(Origin + 490);
        Render(480, 160);
        Assert.Equal(take, _engine.CompletedTakeId);
        Assert.False(_engine.TryReadNote(out _));
        Assert.Equal(0, _engine.CopyHeldRecordingNotes(take, _held));
    }

    [Fact]
    public void CancelBeforeThePresentedDownbeatDiscardsNotesEvenWhenBarOneIsAlreadyQueued()
    {
        long take = Start(countIn: true);
        Midi(388, 0x90, 60);
        Midi(420, 0x80, 60);
        Midi(440, 0x90, 64);
        Render(0, 500);
        _engine.Render(new float[60], Origin + 500, new(Origin, Origin), callbackTimestamp: Origin + 480);
        Assert.False(_engine.TryReadNote(out _));
        _engine.StopSongRecording(Origin + 490);
        _engine.Render(new float[60], Origin + 530, new(Origin, Origin), callbackTimestamp: Origin + 510);
        Assert.Equal(take, _engine.CompletedTakeId);
        Assert.False(_engine.TryReadNote(out _));
    }

    [Theory]
    [InlineData(-112, 80)]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    public void AnArmedTakeCanCaptureInputBeforeItsFirstWritableOutputFrame(int offset, long expectedStart)
    {
        long take = Start(countIn: false, offset);
        Midi(8, 0x90, 60);
        Render(40, 10); // 40 ms of audio was already queued when Record was pressed.
        Assert.Equal(1, _engine.CopyHeldRecordingNotes(take, _held));
        Assert.Equal(new HeldRecordingNote(0, 60, 0, expectedStart, 42), _held[0]);
        Midi(-1, 0x90, 59); // Late delivery of a timestamp from before Record.
        Midi(51, 0x80, 59);
        Midi(88, 0x80, 60);
        Render(50, 40);
        Assert.True(_engine.TryReadNote(out var note));
        Assert.Equal((60, expectedStart, 80L), ((int)note.NoteNumber, note.StartTick, note.LengthTicks));
        Assert.False(_engine.TryReadNote(out _));
    }

    [Fact]
    public void OpeningLoopNoteUsesTheCountInTempoAndKeepsItsPlayedLength()
    {
        var tempo = new TempoMap(500, [new(0, 250_000), new(500, 500_000)]);
        long take = Start(countIn: true, loop: new LoopRange(500, 600), tempo: tempo);
        Midi(388, 0x90, 62); // One full compensation window early, longer than this loop.
        Render(0, 501);
        Assert.Equal(1, _engine.CopyHeldRecordingNotes(take, _held));
        Assert.Equal(new HeldRecordingNote(0, 62, 0, 500, 100), _held[0]);
        Midi(508, 0x80, 62);
        Render(501, 8);
        Assert.True(_engine.TryReadNote(out var first));
        Assert.True(_engine.TryReadNote(out var wrap));
        Assert.Equal((500L, 100L, 100L), (first.StartTick, first.LengthTicks, first.PlayedLengthFrames));
        Assert.Equal((500L, 20L, 20L), (wrap.StartTick, wrap.LengthTicks, wrap.PlayedLengthFrames));
        Assert.False(_engine.TryReadNote(out _));
    }

    private long Start(bool countIn, double offset = -112, LoopRange? loop = null, TempoMap? tempo = null)
    {
        var song = new Song(tempo ?? TempoMap.Constant(120, 500), [], new SongMeter(1, 4));
        return _engine.RecordSong(song, SilentMonitorSynth.Instance, countIn, offset,
            quantizationDivision: 0, timestamp: Origin, loop: loop);
    }

    private void Midi(long elapsed, byte status, byte pitch) =>
        Assert.True(_engine.TryEnqueue(new MidiMessage(Origin + elapsed, status, pitch, 100)));

    private void Render(long elapsed, int length) =>
        _engine.Render(new float[2 * length], Origin + elapsed, new AudioClockAnchor(Origin, Origin));
}
