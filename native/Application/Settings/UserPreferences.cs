using System.Text.Json;
using Signal.Native.Application.Appearance;
using Signal.Native.Engine;

namespace Signal.Native.Application.Settings;

public sealed record MidiInputPreference(string Name, string? InterfacePath);

public sealed class UserPreferences(string directory)
{
    private const string OffsetFile = "recording-offset.json";
    private const string MidiInputFile = "midi-input.json";
    private const string QuantizationFile = "recording-quantization.json";
    private const string ThemeFile = "theme.json";
    private const string OutputGainFile = "output-gain.json";
    private const string CompressorFile = "compressor.json";

    public static UserPreferences ForCurrentUser(string profile) => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), profile));

    public double? ReadOffset()
    {
        var value = Read<double?>(OffsetFile);
        return value is >= -500 and <= 500 ? value : null;
    }

    public MidiInputPreference? ReadMidiInput() =>
        Read<MidiInputPreference>(MidiInputFile) is { Name.Length: > 0 } device ? device : null;

    public int? ReadQuantizationDivision() => Read<int?>(QuantizationFile);
    public string? ReadTheme() => Read<string>(ThemeFile);

    public IReadOnlyList<ThemeDefinition> ReadCustomThemes()
    {
        try
        {
            var themes = new List<ThemeDefinition>();
            foreach (var path in Directory.EnumerateFiles(directory, "theme-custom-*.json"))
            {
                string file = Path.GetFileName(path);
                if (Read<ThemeDefinition>(file) is { } theme && theme.IsValid() && file == CustomThemeFile(theme.Id))
                    themes.Add(theme);
            }
            return themes.OrderBy(theme => theme.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void SaveCustomTheme(ThemeDefinition theme)
    {
        if (!theme.IsValid())
            throw new ArgumentException("The custom theme needs a name and a complete RGB palette.", nameof(theme));
        Write(CustomThemeFile(theme.Id), theme);
    }

    private static string CustomThemeFile(Guid id) => $"theme-custom-{id:D}.json";

    public int? ReadOutputGain()
    {
        var value = Read<int?>(OutputGainFile);
        return value is >= LatencyEngine.MinimumOutputGainDb and <= LatencyEngine.MaximumOutputGainDb ? value : null;
    }

    public bool ReadCompressorEnabled() => Read<bool>(CompressorFile);

    public void SaveOffset(double value) => Write(OffsetFile, value);
    public void SaveMidiInput(MidiInputPreference? value) => Write(MidiInputFile, value);
    public void SaveQuantizationDivision(int value) => Write(QuantizationFile, value);
    public void SaveTheme(string value) => Write(ThemeFile, value);
    public void SaveOutputGain(int value) => Write(OutputGainFile, value);
    public void SaveCompressorEnabled(bool value) => Write(CompressorFile, value);

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
