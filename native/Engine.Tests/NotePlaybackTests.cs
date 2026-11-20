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
    public void PlaysEveryTrackAcrossTempoChangesWithChannelSetupBeforeTheAttack()
    {
        var engine = Create();
        var synth = new EventSynth();
        var tempo = new TempoMap(240, [new(0, 500_000), new(240, 1_000_000)]);
        var song = new Song(tempo,
        [
            new SongTrack("Piano", [new(120, 240, 0, 60, 90)], [new(0, 0xc0, 0, 0)]),
            new SongTrack("Bass", [new(240, 240, 1, 36, 100)],
                [new(240, 0xb1, 0, 0), new(240, 0xc1, 33, 0), new(360, 0xe1, 0, 64)])
        ]);

        Assert.True(engine.PlaySong(song, synth));
        Render(engine, Origin, 72_001);

        Assert.Equal(
            [
                new ReceivedMessage(0, 0xc0, 0, 0),
                new ReceivedMessage(12_000, 0x90, 60, 90),
                new ReceivedMessage(24_000, 0xb1, 0, 0),
                new ReceivedMessage(24_000, 0xc1, 33, 0),
                new ReceivedMessage(24_000, 0x91, 36, 100),
                new ReceivedMessage(48_000, 0x80, 60, 0),
                new ReceivedMessage(48_000, 0xe1, 0, 64),
                new ReceivedMessage(72_000, 0x81, 36, 0)
            ], synth.Messages);
        Assert.Equal(TempoMap.DefaultTicksPerQuarter, engine.TempoMap.TicksPerQuarter);
        Assert.Null(engine.PlaybackTick);
    }

    [Fact]
    public void ClickUsesTheSongTempoWhilePlaying()
    {
        var engine = Create();
        engine.MetronomeEnabled = true;
        var tempo = new TempoMap(960, [new(0, 500_000), new(960, 1_000_000)]);
        engine.PlaySong(new Song(tempo, [new SongTrack("Track", [new(0, 3_840, 0, 60, 100)])]),
            SilentMonitorSynth.Instance);

        Assert.Equal(0.25f, Render(engine, Origin, 24_000)[0]);
        Assert.Equal(0.16f, Render(engine, Origin + 24_000, 24_000)[0]);
        Assert.Equal(0, Render(engine, Origin + 48_000, 24_000)[0]);
        Assert.Equal(0.16f, Render(engine, Origin + 72_000, 32)[0]);
    }

    [Fact]
    public void DenseTempoClicksStayConsistentAcrossBufferBoundaries()
    {
        var tempo = new TempoMap(1, [new(0, 1)]);
        var song = new Song(tempo, [new SongTrack("Track", [new(0, 100_000, 0, 60, 100)])]);
        var whole = Create();
        var split = Create();
        whole.MetronomeEnabled = split.MetronomeEnabled = true;
        whole.PlaySong(song, SilentMonitorSynth.Instance);
        split.PlaySong(song, SilentMonitorSynth.Instance);

        const int firstFrames = 776;
        const int secondFrames = 1_176;
        var expected = Render(whole, Origin, firstFrames + secondFrames);
        var actual = Render(split, Origin, firstFrames).Concat(Render(split, Origin + firstFrames, secondFrames));
        Assert.Equal(0.25f, expected[0]);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void PlaybackIncludesAnEmptyTracksTrailingTime()
    {
        var engine = Create();
        var song = new Song(engine.TempoMap,
        [
            new SongTrack("Piano", [new(0, 1, 0, 60, 100)]),
            new SongTrack("Conductor", [], endTick: 40)
        ]);
        engine.PlaySong(song, new GateSynth(0.2f));

        var first = Render(engine, Origin, 1_000);
        Assert.Equal(0.2f, first[0]);
        Assert.Equal(0, first[^1]);
        Assert.Equal(20, engine.PlaybackTick);
        Render(engine, Origin + 1_000, 1_000);
        Assert.Null(engine.PlaybackTick);
    }

    [Fact]
    public void PlaybackDoesNotAllocateInTheWarmAudioCallback()
    {
        var engine = Create();
        engine.MetronomeEnabled = true;
        var tempo = new TempoMap(240, [new(0, 500_000), new(240, 1_000_000)]);
        var notes = Enumerable.Range(0, 480).Select(i => new SongNote(i * 2, 1, 0, 60, 100));
        engine.PlaySong(new Song(tempo, [new SongTrack("Track", notes)]), new GateSynth(0.2f));
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

    private sealed class EventSynth : IMonitorSynth
    {
        private int _frames;
        public List<ReceivedMessage> Messages { get; } = [];

        public void ProcessMidi(byte status, byte data1, byte data2) => Messages.Add(new(_frames, status, data1, data2));

        public void Render(Span<float> interleavedStereo)
        {
            _frames += interleavedStereo.Length / 2;
            interleavedStereo.Clear();
        }
    }

    private readonly record struct ReceivedMessage(int Frame, byte Status, byte Data1, byte Data2);
}
