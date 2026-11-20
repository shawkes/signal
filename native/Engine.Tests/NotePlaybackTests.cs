using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class NotePlaybackTests
{
    private const int SampleRate = 48_000;
    private const long Origin = 10_000;

    [Fact]
    public void SchedulesAnEditedSnapshotAcrossBuffersWithoutRecordingIt()
    {
        var engine = Create();
        var notes = new List<SongNote> { new(2, 3, 0, 60, 100) };
        engine.SetRecording(true, 0, timestamp: Origin);
        Assert.True(engine.PlayNotes(notes, new GateSynth(0.2f)));
        Assert.Equal(0, engine.PlaybackTick);
        notes.Clear();

        var first = Render(engine, Origin, 112);
        Assert.Equal(0, first[2 * 99]);
        Assert.Equal(0.2f, first[2 * 100]);
        Assert.Equal(2, engine.PlaybackTick);

        var second = Render(engine, Origin + 112, 200);
        Assert.Equal(0.2f, second[2 * 137]);
        Assert.Equal(0, second[2 * 138]);
        Assert.Null(engine.PlaybackTick);
        Assert.False(engine.TryReadNote(out _));
    }

    [Fact]
    public void StartsThePlaybackMetronomeAtTheSameFrameAsTickZero()
    {
        var engine = Create();
        engine.MetronomeEnabled = true;
        engine.PlayNotes([new(0, 120, 0, 60, 100)], new GateSynth(0.2f));

        var output = Render(engine, Origin, 32);

        Assert.Equal(0.45f, output[0], precision: 6);
    }

    [Fact]
    public void StopsOnlyPlaybackAndLeavesLiveMidiSounding()
    {
        var engine = Create(new GateSynth(0.1f));
        engine.TryEnqueue(new MidiMessage(Origin, 0x90, 60, 90));
        engine.PlayNotes([new(0, 480, 0, 60, 100)], new GateSynth(0.2f));
        Assert.Equal(0.3f, Render(engine, Origin, 80)[0], precision: 6);

        engine.StopPlayback();
        Assert.Null(engine.PlaybackTick);
        var stopped = Render(engine, Origin + 80, 600);
        Assert.Equal(0.3f, stopped[0], precision: 6);
        Assert.Equal(0.1f, stopped[^1]);

        engine.TryEnqueue(new MidiMessage(Origin + 680, 0x80, 60, 0));
        Assert.Equal(0, Render(engine, Origin + 680, 80)[0]);
    }

    [Fact]
    public void KeepsOverlappingSameKeyNotesSoundingUntilTheLastRelease()
    {
        var engine = Create();
        engine.PlayNotes([new(0, 3, 0, 60, 100), new(2, 3, 0, 60, 100)], new GateSynth(0.2f));

        var output = Render(engine, Origin, 300);

        Assert.Equal(0.2f, output[2 * 150]);
        Assert.Equal(0.2f, output[2 * 249]);
        Assert.Equal(0, output[2 * 250]);
    }

    [Fact]
    public void PlaybackDoesNotAllocateInTheWarmAudioCallback()
    {
        var engine = Create();
        engine.MetronomeEnabled = true;
        engine.PlayNotes(Enumerable.Range(0, 480).Select(i => new SongNote(i * 2, 1, 0, 60, 100)), new GateSynth(0.2f));
        var output = new float[256];
        for (int i = 0; i < 100; i++)
        {
            long frame = Origin + i * 128;
            engine.Render(output, frame, new AudioClockAnchor(frame, frame));
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 100; i < 200; i++)
        {
            long frame = Origin + i * 128;
            engine.Render(output, frame, new AudioClockAnchor(frame, frame));
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void PublishesTakeCompletionAfterClosingHeldNotes()
    {
        var engine = Create();
        long take = engine.SetRecording(true, 0, timestamp: Origin);
        engine.TryEnqueue(new MidiMessage(Origin + 5, 0x90, 60, 64));
        engine.SetRecording(false, 0, timestamp: Origin + 80);

        Render(engine, Origin, 80);
        Assert.Equal(0, engine.CompletedTakeId);
        Assert.False(engine.TryReadNote(out _));

        Render(engine, Origin + 80, 80);
        Assert.Equal(take, engine.CompletedTakeId);
        Assert.True(engine.TryReadNote(out var note));
        Assert.Equal(take, note.TakeId);
        Assert.Equal(75, note.PlayedLengthFrames);
    }

    private static LatencyEngine Create(IMonitorSynth? synth = null) =>
        new(SampleRate, synth ?? SilentMonitorSynth.Instance, timestampFrequency: SampleRate);

    private static float[] Render(LatencyEngine engine, long firstFrame, int frames)
    {
        var output = new float[frames * 2];
        engine.Render(output, firstFrame, new AudioClockAnchor(firstFrame, firstFrame));
        return output;
    }

    private sealed class GateSynth(float amplitude) : IMonitorSynth
    {
        private bool _gate;

        public void ProcessMidi(byte status, byte data1, byte data2) =>
            _gate = status == 0x90 && data2 != 0;

        public void Render(Span<float> interleavedStereo) => interleavedStereo.Fill(_gate ? amplitude : 0);
    }
}
