namespace Signal.Native.Application.Appearance;

public enum ThemeColor
{
    WindowBackground, Header, Accent, AccentText,
    NaturalLane, AccidentalLane, MinorLine, BeatLine, BarLine, Ruler, RulerText, RulerMuted,
    NaturalKey, AccidentalKey, NaturalKeyText, AccidentalKeyText,
    Note, NoteOutline, SelectedNote, SelectedOutline, NoteText, NoteHandle, Playhead, Loop
}

public sealed record ThemeDefinition(Guid Id, string Name, bool IsLight, Dictionary<ThemeColor, uint> Colors)
{
    public const int MaxNameLength = 64;

    public bool IsValid() => Id != Guid.Empty && !string.IsNullOrWhiteSpace(Name) && Name.Length <= MaxNameLength &&
        Colors is not null && Enum.GetValues<ThemeColor>().All(role => Colors.TryGetValue(role, out uint rgb) && rgb <= 0xffffff);
}
