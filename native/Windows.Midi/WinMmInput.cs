using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Signal.Native.Engine;

namespace Signal.Native.Windows.Midi;

public sealed record MidiInputDevice(uint Id, string Name);

public sealed class WinMmInput : IDisposable
{
    private const uint CallbackFunction = 0x30000;
    private const uint DataMessage = 0x3C3;
    private readonly Action<MidiMessage> _receive;
    private readonly NativeCallback _callback;
    private nint _handle;

    public static IReadOnlyList<MidiInputDevice> EnumerateDevices()
    {
        var devices = new List<MidiInputDevice>();
        for (uint id = 0, count = midiInGetNumDevs(); id < count; id++)
        {
            if (midiInGetDevCaps(id, out var caps, (uint)Marshal.SizeOf<InputCaps>()) == 0)
                devices.Add(new MidiInputDevice(id, caps.Name));
        }
        return devices;
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
        if (status is >= 0x80 and < 0xF0)
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
