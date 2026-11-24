using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Signal.Native.Engine;
using Signal.Native.Uno.App.Controls;

namespace Signal.Native.Uno.App.Tempo;

public sealed partial class TempoChangesView : UserControl
{
    private readonly int _ticksPerQuarter;
    private readonly List<TempoChange> _changes;
    private TempoChange? _selected;
    private string _selectedBpmText = "";
    private string _selectedBeatText = "";
    private string _selectedTickText = "";
    private bool _updating;

    public TempoChangesView(TempoMap map)
    {
        _ticksPerQuarter = map.TicksPerQuarter;
        _changes = map.Changes.ToList();
        InitializeComponent();
        decimal lastBeat = Math.Min(long.MaxValue, (decimal)(long.MaxValue / _ticksPerQuarter) + 1);
        NumericScrub.Attach(BeatEntry, BeatBox, () => new(1, lastBeat, 1));
        NumericScrub.Attach(TickEntry, TickBox, () => new(0, _ticksPerQuarter - 1));
        NumericScrub.Attach(BpmEntry, BpmBox, () => new(3.577m, 60_000_000, 120), fractional: true);
        PositionHint.Text = $"Positions start at beat 1, tick 0. Each quarter-note beat has {_ticksPerQuarter} ticks. " +
            "The opening tempo stays at beat 1. Apply updates playback and MIDI save.";
        RefreshPoints(0);
    }

    public IReadOnlyList<TempoChange> Changes => _changes;

    public bool TryFinishEdit()
    {
        if (BeatBox.Text == _selectedBeatText && TickBox.Text == _selectedTickText && BpmBox.Text == _selectedBpmText)
            return true;
        return UpdateSelected();
    }

    private void RefreshPoints(long selectedTick)
    {
        _changes.Sort(static (left, right) => left.Tick.CompareTo(right.Tick));
        _updating = true;
        TempoList.ItemsSource = _changes.Select(change => new TempoRow(change,
            $"Beat {change.Tick / _ticksPerQuarter + 1}, tick {change.Tick % _ticksPerQuarter}  ·  " +
            $"{60_000_000.0 / change.MicrosecondsPerQuarterNote:0.###} BPM")).ToArray();
        TempoList.SelectedIndex = _changes.FindIndex(change => change.Tick == selectedTick);
        _updating = false;
        LoadSelected();
    }

    private void TempoSelected(object sender, SelectionChangedEventArgs args)
    {
        if (!_updating)
            LoadSelected();
    }

    private void LoadSelected()
    {
        _selected = (TempoList.SelectedItem as TempoRow)?.Change;
        if (_selected is { } change)
        {
            _updating = true;
            _selectedBeatText = BeatBox.Text = (change.Tick / _ticksPerQuarter + 1).ToString(CultureInfo.CurrentCulture);
            _selectedTickText = TickBox.Text = (change.Tick % _ticksPerQuarter).ToString(CultureInfo.CurrentCulture);
            _selectedBpmText = BpmBox.Text = (60_000_000.0 / change.MicrosecondsPerQuarterNote)
                .ToString("0.###", CultureInfo.CurrentCulture);
            _updating = false;
        }
        RefreshButtons();
    }

    private bool TryReadPoint(out TempoChange point, out string error)
    {
        point = default;
        if (!long.TryParse(BeatBox.Text, out long beat) || beat < 1 ||
            !int.TryParse(TickBox.Text, out int tick) || tick < 0 || tick >= _ticksPerQuarter)
        {
            error = $"Enter a beat of 1 or greater and a tick from 0 to {_ticksPerQuarter - 1}.";
            return false;
        }

        Int128 position = (Int128)(beat - 1) * _ticksPerQuarter + tick;
        if (position > long.MaxValue)
        {
            error = "This position exceeds the song's tick range.";
            return false;
        }

        int microseconds;
        if (_selected is { } selected && BpmBox.Text == _selectedBpmText)
        {
            microseconds = selected.MicrosecondsPerQuarterNote;
        }
        else
        {
            if (!double.TryParse(BpmBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double bpm) ||
                !double.IsFinite(bpm) || bpm <= 0)
            {
                error = "Enter a positive tempo in BPM.";
                return false;
            }
            double value = Math.Round(60_000_000.0 / bpm, MidpointRounding.AwayFromZero);
            if (value is < 1 or > 0xffffff)
            {
                error = "Enter a tempo within the MIDI range (about 3.5763 to 60,000,000 BPM).";
                return false;
            }
            microseconds = (int)value;
        }

        point = new TempoChange((long)position, microseconds);
        error = "";
        return true;
    }

    private void ValueChanged(object sender, TextChangedEventArgs args)
    {
        if (!_updating && ValidationText is not null)
            RefreshButtons();
    }

    private void RefreshButtons()
    {
        bool valid = TryReadPoint(out var point, out string error);
        bool occupied = valid && _changes.Any(change => change.Tick == point.Tick);
        bool openingMoved = _selected?.Tick == 0 && point.Tick != 0;
        bool collision = occupied && point.Tick != _selected?.Tick;
        AddButton.IsEnabled = valid && !occupied;
        UpdateButton.IsEnabled = valid && _selected is not null && !openingMoved && !collision && point != _selected.Value;
        RemoveButton.IsEnabled = _selected?.Tick > 0;
        RemoveLaterButton.IsEnabled = _changes.Count > 1;
        if (valid && openingMoved)
            error = "The opening tempo stays at beat 1, tick 0. Use Add to create a later point.";
        else if (valid && collision)
            error = "A tempo point already occupies that position. Select it to change its BPM.";
        ValidationText.Text = error;
        ValidationText.Visibility = error.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AddClicked(object sender, RoutedEventArgs args)
    {
        if (!TryReadPoint(out var point, out _) || _changes.Any(change => change.Tick == point.Tick))
            return;
        _changes.Add(point);
        RefreshPoints(point.Tick);
    }

    private void UpdateClicked(object sender, RoutedEventArgs args) => UpdateSelected();

    private bool UpdateSelected()
    {
        RefreshButtons();
        if (!TryReadPoint(out var point, out _) || _selected is not { } selected ||
            (selected.Tick == 0 && point.Tick != 0) ||
            _changes.Any(change => change.Tick == point.Tick && change.Tick != selected.Tick))
            return false;

        _changes[_changes.IndexOf(selected)] = point;
        RefreshPoints(point.Tick);
        return true;
    }

    private void RemoveClicked(object sender, RoutedEventArgs args)
    {
        if (_selected is not { Tick: > 0 } selected)
            return;
        int index = _changes.IndexOf(selected);
        _changes.RemoveAt(index);
        RefreshPoints(_changes[Math.Min(index, _changes.Count - 1)].Tick);
    }

    private sealed record TempoRow(TempoChange Change, string Label);

    private void RemoveLaterClicked(object sender, RoutedEventArgs args)
    {
        _changes.RemoveRange(1, _changes.Count - 1);
        RefreshPoints(0);
    }
}
