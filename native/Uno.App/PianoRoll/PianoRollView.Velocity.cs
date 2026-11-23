using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.Engine;
using Windows.System;

namespace Signal.Native.Uno.App.PianoRoll;

public sealed partial class PianoRollView
{
    private void VelocityClicked(object sender, RoutedEventArgs args)
    {
        var document = _document;
        var notes = SelectedNotes();
        int first = notes.Length == 0 ? 96 : notes[0].Note.Velocity;
        var mode = new ComboBox
        {
            Header = "Operation",
            ItemsSource = new[] { "Set to", "Adjust by" },
            SelectedIndex = 0
        };
        var value = new TextBox
        {
            Header = "Velocity",
            PlaceholderText = "1–127",
            Text = notes.All(note => note.Note.Velocity == first) ? first.ToString() : ""
        };
        var apply = new Button { Content = "Apply" };
        var lane = new Button { Content = _showVelocityLane ? "Hide velocity lane" : "Show velocity lane" };
        var panel = new StackPanel { Spacing = 8 };
        if (notes.Length > 0 && _editingEnabled)
        {
            panel.Children.Add(mode);
            panel.Children.Add(value);
            panel.Children.Add(apply);
        }
        panel.Children.Add(lane);
        var flyout = new Flyout { Content = panel };
        bool ReadAmount(out int amount) => int.TryParse(value.Text, out amount) &&
            (mode.SelectedIndex == 1 ? amount is >= -126 and <= 126 : amount is >= 1 and <= 127);
        void RefreshApply() => apply.IsEnabled = ReadAmount(out _);
        RefreshApply();
        value.TextChanged += (_, _) => RefreshApply();
        mode.SelectionChanged += (_, _) =>
        {
            bool relative = mode.SelectedIndex == 1;
            value.Header = relative ? "Velocity offset" : "Velocity";
            value.PlaceholderText = relative ? "−126 to +126" : "1–127";
            value.Text = relative ? "0" : notes.All(note => note.Note.Velocity == first) ? first.ToString() : "";
            RefreshApply();
        };
        void Apply()
        {
            if (!_editingEnabled || !ReferenceEquals(_document, document) || !ReadAmount(out int amount))
                return;
            bool relative = mode.SelectedIndex == 1;
            document!.ReplaceMany(notes.Select(item => item with
            {
                Note = item.Note with
                {
                    Velocity = (byte)Math.Clamp(relative ? item.Note.Velocity + amount : amount, 1, 127)
                }
            }).ToArray());
            flyout.Hide();
        }
        apply.Click += (_, _) => Apply();
        value.KeyDown += (_, args) =>
        {
            if (args.Key != VirtualKey.Enter)
                return;
            args.Handled = true;
            Apply();
        };
        flyout.Opened += (_, _) =>
        {
            if (notes.Length > 0 && _editingEnabled)
            {
                value.Focus(FocusState.Programmatic);
                value.SelectAll();
            }
        };
        lane.Click += (_, _) =>
        {
            SetVelocityLane(!_showVelocityLane);
            flyout.Hide();
        };
        flyout.ShowAt(VelocityButton);
    }
}
