using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Signal.Native.Application.Appearance;
using static Signal.Native.Uno.App.Appearance.ThemeBrushes;

namespace Signal.Native.Uno.App.Appearance;

internal static class ThemePalette
{
    public static EditorTheme ToEditorTheme(ThemeDefinition definition)
    {
        SolidColorBrush Color(ThemeColor role) => Rgb(definition.Colors[role]);

        return new(definition.Id.ToString("D"), definition.Name,
            new(Color(ThemeColor.WindowBackground), Color(ThemeColor.Header), Color(ThemeColor.Accent),
                Color(ThemeColor.AccentText), definition.IsLight ? ElementTheme.Light : ElementTheme.Dark),
            new(Color(ThemeColor.NaturalLane), Color(ThemeColor.AccidentalLane), Color(ThemeColor.MinorLine),
                Color(ThemeColor.BeatLine), Color(ThemeColor.BarLine), Color(ThemeColor.Ruler), Color(ThemeColor.RulerText),
                Color(ThemeColor.RulerMuted), Color(ThemeColor.NaturalKey), Color(ThemeColor.AccidentalKey),
                Color(ThemeColor.NaturalKeyText), Color(ThemeColor.AccidentalKeyText), Color(ThemeColor.Note),
                Color(ThemeColor.NoteOutline), Color(ThemeColor.SelectedNote), Color(ThemeColor.SelectedOutline),
                Color(ThemeColor.NoteText), Color(ThemeColor.NoteHandle), Color(ThemeColor.Playhead), Color(ThemeColor.Loop)));
    }

    public static ThemeDefinition CreateDraft(EditorTheme theme, bool systemLight, Guid id, string name)
    {
        bool light = theme.Window is { } window ? window.Mode == ElementTheme.Light : systemLight;
        var chrome = theme.Window ?? (light
            ? new WindowPalette(Rgb(0xf7f8fc), Rgb(0xe4edf6), Rgb(0x0c65b4), Rgb(0xffffff), ElementTheme.Light)
            : new WindowPalette(Rgb(0x131d29), Rgb(0x222f3e), Rgb(0x76b9ff), Rgb(0x172636)));
        var roll = theme.PianoRoll ?? (light ? PianoRollPalette.Light : PianoRollPalette.Dark);
        return new(id, name, light, new()
        {
            [ThemeColor.WindowBackground] = Value(chrome.Background),
            [ThemeColor.Header] = Value(chrome.Header),
            [ThemeColor.Accent] = Value(chrome.Accent),
            [ThemeColor.AccentText] = Value(chrome.AccentText),
            [ThemeColor.NaturalLane] = Value(roll.NaturalLane),
            [ThemeColor.AccidentalLane] = Value(roll.AccidentalLane),
            [ThemeColor.MinorLine] = Value(roll.MinorLine),
            [ThemeColor.BeatLine] = Value(roll.BeatLine),
            [ThemeColor.BarLine] = Value(roll.MajorLine),
            [ThemeColor.Ruler] = Value(roll.Ruler),
            [ThemeColor.RulerText] = Value(roll.RulerText),
            [ThemeColor.RulerMuted] = Value(roll.RulerMuted),
            [ThemeColor.NaturalKey] = Value(roll.NaturalKey),
            [ThemeColor.AccidentalKey] = Value(roll.AccidentalKey),
            [ThemeColor.NaturalKeyText] = Value(roll.NaturalKeyText),
            [ThemeColor.AccidentalKeyText] = Value(roll.AccidentalKeyText),
            [ThemeColor.Note] = Value(roll.Note),
            [ThemeColor.NoteOutline] = Value(roll.NoteOutline),
            [ThemeColor.SelectedNote] = Value(roll.SelectedNote),
            [ThemeColor.SelectedOutline] = Value(roll.SelectedOutline),
            [ThemeColor.NoteText] = Value(roll.NoteText),
            [ThemeColor.NoteHandle] = Value(roll.Handle),
            [ThemeColor.Playhead] = Value(roll.Playhead),
            [ThemeColor.Loop] = Value(roll.Loop)
        });
    }

    private static uint Value(Brush brush) => Value(((SolidColorBrush)brush).Color);

    internal static uint Value(global::Windows.UI.Color color) =>
        ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
}
