using Windows.Storage.Pickers;

namespace Signal.Native.Uno.App;

internal static class DesktopFileDialogs
{
    public static async Task<string?> OpenAsync(string[] extensions, string history)
    {
        var picker = new FileOpenPicker { SettingsIdentifier = history };
        foreach (string extension in extensions)
            picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> SaveAsync(string name, string description, string[] extensions, string history)
    {
        var picker = new FileSavePicker { SettingsIdentifier = history, SuggestedFileName = name };
        picker.FileTypeChoices.Add(description, extensions);
        return (await picker.PickSaveFileAsync())?.Path;
    }
}
