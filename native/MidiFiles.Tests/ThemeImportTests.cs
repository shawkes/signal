using Signal.Native.Application.Appearance;
using Signal.Native.Application.Settings;
using Xunit;

namespace Signal.Native.MidiFiles.Tests;

public sealed class ThemeImportTests
{
    [Fact]
    public void ReadsLiteralColorsAndContinuationsWithGuiColorsTakingPrecedence()
    {
        var scheme = VimColorScheme.Parse("""
            set bg=light
            let g:colors_name = 'Sample'
            hi clear
            hi Normal ctermbg=16 guibg=#eeddcc
                  \ guifg=#214365
            hi Normal ctermfg=9
            :highlight LineNr guifg='Dark Slate Gray' " guifg=Red
            """, "filename");

        Assert.Equal("Sample", scheme.Name);
        Assert.True(scheme.IsLight);
        Assert.Equal(0xeeddccu, scheme.Background("Normal"));
        Assert.Equal(0x214365u, scheme.Foreground("Normal"));
        Assert.Equal(0x2f4f4fu, scheme.Foreground("LineNr"));
    }

    [Fact]
    public void ReadsTerminalColorsResolvesLinksAndSkipsScripts()
    {
        var scheme = VimColorScheme.Parse("""
            " hi Normal ctermbg=1
            hi Normal ctermbg=233 ctermfg=223
            hi Constant ctermfg=81
            hi default link String Constant
            hi link First Second
            hi link Second First
            hi Visual cterm=reverse
            hi Cursor guibg=fg
            hi Title guifg=#aabbcc
            hi! link Title String
            execute 'hi ErrorMsg guifg=#ffffff'
            """, "filename");

        Assert.Equal("filename", scheme.Name);
        Assert.Equal(0x121212u, scheme.Background("Normal"));
        Assert.Equal(0x5fd7ffu, scheme.Foreground("String"));
        Assert.Equal(0x5fd7ffu, scheme.Foreground("Title"));
        Assert.Equal(0xffd7afu, scheme.Background("Visual"));
        Assert.Equal(0xffd7afu, scheme.Background("Cursor"));
        Assert.Null(scheme.Foreground("First"));
        Assert.Null(scheme.Foreground("ErrorMsg"));
    }

    [Fact]
    public void CustomThemeRoundTripsAlongsideExistingSelection()
    {
        var directory = Directory.CreateTempSubdirectory("signal-theme-");
        try
        {
            var preferences = new UserPreferences(directory.FullName);
            var colors = Enum.GetValues<ThemeColor>().ToDictionary(role => role, role => 0xff0000u + (uint)role);
            var theme = new ThemeDefinition(Guid.NewGuid(), "Custom", true, colors);
            preferences.SaveTheme("Elflord");
            preferences.SaveCustomTheme(theme);

            var reopened = new UserPreferences(directory.FullName);
            Assert.Equal("Elflord", reopened.ReadTheme());
            var saved = Assert.Single(reopened.ReadCustomThemes());
            Assert.Equal(theme.Id, saved.Id);
            Assert.Equal(theme.Name, saved.Name);
            Assert.True(saved.IsLight);
            Assert.Equal(colors, saved.Colors);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
