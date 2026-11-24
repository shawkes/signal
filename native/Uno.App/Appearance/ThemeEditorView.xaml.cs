using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Signal.Native.Application.Appearance;

namespace Signal.Native.Uno.App.Appearance;

public sealed partial class ThemeEditorView : UserControl
{
    private static readonly (ThemeColor Role, string Label)[] ColorLabels =
    [
        (ThemeColor.WindowBackground, "Window background"),
        (ThemeColor.Header, "Menu background"),
        (ThemeColor.Accent, "Transport buttons"),
        (ThemeColor.AccentText, "Transport button text"),
        (ThemeColor.Note, "Notes"),
        (ThemeColor.NoteOutline, "Note outline"),
        (ThemeColor.SelectedNote, "Selected notes"),
        (ThemeColor.SelectedOutline, "Selected note outline"),
        (ThemeColor.NoteText, "Note text"),
        (ThemeColor.NoteHandle, "Note resize handle"),
        (ThemeColor.Playhead, "Playhead"),
        (ThemeColor.Loop, "Loop and selection"),
        (ThemeColor.NaturalLane, "Grid rows"),
        (ThemeColor.AccidentalLane, "Alternate grid rows"),
        (ThemeColor.MinorLine, "Grid lines"),
        (ThemeColor.BeatLine, "Beat lines"),
        (ThemeColor.BarLine, "Bar lines"),
        (ThemeColor.Ruler, "Ruler background"),
        (ThemeColor.RulerText, "Ruler text"),
        (ThemeColor.RulerMuted, "Beat numbers"),
        (ThemeColor.NaturalKey, "Natural keys"),
        (ThemeColor.AccidentalKey, "Accidental keys"),
        (ThemeColor.NaturalKeyText, "Natural key text"),
        (ThemeColor.AccidentalKeyText, "Accidental key text")
    ];
    private readonly ThemeDefinition _draft;

    internal ThemeEditorView(ThemeDefinition draft)
    {
        _draft = draft;
        InitializeComponent();
        NameBox.MaxLength = ThemeDefinition.MaxNameLength;
        NameBox.Text = draft.Name;
        ModeBox.SelectedIndex = draft.IsLight ? 1 : 0;
        ColorList.ItemsSource = ColorLabels.Select(color =>
            new ColorRow(color.Role, color.Label, ThemeBrushes.Rgb(draft.Colors[color.Role]))).ToArray();
        ColorList.SelectionChanged += (_, _) =>
        {
            if (ColorList.SelectedItem is ColorRow row)
                Picker.Color = row.Brush.Color;
        };
        ColorList.SelectedIndex = 0;
        Picker.ColorChanged += PickerColorChanged;
        ModeBox.SelectionChanged += (_, _) => PreviewChanged?.Invoke(ReadDraft());
        NameBox.TextChanged += (_, _) => ErrorBar.IsOpen = false;
    }

    internal event Action<ThemeDefinition>? PreviewChanged;

    internal ThemeDefinition ReadDraft() =>
        _draft with { Name = NameBox.Text.Trim(), IsLight = ModeBox.SelectedIndex == 1 };

    internal void ShowError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }

    private void PickerColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (ColorList.SelectedItem is not ColorRow row || row.Brush.Color == args.NewColor)
            return;
        row.Brush.Color = args.NewColor;
        _draft.Colors[row.Role] = ThemePalette.Value(args.NewColor);
        PreviewChanged?.Invoke(ReadDraft());
    }

    public sealed record ColorRow(ThemeColor Role, string Label, SolidColorBrush Brush);
}
