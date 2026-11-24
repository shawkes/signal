using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using static Signal.Native.Uno.App.Appearance.ThemeBrushes;

namespace Signal.Native.Uno.App.Appearance;

internal sealed record EditorTheme(string Id, string Label, WindowPalette? Window, PianoRollPalette? PianoRoll)
{
    public static EditorTheme System { get; } = new("System", "System", null, null);

    public static IReadOnlyList<EditorTheme> Choices { get; } =
    [
        System,
        new("Elflord", "Elflord",
            new(Rgb(0x191520), Rgb(0x262034), Rgb(0xE2A0ED), Rgb(0x2C1435)),
            new(
                Rgb(0x211A2B), Rgb(0x181321), Rgb(0x352942), Rgb(0x493858), Rgb(0x66507D),
                Rgb(0x2E253B), Rgb(0xF3E9FE), Rgb(0xBDA9CF),
                Rgb(0xDDD1EA), Rgb(0x47365C), Rgb(0x291D34), Rgb(0xF3E9FE),
                Rgb(0x73CCD5), Rgb(0x9BE3EA), Rgb(0xDEA8F6), Rgb(0xF4D3FF),
                Rgb(0x20212F), Rgb(0x3A5066), Rgb(0xFF83C2), Rgb(0x929DF4))),
        new("Gruvbox", "Gruvbox",
            new(Rgb(0x282725), Rgb(0x39332A), Rgb(0xFABD63), Rgb(0x342717)),
            new(
                Rgb(0x2B2824), Rgb(0x211F1B), Rgb(0x443D33), Rgb(0x594D3C), Rgb(0x71604C),
                Rgb(0x393229), Rgb(0xF5E7CA), Rgb(0xCAB89B),
                Rgb(0xE4D3AC), Rgb(0x554638), Rgb(0x302619), Rgb(0xF5E7CA),
                Rgb(0xA8B862), Rgb(0xC5CF83), Rgb(0xF6BA65), Rgb(0xFFE0A3),
                Rgb(0x302619), Rgb(0x5C4A27), Rgb(0xF48C69), Rgb(0x88AA97))),
        new("cyberdream", "Cyberdream",
            new(Rgb(0x16181A), Rgb(0x1E2124), Rgb(0x5EF1FF), Rgb(0x162229)),
            new(
                Rgb(0x202326), Rgb(0x16181A), Rgb(0x2C3036), Rgb(0x3C4048), Rgb(0x555C68),
                Rgb(0x272B30), Rgb(0xFFFFFF), Rgb(0xA6B0C1),
                Rgb(0xD4DDE6), Rgb(0x3A424E), Rgb(0x19202A), Rgb(0xECF2FA),
                Rgb(0x5EFF6C), Rgb(0x94FFA1), Rgb(0xFF5EF1), Rgb(0xFFC1FA),
                Rgb(0x1A2026), Rgb(0x3B4851), Rgb(0xF1FF5E), Rgb(0x5EA1FF))),
        new("littleprince", "Little Prince",
            new(Rgb(0x1A1C2E), Rgb(0x1F2233), Rgb(0xE8C975), Rgb(0x2C241B)),
            new(
                Rgb(0x22263B), Rgb(0x181A2A), Rgb(0x31364D), Rgb(0x404563), Rgb(0x5B6080),
                Rgb(0x2A2E44), Rgb(0xCAD6F5), Rgb(0xA0ACD9),
                Rgb(0xCAD6F5), Rgb(0x404563), Rgb(0x20243A), Rgb(0xE4EAFC),
                Rgb(0x90C290), Rgb(0xB6DEB4), Rgb(0xE8C975), Rgb(0xFBEDB9),
                Rgb(0x242432), Rgb(0x485443), Rgb(0xE5738B), Rgb(0x86C5C5))),
        new("tokyonight-night", "Tokyo Night (Night)",
            new(Rgb(0x1A1B26), Rgb(0x222538), Rgb(0x7AA2F7), Rgb(0x16161E)),
            new(
                Rgb(0x202235), Rgb(0x16161E), Rgb(0x2F3449), Rgb(0x3B4261), Rgb(0x565F89),
                Rgb(0x292E42), Rgb(0xC0CAF5), Rgb(0xA9B1D6),
                Rgb(0xC0CAF5), Rgb(0x394B70), Rgb(0x1A1B26), Rgb(0xE6ECFF),
                Rgb(0x7AA2F7), Rgb(0xA1BDFC), Rgb(0xBB9AF7), Rgb(0xDED0FA),
                Rgb(0x16161E), Rgb(0x394B70), Rgb(0xE0AF68), Rgb(0x73DACA))),
        new("everforest", "Everforest",
            new(Rgb(0x2D353B), Rgb(0x343F44), Rgb(0xA7C080), Rgb(0x252D29)),
            new(
                Rgb(0x2D393A), Rgb(0x232A2E), Rgb(0x3D484D), Rgb(0x475258), Rgb(0x626E68),
                Rgb(0x394547), Rgb(0xD3C6AA), Rgb(0xB4B8A4),
                Rgb(0xD3C6AA), Rgb(0x4F6057), Rgb(0x272E2B), Rgb(0xEFE8D7),
                Rgb(0x83C092), Rgb(0xB2DBB9), Rgb(0xDBBC7F), Rgb(0xF6E2B8),
                Rgb(0x282F2B), Rgb(0x425047), Rgb(0xE67E80), Rgb(0x7FBBB3))),
        new("mocha", "Mocha",
            new(Rgb(0x1B1814), Rgb(0x302A20), Rgb(0xC2BFA5), Rgb(0x1B1814)),
            new(
                Rgb(0x211D17), Rgb(0x1B1814), Rgb(0x332D24), Rgb(0x443C2F), Rgb(0x665640),
                Rgb(0x302A20), Rgb(0xDDDDCC), Rgb(0xB4AD93),
                Rgb(0xDCD7BB), Rgb(0x514739), Rgb(0x211D17), Rgb(0xF0E9D5),
                Rgb(0xA4C260), Rgb(0xC4D68C), Rgb(0xCDA869), Rgb(0xF0D8A6),
                Rgb(0x242519), Rgb(0x506036), Rgb(0xF1D885), Rgb(0x84A6C0))),
        new("idleFingers", "idleFingers",
            new(Rgb(0x282828), Rgb(0x323232), Rgb(0xFFC66D), Rgb(0x24201B)),
            new(
                Rgb(0x303030), Rgb(0x282828), Rgb(0x3F3F3F), Rgb(0x505050), Rgb(0x666666),
                Rgb(0x363636), Rgb(0xD8D8D8), Rgb(0xB1B1B1),
                Rgb(0xD8D8D8), Rgb(0x484848), Rgb(0x242424), Rgb(0xE8E8E8),
                Rgb(0xA5C261), Rgb(0xC4D989), Rgb(0x82AECE), Rgb(0xB7DFF8),
                Rgb(0x18191B), Rgb(0x44584E), Rgb(0xFF6347), Rgb(0xA9BAE2))),
        new("cobalt", "Cobalt",
            new(Rgb(0x002F52), Rgb(0x0E2231), Rgb(0xFFEF92), Rgb(0x173148)),
            new(
                Rgb(0x00385D), Rgb(0x002943), Rgb(0x154364), Rgb(0x245575), Rgb(0x39789D),
                Rgb(0x123D5A), Rgb(0xFFFFFF), Rgb(0xA9C4D5),
                Rgb(0xD2E2EC), Rgb(0x205476), Rgb(0x0E2B40), Rgb(0xE8F4FC),
                Rgb(0x3AD900), Rgb(0x80FFBB), Rgb(0xFFEF92), Rgb(0xFFFFCC),
                Rgb(0x13251E), Rgb(0x426333), Rgb(0xFF9D00), Rgb(0xFF7C9D))),
        new("vividchalk", "Vividchalk",
            new(Rgb(0x000000), Rgb(0x171717), Rgb(0xAABBEE), Rgb(0x191D2A)),
            new(
                Rgb(0x101010), Rgb(0x000000), Rgb(0x282828), Rgb(0x3A3A3A), Rgb(0x555577),
                Rgb(0x222222), Rgb(0xEEEEEE), Rgb(0xAABBEE),
                Rgb(0xDDEEFF), Rgb(0x333333), Rgb(0x1C2130), Rgb(0xEEEEEE),
                Rgb(0x66FF00), Rgb(0xB1FF73), Rgb(0xFFCC00), Rgb(0xFFEDA3),
                Rgb(0x162010), Rgb(0x485316), Rgb(0xFF6600), Rgb(0xBE7CE2))),
        new("desert256", "Desert256",
            new(Rgb(0x000000), Rgb(0x24211D), Rgb(0xF0E68C), Rgb(0x2C271A)),
            new(
                Rgb(0x1B1814), Rgb(0x100E0B), Rgb(0x302B23), Rgb(0x463E31), Rgb(0x60533F),
                Rgb(0x302A21), Rgb(0xF5DEB3), Rgb(0xC2BFA5),
                Rgb(0xE3DBC7), Rgb(0x484035), Rgb(0x2A231B), Rgb(0xF5E9D2),
                Rgb(0x98FB98), Rgb(0xC9FFD0), Rgb(0xF0E68C), Rgb(0xFFF0B6),
                Rgb(0x23291C), Rgb(0x506445), Rgb(0xFA8072), Rgb(0x87CEEB))),
        new("reason", "Reason",
            new(Rgb(0xD0D9AE), Rgb(0xBAC29B), Rgb(0xD87A32), Rgb(0x211B14), ElementTheme.Light),
            new(
                Rgb(0xDCE6B8), Rgb(0xD0D9AE), Rgb(0xBFC99C), Rgb(0xAAB686), Rgb(0x89966D),
                Rgb(0xBAC29B), Rgb(0x2A3220), Rgb(0x475039),
                Rgb(0xF1F2D7), Rgb(0x657150), Rgb(0x293322), Rgb(0xF7F9E8),
                Rgb(0x73A883), Rgb(0x2E664A), Rgb(0xD87A32), Rgb(0x924418),
                Rgb(0x192018), Rgb(0x42513C), Rgb(0xA53720), Rgb(0x3C7355))),
        new("nonfelony", "Nonfelony",
            new(Rgb(0xFFFCFC), Rgb(0xE8ECFF), Rgb(0xA7B7F7), Rgb(0x222A43), ElementTheme.Light),
            new(
                Rgb(0xFCF8F9), Rgb(0xF4ECEF), Rgb(0xE1D8DE), Rgb(0xC9BCCB), Rgb(0xA898AE),
                Rgb(0xC7F0F7), Rgb(0x233247), Rgb(0x536071),
                Rgb(0xFFF8EE), Rgb(0x89545C), Rgb(0x292329), Rgb(0xFFF8EE),
                Rgb(0x40D090), Rgb(0x248264), Rgb(0xA7B7F7), Rgb(0x5668AC),
                Rgb(0x182625), Rgb(0x455E60), Rgb(0xAF4F60), Rgb(0x5383C0))),
        new("bluedrake", "Bluedrake",
            new(Rgb(0x002D49), Rgb(0x003951), Rgb(0x54C8BC), Rgb(0x122C30)),
            new(
                Rgb(0x153F53), Rgb(0x002D49), Rgb(0x245065), Rgb(0x376276), Rgb(0x577284),
                Rgb(0x003951), Rgb(0xDAE6F0), Rgb(0xB4C3CF),
                Rgb(0xDAE6F0), Rgb(0x2F5468), Rgb(0x153046), Rgb(0xEDF8FF),
                Rgb(0x20B9AC), Rgb(0x78DAD1), Rgb(0xB18AEF), Rgb(0xDCC6FD),
                Rgb(0x102530), Rgb(0x34536C), Rgb(0xDFAF50), Rgb(0xE987CC))),
        new("CandyPaper", "CandyPaper",
            new(Rgb(0x262626), Rgb(0x303630), Rgb(0xA2BFA2), Rgb(0x212A21)),
            new(
                Rgb(0x2B312B), Rgb(0x242824), Rgb(0x3A433A), Rgb(0x4D584B), Rgb(0x62705E),
                Rgb(0x333D33), Rgb(0xD6E2D5), Rgb(0xAEBCAA),
                Rgb(0xCFDDC9), Rgb(0x465449), Rgb(0x243025), Rgb(0xE6F0E1),
                Rgb(0x94BF69), Rgb(0xC0DAA2), Rgb(0x78ACDF), Rgb(0xABCDF0),
                Rgb(0x172521), Rgb(0x3F553E), Rgb(0xBFBF4C), Rgb(0xC687C8)))
    ];

