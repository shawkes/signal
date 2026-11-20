using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Signal.Native.Windows.Audio;

internal static partial class WasapiInterop
{
    private static readonly StrategyBasedComWrappers Wrappers = new();
    private static readonly Guid EnumeratorClass = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private const int RpcChangedMode = unchecked((int)0x80010106);

    internal const int Render = 0;
    internal const int Multimedia = 1;
    internal const uint Active = 1;
    internal const uint InProcess = 1;
    internal const int Shared = 0;
    internal const uint EventCallback = 0x00040000;
    internal const uint Silent = 0x00000002;
    internal const int EnginePeriodLocked = unchecked((int)0x88890028);

    internal static bool EnterApartment()
    {
        int result = CoInitializeEx(0, 0); // MTA, unless the caller already uses an STA.
        if (result == RpcChangedMode)
            return false;
        Check(result);
        return true;
    }

    internal static IMMDeviceEnumerator CreateEnumerator()
    {
        Guid iid = typeof(IMMDeviceEnumerator).GUID;
        Check(CoCreateInstance(in EnumeratorClass, 0, InProcess, in iid, out nint pointer));
        return Adopt<IMMDeviceEnumerator>(pointer);
    }

    internal static T Adopt<T>(nint pointer) where T : class
    {
        try
        {
            var wrapper = (ComObject)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);
            if (wrapper is T result)
                return result;
            wrapper.FinalRelease();
            throw new InvalidCastException($"The audio endpoint did not provide {typeof(T).Name}.");
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    internal static void Release(object? value) => (value as ComObject)?.FinalRelease();

    internal static void Check(int result)
    {
        if (result < 0)
            Marshal.ThrowExceptionForHR(result, new nint(-1));
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid classId, nint outer, uint context, in Guid interfaceId, out nint instance);

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint mode);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    internal static partial int PropVariantClear(ref PropertyVariant result);

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint AvSetMmThreadCharacteristics(string name, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AvRevertMmThreadCharacteristics(nint handle);
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormat
{
    internal ushort Tag;
    internal ushort Channels;
    internal uint SamplesPerSecond;
    internal uint BytesPerSecond;
    internal ushort BlockAlign;
    internal ushort BitsPerSample;
    internal ushort ExtraSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatExtensible
{
    internal WaveFormat Format;
    internal ushort ValidBits;
    internal uint ChannelMask;
    internal Guid SubFormat;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PropertyKey(Guid formatId, uint id)
{
    internal readonly Guid FormatId = formatId;
    internal readonly uint Id = id;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyVariant
{
    internal ushort Type;
    private ushort _reserved1;
    private ushort _reserved2;
    private ushort _reserved3;
    internal nint Pointer;
    private nint _unionPadding;
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out nint device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out nint device);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal partial interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int Item(uint index, out nint device);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig] int Activate(in Guid iid, uint context, nint activationParameters, out nint instance);
    [PreserveSig] int OpenPropertyStore(uint access, out nint store);
    [PreserveSig] int GetId(out nint id);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
internal partial interface IPropertyStore
{
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetAt(uint index, out PropertyKey key);
    [PreserveSig] int GetValue(in PropertyKey key, out PropertyVariant value);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
internal partial interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, uint flags, long bufferDuration, long periodicity, nint format, nint sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint frames);
    [PreserveSig] int IsFormatSupported(int shareMode, nint format, out nint closestMatch);
    [PreserveSig] int GetMixFormat(out nint format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(nint handle);
    [PreserveSig] int GetService(in Guid iid, out nint instance);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("726778CD-F60A-4EDA-82DE-E47610CD78AA")]
internal partial interface IAudioClient2 : IAudioClient
{
    [PreserveSig] int IsOffloadCapable(int category, out int offloadCapable);
    [PreserveSig] int SetClientProperties(nint properties);
    [PreserveSig] int GetBufferSizeLimits(nint format, int eventDriven, out long minimum, out long maximum);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("7ED4EE07-8E67-4CD4-8C1A-2B7A5987AD42")]
internal partial interface IAudioClient3 : IAudioClient2
{
    [PreserveSig] int GetSharedModeEnginePeriod(nint format, out uint defaultFrames, out uint fundamentalFrames, out uint minimumFrames, out uint maximumFrames);
    [PreserveSig] int GetCurrentSharedModeEnginePeriod(out nint format, out uint periodFrames);
    [PreserveSig] int InitializeSharedAudioStream(uint flags, uint periodFrames, nint format, nint sessionGuid);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
internal partial interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out nint data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}

[GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper)]
[Guid("CD63314F-3FBA-4A1B-812C-EF96358728E7")]
internal partial interface IAudioClock
{
    [PreserveSig] int GetFrequency(out ulong frequency);
    [PreserveSig] int GetPosition(out ulong position, out ulong qpcPositionIn100Ns);
}
