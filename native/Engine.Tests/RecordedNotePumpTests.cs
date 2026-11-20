using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class RecordedNotePumpTests
{
    private const int Rate = 2_000;
    private const long Origin = 10_000;

    [Fact]
    public void StalledEditorReceivesEveryNoteAndTheFinalReleaseWhileOverdubContinues()
    {
        const int notesPerBlock = 128;
        const int blocks = 32;
        const int loopFrames = 8_192;
        var tempo = TempoMap.Constant(120, ticksPerQuarter: 1_000);
        var engine = new LatencyEngine(Rate, SilentMonitorSynth.Instance, tempo, timestampFrequency: Rate);
        using var pump = new RecordedNotePump(engine);
        long take = engine.RecordSong(new Song(tempo, []), SilentMonitorSynth.Instance, countIn: false,
            offsetMilliseconds: 0, timestamp: Origin, loop: new LoopRange(0, loopFrames), overdubSynth: new GateSynth());
        pump.BeginTake(take);
        var audio = new float[notesPerBlock * 4 * 2];
        long frame = Origin;

        // The editor does not touch its queue during either pass. Synchronizing each synthetic
        // audio burst with the collector lets the test run faster than real time on slow machines.
        for (int block = 0; block < blocks; block++)
        {
            for (int i = 0; i < notesPerBlock; i++)
            {
                long at = frame + i * 4 + 1;
                Assert.True(engine.TryEnqueue(new MidiMessage(at, 0x90, 60, 100)));
                Assert.True(engine.TryEnqueue(new MidiMessage(at + 2, 0x80, 60, 0)));
            }
            engine.Render(audio, frame, new(frame, frame));
            if (frame - Origin >= loopFrames)
                Assert.Contains(audio, sample => sample > 0);
            Assert.True(SpinWait.SpinUntil(() => pump.HasCaughtUp, TimeSpan.FromSeconds(5)));
            frame += audio.Length / 2;
        }

        Assert.True(engine.TryEnqueue(new MidiMessage(frame + 1, 0x90, 67, 80)));
        engine.StopSongRecording(frame + 38);
        engine.Render(audio, frame, new(frame, frame));
        Assert.True(SpinWait.SpinUntil(() => pump.CompletedTakeId == take, TimeSpan.FromSeconds(5)));

        for (int i = 0; i < blocks * notesPerBlock; i++)
        {
            Assert.True(pump.TryReadNote(out var note));
            Assert.Equal(take, note.TakeId);
            Assert.Equal(Origin + i * 4 + 1, note.ReceivedFrame);
            Assert.Equal((i * 4 + 1) % loopFrames, note.StartTick);
            Assert.Equal(2, note.LengthTicks);
        }
        Assert.True(pump.TryReadNote(out var final));
        Assert.Equal((byte)67, final.NoteNumber);
        Assert.Equal(37, final.LengthTicks);
        Assert.False(pump.TryReadNote(out _));
        Assert.Equal(0, engine.DroppedRecordedNotes);
        Assert.Equal(0, engine.DroppedMidiMessages);
    }

    [Fact]
    public void RestartAndDeviceFailureFlushCompletedNotesEvenWithoutAStopAcknowledgment()
    {
        var engine = new LatencyEngine(Rate, SilentMonitorSynth.Instance, timestampFrequency: Rate);
        using var pump = new RecordedNotePump(engine);
        long first = engine.SetRecording(true, 0, timestamp: Origin);
        pump.BeginTake(first);
        engine.SetRecording(false, 0, timestamp: Origin + 4);
        var audio = new float[32];
        engine.Render(audio, Origin, new(Origin, Origin));
        Assert.True(SpinWait.SpinUntil(() => pump.CompletedTakeId == first, TimeSpan.FromSeconds(5)));

        long nextFrame = Origin + audio.Length / 2;
        long second = engine.SetRecording(true, 0, timestamp: nextFrame);
        pump.BeginTake(second);
        Assert.True(engine.TryEnqueue(new MidiMessage(nextFrame + 2, 0x90, 64, 100)));
        Assert.True(engine.TryEnqueue(new MidiMessage(nextFrame + 9, 0x80, 64, 0)));
        engine.Render(audio, nextFrame, new(nextFrame, nextFrame));
        // A failed output will never render a stop command.
        pump.Dispose();
        Assert.Equal(first, pump.CompletedTakeId);
        Assert.True(pump.TryReadNote(out var note));
        Assert.Equal(second, note.TakeId);
        Assert.Equal(7, note.PlayedLengthFrames);
        Assert.False(pump.TryReadNote(out _));
    }

    private sealed class GateSynth : IMonitorSynth
    {
        private bool _held;
        public void ProcessMidi(byte status, byte data1, byte data2) => _held = (status & 0xf0) == 0x90 && data2 != 0;
        public void Render(Span<float> output) => output.Fill(_held ? 0.25f : 0);
        public void Reset() => _held = false;
    }
}
