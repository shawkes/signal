using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class LiveMidiRoutingTests
{
    private const int Rate = 1000;
    private const long Start = 1000;
    private readonly MidiLog _monitor = new();
    private readonly LatencyEngine _engine;
    private readonly Song _song = new(TempoMap.Constant(120, 500), []);

    public LiveMidiRoutingTests() =>
        _engine = new(Rate, _monitor, timestampFrequency: Rate);

    [Fact]
    public void SelectedTrackRoutesAnyInputChannelDuringIdleAndPlaybackWithoutChangingTheBacking()
    {
        RouteAll(new(9, 2), Start);
        Send(0x90, 36, 90, Start + 1);
        Send(0x83, 36, 0, Start + 2);
        Render(Start, 10);
        Assert.Equal([(0x99, 36, 90), (0x89, 36, 0)], _monitor.Messages);

        var backing = new MidiLog();
        _engine.PlayNotes([new(0, 200, 0, 64, 95)], backing);
        RouteAll(new(2, 1), Start + 14);
        Send(0x90, 60, 90, Start + 15);
        Render(Start + 10, 50);
        Assert.Equal((0x92, 60, 90), _monitor.Messages[^1]);
        Assert.Equal((0x90, 64, 95), Assert.Single(backing.Messages));
    }

    [Fact]
    public void SelectionChangeFinishesHeldNotesOnTheirOriginalTrackEvenWhenBothTracksShareAChannel()
    {
        RouteAll(new(1, 1), Start);
        long take = _engine.RecordSong(_song, SilentMonitorSynth.Instance, false, 0, timestamp: Start);
        Send(0x90, 60, 90, Start + 10);
        RouteAll(new(1, 3), Start + 30);
        Send(0x80, 60, 0, Start + 40);
        Send(0x90, 64, 95, Start + 50);
        Render(Start, 60);

        Assert.True(_engine.TryReadNote(out var first));
        Assert.Equal((1, 1, 10L, 20L), ((int)first.Channel, first.DestinationTrack, first.StartTick, first.LengthTicks));
        var held = new HeldRecordingNote[1];
        Assert.Equal(1, _engine.CopyHeldRecordingNotes(take, held));
        Assert.Equal(3, held[0].DestinationTrack);
        Send(0x80, 64, 0, Start + 65);
        Render(Start + 60, 20);
        Assert.True(_engine.TryReadNote(out var second));
        Assert.Equal((1, 3, 50L, 15L), ((int)second.Channel, second.DestinationTrack, second.StartTick, second.LengthTicks));
        Assert.False(_engine.TryReadNote(out _));
    }

    [Fact]
    public void MultiRoutesMatchingChannelsAndPreservesTrackOwnedPrograms()
    {
        var destinations = new LiveMidiDestination?[16];
        destinations[1] = new(1, 2);
        destinations[9] = new(9, 3);
        _engine.ConfigureLiveMidi(destinations, timestamp: Start);
        _engine.RecordSong(_song, SilentMonitorSynth.Instance, false, 0, timestamp: Start);
        Send(0x90, 60, 90, Start + 10);
        Send(0x91, 64, 95, Start + 10);
        Send(0x99, 37, 80, Start + 10);
        Send(0xC1, 25, 0, Start + 20);
        Send(0xB1, 0, 3, Start + 20);
        Send(0xB1, 64, 127, Start + 20);
        Send(0x81, 64, 0, Start + 30);
        Send(0x89, 37, 0, Start + 35);
        Render(Start, 50);

        Assert.Equal([(0x91, 64, 95), (0x99, 37, 80), (0xB1, 64, 127), (0x81, 64, 0), (0x89, 37, 0)], _monitor.Messages);
        Assert.True(_engine.TryReadNote(out var instrument));
        Assert.True(_engine.TryReadNote(out var drums));
        Assert.Equal((1, 2, 20L), ((int)instrument.Channel, instrument.DestinationTrack, instrument.LengthTicks));
        Assert.Equal((9, 3, 25L), ((int)drums.Channel, drums.DestinationTrack, drums.LengthTicks));
        Assert.False(_engine.TryReadNote(out _));
    }

    private void RouteAll(LiveMidiDestination destination, long frame)
    {
        var destinations = new LiveMidiDestination?[16];
        Array.Fill<LiveMidiDestination?>(destinations, destination);
        _engine.ConfigureLiveMidi(destinations, timestamp: frame);
    }

    private void Send(byte status, byte data1, byte data2, long frame) =>
        Assert.True(_engine.TryEnqueueLiveMidi(new MidiMessage(frame, status, data1, data2)));

    private void Render(long frame, int length) =>
        _engine.Render(new float[length * 2], frame, new AudioClockAnchor(frame, frame));

    private sealed class MidiLog : IMonitorSynth
    {
        public List<(int Status, int Data1, int Data2)> Messages { get; } = [];
        public void Render(Span<float> stereo) => stereo.Clear();

        public void ProcessMidi(byte status, byte data1, byte data2)
        {
            if ((status & 0xF0) != 0xB0 || data1 is not (120 or 121))
                Messages.Add((status, data1, data2));
        }
    }
}
