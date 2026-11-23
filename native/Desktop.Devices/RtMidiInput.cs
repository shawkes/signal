using System.Diagnostics;
using RtMidi.Net;
using EngineMidiMessage = Signal.Native.Engine.MidiMessage;

namespace Signal.Native.Desktop.Devices;

public sealed record MidiInputDevice(uint Id, string Name, string? InterfacePath = null);

public sealed class RtMidiInput : IDisposable
{
    private readonly MidiInputClient _input;
    private readonly Action<EngineMidiMessage> _receive;
    private static MidiApi PlatformApi => OperatingSystem.IsWindows() ? MidiApi.WindowsMultimediaMidi :
        OperatingSystem.IsMacOS() ? MidiApi.MacOsxCore : MidiApi.LinuxAlsa;

    public RtMidiInput(uint port, Action<EngineMidiMessage> receive)
    {
        _receive = receive;
        try
        {
            _input = new MidiInputClient(MidiManager.GetDeviceInfo(port, MidiDeviceType.Input, PlatformApi))
            {
                IgnoreTimeMessages = false,
                IgnoreSenseMessages = false
            };
            try
            {
                _input.MessageReceived += OnMessage;
                _input.Open();
            }
            catch
            {
                _input.Dispose();
                throw;
            }
        }
        catch (RtMidiException error)
        {
            throw new InvalidOperationException($"Could not open MIDI input. {error.Message}", error);
        }
    }

    public static IReadOnlyList<MidiInputDevice> EnumerateDevices()
    {
        try
        {
            return MidiManager.GetAvailableDevices(PlatformApi).Where(device => device.Type == MidiDeviceType.Input)
                .Select(device => new MidiInputDevice(device.Port, device.Name)).ToArray();
        }
        catch (RtMidiException error)
        {
            throw new InvalidOperationException(error.Message, error);
        }
    }

    public static MidiInputDevice? FindPreferredDevice(IReadOnlyList<MidiInputDevice> devices, string name,
        string? interfacePath)
    {
        var candidates = devices.Where(device => interfacePath is null
            ? string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase)
            : string.Equals(device.InterfacePath, interfacePath, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 1)
            return candidates[0];

        // Some multi-port drivers share a path across several named ports.
        var named = candidates.Where(device => string.Equals(device.Name, name, StringComparison.OrdinalIgnoreCase))
            .Take(2).ToArray();
        return named.Length == 1 ? named[0] : null;
    }

    private void OnMessage(object? sender, MidiMessageReceivedEventArgs args)
    {
        long received = Stopwatch.GetTimestamp();
        byte status = (byte)args.Message.Type;
        (byte Status, byte Data1, byte Data2) data = args.Message switch
        {
            MidiMessageNote message => ((byte)(status | (byte)message.Channel), message.Note.Number, message.Velocity),
            MidiMessageNoteAfterTouch message => ((byte)(status | (byte)message.Channel), message.Note.Number, message.Pressure),
            MidiMessageControlChange message => ((byte)(status | (byte)message.Channel), message.ControlNumber, message.Value),
            MidiMessageProgramChange message => ((byte)(status | (byte)message.Channel), message.Program, 0),
            MidiMessageChannelAfterTouch message => ((byte)(status | (byte)message.Channel), message.Pressure, 0),
            MidiMessagePitchBendChange message => ((byte)(status | (byte)message.Channel), message.Lsb, message.Msb),
            MidiMessageTimeCodeQuarterFrame message => (status, (byte)((message.Piece << 4) | message.Value), 0),
            MidiMessageSongPositionPointer message => (status, message.Lsb, message.Msb),
            MidiMessageSongSelect message => (status, message.SongNumber, 0),
            _ when status >= 0xf6 => (status, 0, 0),
            _ => default
        };
        if (data.Status >= 0x80)
            _receive(new(received, data.Status, data.Data1, data.Data2));
    }

    public void Dispose()
    {
        _input.MessageReceived -= OnMessage;
        _input.Dispose();
    }
}