    public static EditorTheme Find(string? id) =>
        Choices.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)) ?? System;
}

internal sealed record WindowPalette(
    Brush Background, Brush Header, Brush Accent, Brush AccentText, ElementTheme Mode = ElementTheme.Dark);

internal sealed record PianoRollPalette(
    Brush NaturalLane, Brush AccidentalLane, Brush MinorLine, Brush BeatLine, Brush MajorLine,
    Brush Ruler, Brush RulerText, Brush RulerMuted,
    Brush NaturalKey, Brush AccidentalKey, Brush NaturalKeyText, Brush AccidentalKeyText,
    Brush Note, Brush NoteOutline, Brush SelectedNote, Brush SelectedOutline, Brush NoteText, Brush Handle,
    Brush Playhead, Brush Loop)
{
    public static PianoRollPalette Dark { get; } = new(
        Rgb(0x182432), Rgb(0x131D29), Rgb(0x273648), Rgb(0x374C63), Rgb(0x4A6078),
        Rgb(0x222F3E), Rgb(0xE5EAF1), Rgb(0xA3B5C8),
        Rgb(0xC5D2DF), Rgb(0x33465D), Rgb(0x172636), Rgb(0xE2EBF4),
        Rgb(0x57BAA9), Rgb(0x64CDB8), Rgb(0x96E7D4), Rgb(0xE2FFF9),
        Rgb(0x132D2A), Rgb(0x326952), Rgb(0xFFC16C), Rgb(0x76B9FF));

    public static PianoRollPalette Light { get; } = new(
        Rgb(0xF8FAFD), Rgb(0xEEF3F9), Rgb(0xDEE7F0), Rgb(0xC7D6E4), Rgb(0xA6BBCC),
        Rgb(0xE4EDF6), Rgb(0x22384D), Rgb(0x4B637B),
        Rgb(0xF4F7FC), Rgb(0x3B526A), Rgb(0x1B3043), Rgb(0xF0F5FB),
        Rgb(0x7BCCBA), Rgb(0x318574), Rgb(0x63C5AD), Rgb(0x134D43),
        Rgb(0x112E29), Rgb(0x376F64), Rgb(0xA75314), Rgb(0x0C65B4));
}

internal static class ThemeBrushes
{
    public static SolidColorBrush Rgb(uint value) =>
        new(Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value));
}
