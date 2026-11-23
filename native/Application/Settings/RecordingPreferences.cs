using System.Text.Json;

namespace Signal.Native.Application.Settings;

public sealed record MidiInputPreference(string Name, string? InterfacePath);

public sealed class RecordingPreferences(string directory)
{
    private const string OffsetFile = "recording-offset.json";
    private const string MidiInputFile = "midi-input.json";
    private const string QuantizationFile = "recording-quantization.json";

    public static RecordingPreferences ForCurrentUser(string profile) => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), profile));

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
