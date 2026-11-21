using System.Text.Json;
using Signal.Native.Windows.Midi;

namespace Signal.Native.Windows.App.Settings;

internal sealed record MidiInputPreference(string Name, string? InterfacePath)
{
    public MidiInputDevice? Find(IReadOnlyList<MidiInputDevice> devices)
    {
        var matches = InterfacePath is null
            ? devices.Where(device => Same(device.Name, Name))
            : devices.Where(device => Same(device.InterfacePath, InterfacePath));
        var candidates = matches.ToArray();
        if (candidates.Length == 1)
            return candidates[0];

        // Some multi-port drivers share a path across several named ports.
        var named = candidates.Where(device => Same(device.Name, Name)).Take(2).ToArray();
        return named.Length == 1 ? named[0] : null;
    }

    private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

internal sealed class RecordingPreferences(string directory)
{
    private const string OffsetFile = "recording-offset.json";
    private const string MidiInputFile = "midi-input.json";
    private const string QuantizationFile = "recording-quantization.json";

    public static RecordingPreferences ForCurrentUser() => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Signal Native"));

    public double? ReadOffset()
    {
        var value = Read<double?>(OffsetFile);
        return value is >= -500 and <= 500 ? value : null;
    }

    public MidiInputPreference? ReadMidiInput() =>
        Read<MidiInputPreference>(MidiInputFile) is { Name.Length: > 0 } device ? device : null;

    public int? ReadQuantizationDivision() => Read<int?>(QuantizationFile);

    public void SaveOffset(double value) => Write(OffsetFile, value);
    public void SaveMidiInput(MidiInputPreference? value) => Write(MidiInputFile, value);
    public void SaveQuantizationDivision(int value) => Write(QuantizationFile, value);

    private T? Read<T>(string name)
    {
        try
        {
            using var input = new FileStream(Path.Combine(directory, name), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<T>(input);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return default;
        }
    }

    // Independent files let running windows change different preferences without overwriting one another.
    private void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, value);
            File.Move(temporary, Path.Combine(directory, name), overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
