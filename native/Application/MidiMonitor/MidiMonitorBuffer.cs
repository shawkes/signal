using System.Threading.Channels;
using Signal.Native.Engine;

namespace Signal.Native.Application.MidiMonitor;

// The device callback submits value types. The window drains and formats a batch on its own timer.
public sealed class MidiMonitorBuffer
{
    public const int Capacity = 512;
    public const byte ActiveSensingStatus = 0xFE;
    private readonly Channel<MidiMessage> _messages;
    private long _skipped;
    private volatile bool _paused;
    private volatile bool _showActiveSensing;

    public MidiMonitorBuffer() => _messages = Channel.CreateBounded<MidiMessage>(new BoundedChannelOptions(Capacity)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest
    }, _ => Interlocked.Increment(ref _skipped));

    public long Skipped => Interlocked.Read(ref _skipped);

    public bool Paused
    {
        set => _paused = value;
    }

    public bool ShowActiveSensing
    {
        get => _showActiveSensing;
        set => _showActiveSensing = value;
    }

    public void Capture(MidiMessage message)
    {
        if (!_paused && (message.Status != ActiveSensingStatus || _showActiveSensing))
            _messages.Writer.TryWrite(message);
    }

    public bool TryRead(out MidiMessage message) => _messages.Reader.TryRead(out message);
}
