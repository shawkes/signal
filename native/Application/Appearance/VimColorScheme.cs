using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Signal.Native.Application.Appearance;

/// <summary>Reads literal Vim highlight definitions as color data.</summary>
public sealed class VimColorScheme
{
    private static readonly Regex NamePattern = new(
        @"^(?:let\s+)?g:colors_name\s*=\s*(?:'(?<name>[^']+)'|""(?<name>[^""]+)"")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ModePattern = new(
        @"^set(?:local|global)?\s+(?:bg|background)\s*=\s*(?<mode>light|dark)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex AttributePattern = new(
        @"(?:^|\s)(?:(?<key>\w+)\s*=\s*(?<value>'[^']*'|""[^""]*""|[^\s|]+)|(?<comment>""))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly uint[] Ansi =
    [
        0x000000, 0x800000, 0x008000, 0x808000, 0x000080, 0x800080, 0x008080, 0xc0c0c0,
        0x808080, 0xff0000, 0x00ff00, 0xffff00, 0x0000ff, 0xff00ff, 0x00ffff, 0xffffff
    ];
    private readonly Dictionary<string, Highlight> _groups = new(StringComparer.OrdinalIgnoreCase);

    private VimColorScheme(string name) => Name = name;

    public string Name { get; private set; }
    public bool? IsLight { get; private set; }

    public static VimColorScheme Parse(string text, string fallbackName)
    {
        var scheme = new VimColorScheme(fallbackName);
        using var reader = new StringReader(text);
        string? command = null;
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.StartsWith('\\') && command is not null)
            {
                command += " " + line[1..];
                continue;
            }

            if (command is not null)
                scheme.ReadCommand(command);
            command = line;
        }
        if (command is not null)
            scheme.ReadCommand(command);
        return scheme;
    }

    public uint? Foreground(string group) => ResolveColor(group, background: false);
    public uint? Background(string group) => ResolveColor(group, background: true);

    private void ReadCommand(string line)
    {
        line = line.TrimStart(':').TrimStart();
        if (line.Length == 0 || line.StartsWith('"') || line.StartsWith('#'))
            return;

        if (NamePattern.Match(line) is { Success: true } name)
        {
            Name = name.Groups["name"].Value;
            return;
        }
        if (ModePattern.Match(line) is { Success: true } mode)
        {
            IsLight = mode.Groups["mode"].Value.Equals("light", StringComparison.OrdinalIgnoreCase);
            return;
        }

        // A pipe after a highlight starts another Ex command.
        int pipe = line.IndexOf('|');
        if (pipe >= 0)
            line = line[..pipe];
        var parts = line.Split((char[]?)null, 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return;
        bool force = parts[0].EndsWith('!');
        string verb = parts[0].TrimEnd('!');
        if (verb.Length < 2 || !"highlight".StartsWith(verb, StringComparison.OrdinalIgnoreCase))
            return;

        string arguments = line[parts[0].Length..].TrimStart();
        bool isDefault = Keyword(parts[1], "default", 3);
        if (isDefault)
            arguments = arguments[parts[1].Length..].TrimStart();
        parts = arguments.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return;

        string group = parts[0];
        if (group.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            if (parts.Length > 1 && !parts[1].StartsWith('"'))
                _groups.Remove(parts[1]);
            else
                _groups.Clear();
            return;
        }
        if (group.Equals("link", StringComparison.OrdinalIgnoreCase) && parts.Length == 3)
        {
            group = parts[1];
            if (!_groups.ContainsKey(group) || (force && !isDefault))
                _groups[group] = new Highlight { Link = parts[2].Split((char[]?)null, 2)[0] };
            return;
        }
        _groups.TryGetValue(group, out var existing);
        if (parts.Length < 2 || (isDefault && existing is not null))
            return;
        if (parts[1].Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            _groups[group] = new Highlight();
            return;
        }
        var colors = existing is { Link: null } ? existing : new Highlight();
        foreach (Match attribute in AttributePattern.Matches(arguments[group.Length..]))
        {
            if (attribute.Groups["comment"].Success)
                break;
            string value = attribute.Groups["value"].Value.Trim('\'', '"');
            switch (attribute.Groups["key"].Value.ToLowerInvariant())
            {
                case "guifg": colors.GuiForeground = value; break;
                case "guibg": colors.GuiBackground = value; break;
                case "ctermfg": colors.TerminalForeground = value; break;
                case "ctermbg": colors.TerminalBackground = value; break;
                case "gui": colors.GuiReverse = IsReverse(value); break;
                case "cterm": colors.TerminalReverse = IsReverse(value); break;
            }
        }
        _groups[group] = colors;
    }

    private uint? ResolveColor(string group, bool background)
    {
        bool allowNormal = !group.Equals("Normal", StringComparison.OrdinalIgnoreCase);
        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_groups.TryGetValue(group, out var colors) && linked.Add(group))
        {
            if (colors.Link is { } link)
            {
                group = link;
                continue;
            }
            bool useBackground = background ^ (colors.GuiReverse ?? colors.TerminalReverse ?? false);
            string? gui = useBackground ? colors.GuiBackground : colors.GuiForeground;
            string? term = useBackground ? colors.TerminalBackground : colors.TerminalForeground;
            return ResolveToken(gui, allowNormal, terminal: false) ?? ResolveToken(term, allowNormal, terminal: true) ??
                (useBackground != background && allowNormal ? ResolveColor("Normal", useBackground) : null);
        }
        return null;
    }

    private uint? ResolveToken(string? color, bool allowNormal, bool terminal)
    {
        if (color is null || color.Equals("NONE", StringComparison.OrdinalIgnoreCase))
            return null;
        if (color.Equals("fg", StringComparison.OrdinalIgnoreCase) || color.Equals("bg", StringComparison.OrdinalIgnoreCase))
            return allowNormal ? ResolveColor("Normal", color.Equals("bg", StringComparison.OrdinalIgnoreCase)) : null;
        if (color.Length == 7 && color[0] == '#' &&
            uint.TryParse(color.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            return rgb;
        if (terminal && int.TryParse(color, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index is >= 0 and < 256)
            return TerminalColor(index);

        string name = color.Replace(" ", "").ToLowerInvariant().Replace("grey", "gray");
        if (name.StartsWith("gray", StringComparison.Ordinal) &&
            int.TryParse(name.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out int level) && level <= 100)
            return (uint)Math.Round(level * 255 / 100.0) * 0x010101;
        return name switch
        {
            "gray" => 0xbebebe,
            "green" => 0x00ff00,
            "maroon" when !terminal => 0xb03060,
            "purple" when !terminal => 0xa020f0,
            "darkyellow" => 0x808000,
            _ => Color.FromName(name) is { IsKnownColor: true, IsSystemColor: false, A: 255 } known
                ? (uint)known.ToArgb() & 0xffffff
                : null
        };
    }

    private static uint TerminalColor(int index)
    {
        if (index < 16)
            return Ansi[index];
        if (index >= 232)
            return (uint)(8 + 10 * (index - 232)) * 0x010101;
        int cube = index - 16;
        return (Level(cube / 36) << 16) | (Level(cube / 6 % 6) << 8) | Level(cube % 6);

        static uint Level(int channel) => (uint)(channel == 0 ? 0 : 55 + 40 * channel);
    }

    private static bool Keyword(string word, string keyword, int minimumLength) =>
        word.Length >= minimumLength && keyword.StartsWith(word, StringComparison.OrdinalIgnoreCase);

    private static bool IsReverse(string value) => value.Split(',').Any(flag =>
        flag.Equals("reverse", StringComparison.OrdinalIgnoreCase) || flag.Equals("inverse", StringComparison.OrdinalIgnoreCase));

    private sealed class Highlight
    {
        public string? Link { get; init; }
        public string? GuiForeground { get; set; }
        public string? GuiBackground { get; set; }
        public string? TerminalForeground { get; set; }
        public string? TerminalBackground { get; set; }
        public bool? GuiReverse { get; set; }
        public bool? TerminalReverse { get; set; }
    }
}
