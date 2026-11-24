using Signal.Native.Application.Appearance;

namespace Signal.Native.Uno.App.Appearance;

internal static class VimThemePalette
{
    public static ThemeDefinition Create(VimColorScheme scheme, Guid id, string name)
    {
        uint? normalBg = scheme.Background("Normal");
        uint? normalFg = scheme.Foreground("Normal");
        uint? menuBg = First(scheme.Background, "Pmenu", "StatusLineNC", "CursorLine");
        uint? comment = First(scheme.Foreground, "LineNr", "Comment", "NonText");
        uint? stringColor = First(scheme.Foreground, "String", "Function", "Identifier", "Constant", "Type");
        uint? accentColor = First(scheme.Foreground, "Title", "Keyword", "Statement", "Special");
        uint? visual = First(scheme.Background, "Visual", "PmenuSel", "DiffChange");
        uint? cursor = First(scheme.Background, "Cursor", "Search", "IncSearch");
        if ((normalBg ?? normalFg ?? menuBg ?? comment ?? stringColor ?? accentColor ?? visual ?? cursor) is null)
            throw new InvalidDataException("This scheme has no usable literal highlight colors. Choose a .vim file with guifg, guibg, ctermfg, or ctermbg definitions.");

        bool light = normalBg is uint background ? IsLight(background) : scheme.IsLight ?? false;
        var draft = ThemePalette.CreateDraft(EditorTheme.System, light, id, name);
        var colors = draft.Colors;
        uint bg = normalBg ?? colors[ThemeColor.WindowBackground];
        uint text = normalFg ?? ContrastingText(bg);
        uint header = menuBg ?? Blend(bg, text, 0.08);
        uint note = stringColor ?? colors[ThemeColor.Note];
        uint accent = accentColor ?? colors[ThemeColor.Accent];
        uint selected = visual ?? accent;
        if (selected == bg || selected == note)
            selected = Blend(note, ContrastingText(note), 0.35);
        uint accidentalKey = Blend(bg, text, 0.23);

        colors[ThemeColor.WindowBackground] = bg;
        colors[ThemeColor.Header] = header;
        colors[ThemeColor.Accent] = accent;
        colors[ThemeColor.AccentText] = ContrastingText(accent);
        colors[ThemeColor.NaturalLane] = bg;
        colors[ThemeColor.AccidentalLane] = Blend(bg, text, 0.07);
        colors[ThemeColor.MinorLine] = Blend(bg, text, 0.13);
        colors[ThemeColor.BeatLine] = Blend(bg, text, 0.22);
        colors[ThemeColor.BarLine] = Blend(bg, text, 0.36);
        colors[ThemeColor.Ruler] = header;
        colors[ThemeColor.RulerText] = First(scheme.Foreground, "Pmenu", "StatusLineNC") ?? text;
        colors[ThemeColor.RulerMuted] = comment ?? Blend(header, text, 0.68);
        colors[ThemeColor.NaturalKey] = text;
        colors[ThemeColor.AccidentalKey] = accidentalKey;
        colors[ThemeColor.NaturalKeyText] = ContrastingText(text);
        colors[ThemeColor.AccidentalKeyText] = ContrastingText(accidentalKey);
        colors[ThemeColor.Note] = note;
        colors[ThemeColor.NoteOutline] = Blend(note, ContrastingText(note), 0.27);
        colors[ThemeColor.SelectedNote] = selected;
        colors[ThemeColor.SelectedOutline] = Blend(selected, ContrastingText(selected), 0.40);
        colors[ThemeColor.NoteText] = ContrastingText(note);
        colors[ThemeColor.NoteHandle] = Blend(note, ContrastingText(note), 0.45);
        colors[ThemeColor.Playhead] = cursor ?? colors[ThemeColor.Playhead];
        colors[ThemeColor.Loop] = visual ?? accent;
        return draft;
    }

    private static uint? First(Func<string, uint?> read, params string[] groups)
    {
        foreach (string group in groups)
        {
            if (read(group) is uint color)
                return color;
        }
        return null;
    }

    private static uint ContrastingText(uint rgb) => IsLight(rgb) ? 0x000000u : 0xffffffu;

    private static bool IsLight(uint rgb)
    {
        static double Linear(uint channel)
        {
            double value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(rgb >> 16) + 0.7152 * Linear((rgb >> 8) & 255) + 0.0722 * Linear(rgb & 255) > 0.179;
    }

    private static uint Blend(uint from, uint to, double amount)
    {
        uint Channel(int shift) =>
            (uint)Math.Round(((from >> shift) & 255) * (1 - amount) + ((to >> shift) & 255) * amount);
        return (Channel(16) << 16) | (Channel(8) << 8) | Channel(0);
    }
}
