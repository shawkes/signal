using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Signal.Native.Engine;

namespace Signal.Native.Windows.Midi;

public sealed record MidiInputDevice(uint Id, string Name, string? InterfacePath = null);

public sealed class WinMmInput : IDisposable
{
    private const uint CallbackFunction = 0x30000;
    private const uint DataMessage = 0x3C3;
    private const uint QueryDeviceInterface = 0x080C;
    private const uint QueryDeviceInterfaceSize = 0x080D;
    private readonly Action<MidiMessage> _receive;
    private readonly NativeCallback _callback;
    private nint _handle;

    public static IReadOnlyList<MidiInputDevice> EnumerateDevices()
    {
        var devices = new List<MidiInputDevice>();
        for (uint id = 0, count = midiInGetNumDevs(); id < count; id++)
        {
            if (midiInGetDevCaps(id, out var caps, (uint)Marshal.SizeOf<InputCaps>()) == 0)
                devices.Add(new MidiInputDevice(id, caps.Name, GetInterfacePath(id)));
        }
        return devices;
    }

    // WinMM device numbers can change after a restart or when another controller is plugged in.
    private static string? GetInterfacePath(uint id)
    {
        if (GetInterfaceSize(id, QueryDeviceInterfaceSize, out uint bytes, 0) != 0 ||
            bytes < sizeof(char) || bytes > 65536)
            return null;

        var path = new StringBuilder((int)bytes / sizeof(char));
        return GetInterfaceName(id, QueryDeviceInterface, path, bytes) == 0 && path.Length > 0 ? path.ToString() : null;
    }

    public WinMmInput(uint id, Action<MidiMessage> receive)
    {
        _receive = receive;
        _callback = OnMidi;
        Check(midiInOpen(out _handle, id, _callback, 0, CallbackFunction), "open");
        uint result = midiInStart(_handle);
        if (result != 0)
        {
            Dispose();
            Check(result, "start");
        }
    }

    private void OnMidi(nint handle, uint message, nuint instance, nuint data, nuint driverTime)
    {
        if (message != DataMessage)
            return;

        long received = Stopwatch.GetTimestamp();
        byte status = (byte)data;
        // System-common and real-time short messages are useful to the MIDI monitor.
        if (status >= 0x80)
            _receive(new MidiMessage(received, status, (byte)((data >> 8) & 0x7F), (byte)((data >> 16) & 0x7F)));
    }

    public void Dispose()
    {
        if (_handle == 0)
            return;

        midiInStop(_handle);
        midiInReset(_handle);
        midiInClose(_handle);
        _handle = 0;
        GC.KeepAlive(_callback);
    }

    private static void Check(uint result, string operation)
    {
        if (result != 0)
            throw new Win32Exception($"Could not {operation} the MIDI input (WinMM result {result}).");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct InputCaps
    {
        public ushort Manufacturer;
        public ushort Product;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Name;
        public uint Support;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void NativeCallback(nint handle, uint message, nuint instance, nuint data, nuint timestamp);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "midiInMessage", ExactSpelling = true)]
    private static extern uint GetInterfaceSize(nuint deviceId, uint message, out uint byteCount, nuint unused);

    [DllImport("winmm.dll", EntryPoint = "midiInMessage", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint GetInterfaceName(nuint deviceId, uint message, [Out] StringBuilder name, nuint byteCount);

    [DllImport("winmm.dll", EntryPoint = "midiInGetDevCapsW", ExactSpelling = true)]
    private static extern uint midiInGetDevCaps(nuint device, out InputCaps caps, uint size);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInOpen(out nint handle, uint device, NativeCallback callback, nuint instance, uint flags);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInStart(nint handle);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInStop(nint handle);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInReset(nint handle);

    [DllImport("winmm.dll", ExactSpelling = true)]
    private static extern uint midiInClose(nint handle);
}
