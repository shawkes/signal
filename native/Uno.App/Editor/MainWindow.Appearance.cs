using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.Application.Appearance;
using Signal.Native.Uno.App.Appearance;

namespace Signal.Native.Uno.App;

public sealed partial class MainWindow
{
    private WindowPalette? _windowPalette;
    private EditorTheme _activeTheme = EditorTheme.System;
    private IReadOnlyList<ThemeDefinition> _customThemes = [];

    private void InitializeThemeMenu()
    {
        RebuildThemeMenu();
        string? selection = _preferences.ReadTheme();
        _activeTheme = Guid.TryParse(selection, out Guid id) && _customThemes.FirstOrDefault(theme => theme.Id == id) is { } custom
            ? ThemePalette.ToEditorTheme(custom)
            : EditorTheme.Find(selection);
        ApplyTheme(_activeTheme);
    }

    private void RebuildThemeMenu()
    {
        _customThemes = _preferences.ReadCustomThemes();
        ThemeMenu.Items.Clear();
        var edit = new MenuFlyoutItem { Text = "Edit theme…" };
        edit.Click += async (_, _) => await ShowThemeEditorAsync(import: false);
        ThemeMenu.Items.Add(edit);
        var import = new MenuFlyoutItem { Text = "Import Vim scheme…" };
        import.Click += async (_, _) => await ShowThemeEditorAsync(import: true);
        ThemeMenu.Items.Add(import);
        ThemeMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var theme in EditorTheme.Choices)
            AddTheme(theme);
        if (_customThemes.Count > 0)
            ThemeMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var theme in _customThemes)
            AddTheme(ThemePalette.ToEditorTheme(theme));

        void AddTheme(EditorTheme theme)
        {
            var item = new RadioMenuFlyoutItem { Text = theme.Label, Tag = theme, GroupName = "Theme" };
            item.Click += ThemeClicked;
            ThemeMenu.Items.Add(item);
        }
    }

    private void ThemeClicked(object sender, RoutedEventArgs args)
    {
        if (!_fileBusy && sender is RadioMenuFlyoutItem { Tag: EditorTheme theme })
            SelectTheme(theme);
    }

    private void SelectTheme(EditorTheme theme)
    {
        _activeTheme = theme;
        ApplyTheme(theme);
        try
        {
            _preferences.SaveTheme(theme.Id);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowFileError("Could not save the theme for the next launch", error);
        }
    }

    private async Task ShowThemeEditorAsync(bool import)
    {
        if (_fileBusy || _closed || CaptureInProgress || AudioMidiSettingsOpen)
            return;

        SetFileBusy(true);
        try
        {
            ThemeDefinition draft;
            if (import)
            {
                string? path = await DesktopFileDialogs.OpenAsync([".vim"], "SignalImportVimTheme");
                if (path is null)
                    return;
                var scheme = VimColorScheme.Parse(await File.ReadAllTextAsync(path), Path.GetFileNameWithoutExtension(path));
                draft = VimThemePalette.Create(scheme, Guid.NewGuid(), UniqueThemeName(scheme.Name));
            }
            else
            {
                bool custom = Guid.TryParse(_activeTheme.Id, out Guid id);
                string name = custom ? _activeTheme.Label :
                    UniqueThemeName(_activeTheme == EditorTheme.System ? "Custom theme" : $"{_activeTheme.Label} copy");
                draft = ThemePalette.CreateDraft(_activeTheme, WorkspaceRoot.ActualTheme == ElementTheme.Light,
                    custom ? id : Guid.NewGuid(), name);
            }

            var view = new ThemeEditorView(draft);
            var dialog = new ContentDialog
            {
                XamlRoot = WorkspaceRoot.XamlRoot,
                Title = "Theme editor",
                PrimaryButtonText = "Save theme",
                CloseButtonText = "Cancel",
                Content = new ScrollViewer
                {
                    Width = Math.Clamp(WorkspaceRoot.ActualWidth - 96, 280, 560),
                    MaxHeight = Math.Max(180, WorkspaceRoot.ActualHeight - 200),
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollMode = ScrollMode.Auto,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = view
                }
            };
            dialog.Resources["ContentDialogMaxWidth"] = 680d;
            void Preview(ThemeDefinition change)
            {
                var theme = ThemePalette.ToEditorTheme(change);
                ApplyTheme(theme);
                dialog.RequestedTheme = WorkspaceRoot.RequestedTheme;
                dialog.Background = theme.Window?.Header;
            }

            ThemeDefinition? saved = null;
            dialog.PrimaryButtonClick += (_, click) =>
            {
                var change = view.ReadDraft();
                string? validation = !change.IsValid() ? "Enter a theme name." :
                    ThemeNameInUse(change.Name, change.Id) ? "A theme with this name already exists." : null;
                if (validation is not null)
                {
                    view.ShowError(validation);
                    click.Cancel = true;
                    return;
                }
                try
                {
                    _preferences.SaveCustomTheme(change);
                    saved = change;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    view.ShowError($"Could not save the theme. {error.Message}");
                    click.Cancel = true;
                }
            };
            view.PreviewChanged += Preview;
            Preview(draft);
            if (await dialog.ShowAsync() == ContentDialogResult.Primary && saved is { } theme)
            {
                RebuildThemeMenu();
                SelectTheme(ThemePalette.ToEditorTheme(theme));
            }
        }
        catch (Exception error)
        {
            ShowFileError(import ? "Could not import the Vim scheme" : "Could not open the theme editor", error);
        }
        finally
        {
            if (!_closed)
            {
                ApplyTheme(_activeTheme);
                SetFileBusy(false);
            }
        }
    }

    private bool ThemeNameInUse(string name, Guid id) =>
        EditorTheme.Choices.Any(theme => string.Equals(theme.Label, name, StringComparison.OrdinalIgnoreCase)) ||
        _customThemes.Any(theme => theme.Id != id && string.Equals(theme.Name, name, StringComparison.OrdinalIgnoreCase));

    private string UniqueThemeName(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "Custom theme" : name.Trim();
        string candidate = name[..Math.Min(name.Length, ThemeDefinition.MaxNameLength)];
        for (int copy = 2; ThemeNameInUse(candidate, Guid.Empty); copy++)
        {
            string suffix = $" ({copy})";
            candidate = name[..Math.Min(name.Length, ThemeDefinition.MaxNameLength - suffix.Length)] + suffix;
        }
        return candidate;
    }

    private void ApplyTheme(EditorTheme theme)
    {
        _windowPalette = theme.Window;

        foreach (var item in ThemeMenu.Items.OfType<RadioMenuFlyoutItem>())
            item.IsChecked = item.Tag is EditorTheme choice && choice.Id == theme.Id;
        ApplyWindowTheme(WorkspaceRoot);
        MainMenu.Background = _windowPalette?.Header;
        RollView.SetPalette(theme.PianoRoll);
        ApplyMidiMonitorTheme();

        _audioMidiSettingsDialog.RequestedTheme = WorkspaceRoot.RequestedTheme;
        if (_windowPalette is { } palette)
            _audioMidiSettingsDialog.Background = palette.Header;
        else
            _audioMidiSettingsDialog.ClearValue(Control.BackgroundProperty);

        foreach (var button in new[] { PlayButton, RecordButton })
        {
            if (_windowPalette is { } chrome)
            {
                button.Background = chrome.Accent;
                button.Foreground = chrome.AccentText;
            }
            else
            {
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.ForegroundProperty);
            }
        }
    }

    private void ApplyMidiMonitorTheme()
    {
        if (_midiMonitor?.Content is Panel root)
            ApplyWindowTheme(root);
    }

    private void ApplyWindowTheme(Panel root)
    {
        root.RequestedTheme = _windowPalette?.Mode ?? ElementTheme.Default;
        root.Background = _windowPalette?.Background;
    }
}
