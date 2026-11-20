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
        Midi(362, 0x90, 58); // Outside the offset plus the 25 ms unquantized pickup window.
        Midi(363, 0x90, 59);
        Midi(371, 0x80, 59);
        Midi(388, 0x90, 60);
        Midi(438, 0x80, 60);
        Midi(450, 0x90, 64);
        Render(0, 499);
        Assert.False(_engine.TryReadNote(out _));
        Assert.Equal(0, _engine.CopyHeldRecordingNotes(take, _held));

        Render(499, 1);
        Render(500, 10);
        Assert.True(_engine.TryReadNote(out var pickup));
        Assert.Equal((59, 0L, 8L), ((int)pickup.NoteNumber, pickup.StartTick, pickup.LengthTicks));
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

    [Theory]
    [InlineData(10, 16)]
    [InlineData(-112, 16)]
    [InlineData(-112, 32)]
    [InlineData(0, 64)]
    public void QuantizesMatchingDrumPatternsTheSameWayInBarsOneAndTwo(int offset, int division)
    {
        const int downbeat = 2_000;
        const int barFrames = 2_000;
        int early = 300 / division;
        var song = new Song(TempoMap.Constant(120, 500), []);
        _engine.RecordSong(song, SilentMonitorSynth.Instance, countIn: true, offsetMilliseconds: offset,
            quantizationDivision: division, timestamp: Origin);

        Midi(downbeat + offset - 1000 / division, 0x99, 50); // Too early to snap to beat one.
        var pattern = new (int Tick, byte Pitch, int Length)[]
        {
            (0, 36, 22), (0, 42, 10), (250, 42, 10), (500, 38, 15), (500, 42, 10),
            (750, 42, 10), (1000, 36, 22), (1000, 42, 10), (1250, 42, 10),
            (1500, 38, 15), (1500, 42, 10), (1750, 42, 10)
        };
        for (int bar = 0; bar < 2; bar++)
        {
            foreach (var (tick, pitch, length) in pattern)
            {
                long attack = downbeat + bar * barFrames + tick + offset +
                    (bar == 0 || tick == 0 ? -early : early);
                Midi(attack, 0x99, pitch);
                Midi(attack + length, 0x89, pitch);
            }
        }
        // The queued audio can cross the downbeat before it reaches the speakers.
        Render(0, downbeat);
        _engine.Render(new float[100], Origin + downbeat, new(Origin, Origin),
            callbackTimestamp: Origin + downbeat - 20);
        Assert.False(_engine.TryReadNote(out _));
        Render(downbeat + 50, 2 * barFrames);

        var captured = new List<SongNote>();
        while (_engine.TryReadNote(out var note))
            captured.Add(note.ToSongNote());

        var expected = Enumerable.Range(0, 2)
            .SelectMany(bar => pattern.Select(note => new SongNote(note.Tick + bar * barFrames,
                note.Length, 9, note.Pitch, 100)));
        Assert.Equal(expected.OrderBy(note => note.StartTick).ThenBy(note => note.NoteNumber),
            captured.OrderBy(note => note.StartTick).ThenBy(note => note.NoteNumber));
    }

    [Fact]
    public void QuantizedLoopPickupUsesTheTempoAtTheLoopStart()
    {
        // The loop opens at 60 BPM, so half a sixteenth is 125 ms.
        var tempo = new TempoMap(500, [new(0, 500_000), new(500, 1_000_000)]);
        Start(countIn: true, offset: 20, loop: new LoopRange(500, 1000), tempo: tempo, division: 16);
        Midi(894, 0x99, 36); // Outside the pickup window by one frame.
        Midi(895, 0x99, 42); // Exactly half a cell early after applying the offset.
        Midi(935, 0x89, 42);
        Render(0, 1_000);
        Assert.False(_engine.TryReadNote(out _));
        Render(1_000, 100);
        Assert.True(_engine.TryReadNote(out var note));
        Assert.Equal((42, 500L, 20L, 40L), ((int)note.NoteNumber, note.StartTick, note.LengthTicks, note.PlayedLengthFrames));
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

    [Theory]
    [InlineData(-112, 0, 388)]
    [InlineData(10, 16, 450)]
    public void CancelBeforeThePresentedDownbeatDiscardsNotesEvenWhenBarOneIsAlreadyQueued(int offset, int division, int attack)
    {
        long take = Start(countIn: true, offset: offset, division: division);
        Midi(attack, 0x90, 60);
        Midi(attack + 15, 0x80, 60);
        Midi(attack + 25, 0x90, 64);
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

    private long Start(bool countIn, double offset = -112, LoopRange? loop = null, TempoMap? tempo = null, int division = 0)
    {
        var song = new Song(tempo ?? TempoMap.Constant(120, 500), [], new SongMeter(1, 4));
        return _engine.RecordSong(song, SilentMonitorSynth.Instance, countIn, offset,
            quantizationDivision: division, timestamp: Origin, loop: loop);
    }

    private void Midi(long elapsed, byte status, byte pitch) =>
        Assert.True(_engine.TryEnqueue(new MidiMessage(Origin + elapsed, status, pitch, 100)));

    private void Render(long elapsed, int length) =>
        _engine.Render(new float[2 * length], Origin + elapsed, new AudioClockAnchor(Origin, Origin));
}
