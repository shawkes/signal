using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class LoopPlaybackTests
{
    private const int SampleRate = 48_000;
    private const long Origin = 1_000;
    private const int PassFrames = 120_000;
    private static readonly LoopRange TempoChangeLoop = new(1_440, 2_880);

    [Fact]
    public void RepeatsTempoMappedClickOnsetsAcrossArbitraryAudioBuffers()
    {
        var whole = Create();
        var split = Create();
        whole.MetronomeEnabled = split.MetronomeEnabled = true;
        Song song = CreateTempoChangeSong();
        whole.PlaySong(song, SilentMonitorSynth.Instance, TempoChangeLoop);
        split.PlaySong(song, SilentMonitorSynth.Instance, TempoChangeLoop);
        Assert.Equal(TempoChangeLoop.StartTick, split.PlaybackTick);

        const int totalFrames = 2 * PassFrames + 100;
        const int firstFrames = PassFrames - 17;
        const int secondFrames = 24_070;
        var expected = Render(whole, Origin, totalFrames);
        var actual = Render(split, Origin, firstFrames)
            .Concat(Render(split, Origin + firstFrames, secondFrames))
            .Concat(Render(split, Origin + firstFrames + secondFrames, totalFrames - firstFrames - secondFrames));

        Assert.Equal(expected, actual);
        for (int pass = 0; pass < 2; pass++)
        {
            int start = pass * PassFrames;
            Assert.Equal(0.16f, expected[2 * start]);
            Assert.Equal(0, expected[2 * (start + 23_999)]);
            Assert.Equal(0.25f, expected[2 * (start + 24_000)]);
            Assert.Equal(0, expected[2 * (start + 48_000)]);
            Assert.Equal(0.16f, expected[2 * (start + 72_000)]);
            Assert.Equal(0, expected[2 * (start + PassFrames - 1)]);
        }
        Assert.Equal(0.16f, expected[4 * PassFrames]);
        Assert.Equal(1_442, split.PlaybackTick);
    }

    [Fact]
    public void RestoresProgramsAndHeldNotesAndExcludesTheLoopEndpoint()
    {
        var engine = Create();
        var synth = new EventSynth();
        Assert.True(engine.PlaySong(CreateTempoChangeSong(), synth, TempoChangeLoop));
        Render(engine, Origin, 2 * PassFrames);

        Assert.Equal(new[] { 0, PassFrames }, synth.Resets);
        for (int pass = 0; pass < 2; pass++)
        {
            int start = pass * PassFrames;
            Assert.Equal(
                [new ReceivedMessage(start, 0xc0, 7, 0), new ReceivedMessage(start, 0x90, 60, 95)],
                synth.Messages.Where(message => message.Frame == start));
            Assert.Contains(new ReceivedMessage(start + 24_000, 0x91, 36, 100), synth.Messages);
            Assert.Contains(new ReceivedMessage(start + 72_000, 0xc0, 14, 0), synth.Messages);
        }
        Assert.DoesNotContain(synth.Messages, message => message.Key == 99);
        Assert.Equal(TempoChangeLoop.StartTick, engine.PlaybackTick);
        Render(engine, Origin + 2 * PassFrames, 1);
        Assert.Equal(new[] { 0, PassFrames, 2 * PassFrames }, synth.Resets);
    }

    [Fact]
    public void EnablingOutsideTheRangeSeeksAndTurningOffContinuesFromTheLoopPosition()
    {
        var engine = Create();
        var synth = new EventSynth();
        var song = new Song(engine.TempoMap,
        [
            new SongTrack("Piano", [new(10, 3, 0, 60, 95), new(20, 1, 0, 72, 95)], endTick: 32)
        ]);
        engine.PlaySong(song, synth);
        Render(engine, Origin, 100);
        Assert.Equal(2, engine.PlaybackTick);
        engine.SetPlaybackLoop(new LoopRange(10, 20));
        Render(engine, Origin + 100, 100);
        Assert.Equal(12, engine.PlaybackTick);
        Assert.Equal(100, Assert.Single(synth.Resets));
        Assert.Equal(new ReceivedMessage(100, 0x90, 60, 95), Assert.Single(synth.Messages));

        engine.SetPlaybackLoop(null);
        Render(engine, Origin + 200, 1_000);
        Assert.Null(engine.PlaybackTick);
        Assert.Single(synth.Resets);
        Assert.Contains(new ReceivedMessage(600, 0x90, 72, 95), synth.Messages);
    }

    [Fact]
    public void ChangingRangeAroundTheCurrentPositionPreservesPlayingVoices()
    {
        var engine = Create();
        var synth = new EventSynth();
        engine.PlaySong(new Song(engine.TempoMap,
            [new SongTrack("Piano", [new(0, 6, 0, 60, 95)])]), synth);
        Render(engine, Origin, 100);
        engine.SetPlaybackLoop(new LoopRange(1, 4));
        Render(engine, Origin + 100, 60);

        Assert.Empty(synth.Resets);
        Assert.Equal(new ReceivedMessage(0, 0x90, 60, 95), Assert.Single(synth.Messages));
        Assert.Equal(3, engine.PlaybackTick);
    }

    [Fact]
    public void ARangeOfSilenceAfterTheSongStillLoopsTheMetronome()
    {
        var engine = Create();
        engine.MetronomeEnabled = true;
        var synth = new EventSynth();
        engine.PlaySong(new Song(engine.TempoMap,
            [new SongTrack("Piano", [new(0, 4, 0, 60, 95)])]), synth, new LoopRange(960, 1_440));
        var output = Render(engine, Origin, 24_128);
        Assert.Equal(0.16f, output[0]);
        Assert.Equal(0.16f, output[48_000]);
        Assert.Empty(synth.Messages);

        engine.SetPlaybackLoop(null);
        Render(engine, Origin + 24_128, 128);
        Assert.Null(engine.PlaybackTick);
    }

    [Fact]
    public void StartsOffBeatWithoutRenderingAnEarlierClicksTail()
    {
        var engine = Create();
        engine.MetronomeEnabled = true;
        engine.PlaySong(new Song(engine.TempoMap,
            [new SongTrack("Piano", [new(0, 4, 0, 60, 95)])]), SilentMonitorSynth.Instance, new LoopRange(481, 961));
        var output = Render(engine, Origin, 24_010);

        Assert.Equal(0, output[0]);
        Assert.Equal(0.16f, output[2 * 23_950]);
        Assert.Equal(0, output[48_000]);
    }

    [Fact]
    public void DoesNotChaseANoteThatEndsExactlyAtLoopStart()
    {
        var engine = Create();
        var synth = new EventSynth();
        var song = new Song(engine.TempoMap,
            [new SongTrack("Piano", [new(0, 10, 0, 60, 95), new(10, 10, 0, 61, 90)])]);
        engine.PlaySong(song, synth, new LoopRange(10, 20));
        Render(engine, Origin, 560);

        Assert.DoesNotContain(synth.Messages, message => message.Status == 0x90 && message.Key == 60);
        Assert.Equal(2, synth.Messages.Count(message => message.Status == 0x90 && message.Key == 61));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AudioGapSkipsMissedDrumsAndResumesOnTheBeatGrid(bool crossesLoopBoundary)
    {
        var engine = Create();
        var synth = new EventSynth();
        var song = new Song(engine.TempoMap,
        [
            new SongTrack("Backing", [new(1, 10, 0, 60, 95),
                new(2, 10, SongTrackEditor.DrumChannel, 36, 95),
                new(8, 3, SongTrackEditor.DrumChannel, 42, 95)],
                [new(3, 0xc0, 8, 0)])
        ]);
        const int loopFrames = 24_000;
        engine.PlaySong(song, synth, new LoopRange(0, 480));
        int framesBeforeGap = crossesLoopBoundary ? loopFrames - 50 : 60;
        Render(engine, Origin, framesBeforeGap);
        synth.Messages.Clear();

        long resume = Origin + (crossesLoopBoundary ? loopFrames : 0) + 250;
        Render(engine, resume, 250);

        Assert.Contains(new ReceivedMessage(framesBeforeGap, 0xc0, 8, 0), synth.Messages);
        Assert.Contains(new ReceivedMessage(framesBeforeGap, 0x90, 60, 95), synth.Messages);
        Assert.DoesNotContain(synth.Messages, message => message.Status == 0x99 && message.Key == 36);
        Assert.Contains(new ReceivedMessage(framesBeforeGap + 150, 0x99, 42, 95), synth.Messages);
        Assert.Equal(10, engine.PlaybackTick);
    }

    [Fact]
    public void ChasesSetupWhenANonzeroLoopStartRoundsToFrameZero()
    {
        var engine = Create();
        var synth = new EventSynth();
        var song = new Song(new TempoMap(100, [new(0, 500)]),
            [new SongTrack("Piano", [new(1, 9, 0, 60, 95)], [new(0, 0xc0, 7, 0)])]);
        engine.PlaySong(song, synth, new LoopRange(1, 10));
        Render(engine, Origin, 1);

        Assert.Equal([new ReceivedMessage(0, 0xc0, 7, 0), new(0, 0x90, 60, 95)], synth.Messages);
    }

    [Fact]
    public void RejectsInvalidRangesAndIntervalsThatDoNotSpanAnAudioSample()
    {
        var engine = Create();
        var song = new Song(new TempoMap(1, [new(0, 1)]),
            [new SongTrack("Piano", [new(0, 100, 0, 60, 95)])]);
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.PlaySong(song, SilentMonitorSynth.Instance, new LoopRange(1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.PlaySong(song, SilentMonitorSynth.Instance, new LoopRange(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => engine.SetPlaybackLoop(new LoopRange(-1, 1)));
    }

    [Fact]
    public void LoopWrapsDoNotAllocateInTheWarmAudioCallback()
    {
        var engine = Create();
        engine.MetronomeEnabled = true;
        var song = new Song(engine.TempoMap,
            [new SongTrack("Piano", [new(0, 2, 0, 60, 95)], [new(0, 0xc0, 7, 0)])]);
        engine.PlaySong(song, new GateSynth(), new LoopRange(1, 4));
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
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static Song CreateTempoChangeSong() => new(
        new TempoMap(480, [new(0, 500_000), new(1_920, 1_000_000)]),
        [
            new SongTrack("Piano", [new(0, 1_800, 0, 60, 95), new(2_880, 140, 0, 99, 96)],
                [new(0, 0xc0, 7, 0), new(2_400, 0xc0, 14, 0)]),
            new SongTrack("Bass", [new(1_920, 480, 1, 36, 100), new(2_760, 360, 1, 43, 94)])
        ]);

    private static LatencyEngine Create() => new(SampleRate, SilentMonitorSynth.Instance, timestampFrequency: SampleRate);

    private static float[] Render(LatencyEngine engine, long firstFrame, int frames)
    {
        var output = new float[2 * frames];
        engine.Render(output, firstFrame, new AudioClockAnchor(firstFrame, firstFrame));
        return output;
    }

    private sealed class EventSynth : IMonitorSynth
    {
        private int _frames;
        public List<ReceivedMessage> Messages { get; } = [];
        public List<int> Resets { get; } = [];

        public void ProcessMidi(byte status, byte data1, byte data2) => Messages.Add(new(_frames, status, data1, data2));
        public void Reset() => Resets.Add(_frames);

        public void Render(Span<float> interleavedStereo)
        {
            _frames += interleavedStereo.Length / 2;
            interleavedStereo.Clear();
        }
    }

    private sealed class GateSynth : IMonitorSynth
    {
        private bool _active;
        public void ProcessMidi(byte status, byte data1, byte data2) => _active = status == 0x90;
        public void Reset() => _active = false;
        public void Render(Span<float> interleavedStereo) => interleavedStereo.Fill(_active ? 0.2f : 0);
    }

    private readonly record struct ReceivedMessage(int Frame, byte Status, byte Key, byte Velocity);
}
