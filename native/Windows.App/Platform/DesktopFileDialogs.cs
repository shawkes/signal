using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;

namespace Signal.Native.Windows.App;

internal static class DesktopFileDialogs
{
    public static async Task<string?> OpenAsync(Window owner, string[] extensions, string history,
        string folder = "")
    {
        var picker = new FileOpenPicker(owner.AppWindow.Id)
        {
            SettingsIdentifier = history,
            SuggestedFolder = folder
        };
        foreach (string extension in extensions)
            picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    public static async Task<string?> SaveAsync(Window owner, string name, string description, string[] extensions,
        string history, string folder)
    {
        var picker = new FileSavePicker(owner.AppWindow.Id)
        {
            SuggestedFileName = name,
            DefaultFileExtension = extensions[0],
            SettingsIdentifier = history,
            SuggestedFolder = folder,
            ShowOverwritePrompt = true
        };
        picker.FileTypeChoices.Add(description, extensions);
        return (await picker.PickSaveFileAsync())?.Path;
    }
}
