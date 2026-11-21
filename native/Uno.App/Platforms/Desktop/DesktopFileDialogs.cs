using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;

namespace Signal.Native.Windows.App;

internal static class DesktopFileDialogs
{
    // Uno's standard picker uses SettingsIdentifier for folder history. It does not
    // expose the WinAppSDK SuggestedFolder property for an arbitrary starting path.
    public static async Task<string?> OpenAsync(Window owner, string[] extensions, string history,
        string folder = "")
    {
        var picker = new FileOpenPicker { SettingsIdentifier = history };
        foreach (string extension in extensions)
            picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> SaveAsync(Window owner, string name, string description, string[] extensions,
        string history, string folder)
    {
        var picker = new FileSavePicker { SettingsIdentifier = history, SuggestedFileName = name };
        picker.FileTypeChoices.Add(description, extensions);
        return (await picker.PickSaveFileAsync())?.Path;
    }
}
