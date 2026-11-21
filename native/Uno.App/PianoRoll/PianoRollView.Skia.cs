using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Signal.Native.Engine;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace Signal.Native.Windows.App.PianoRoll;

public sealed partial class PianoRollView
{
    private ScoreCanvas? _scoreDrawing;
    private readonly TranslateTransform _scoreOffset = new();
    private Rect _scoreViewport;

    partial void InitializeScoreDrawing()
    {
        _scoreDrawing = new ScoreCanvas(DrawScore)
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            RenderTransform = _scoreOffset
        };
        ScoreHost.Children.Add(_scoreDrawing);
    }

    partial void UpdateScoreViewport(double left, double top, double width, double height)
    {
        var viewport = new Rect(left, top, width, height);
        if (_scoreDrawing is not { } score || viewport == _scoreViewport)
            return;

        _scoreViewport = viewport;
        _scoreOffset.X = left;
        _scoreOffset.Y = top;
        score.Width = width;
        score.Height = height;
        score.Invalidate();
    }

    partial void InvalidateScore() => _scoreDrawing?.Invalidate();

    private void DrawScore(SKCanvas canvas)
    {
        var viewport = _scoreViewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
            return;

        canvas.Save();
        canvas.Translate(-(float)viewport.Left, -(float)viewport.Top);
        try
        {
            using var paint = new SKPaint();
            DrawBeatGrid(canvas, paint, viewport);
            DrawNotes(canvas, paint, viewport);
        }
        finally
        {
            canvas.Restore();
        }
    }

    private void DrawBeatGrid(SKCanvas canvas, SKPaint paint, Rect tile)
    {
        var minor = BrushColor(_palette.MinorLine);
        var beat = BrushColor(_palette.BeatLine);
        var major = BrushColor(_palette.MajorLine);
        float y = (float)tile.Top;
        float height = (float)tile.Height;
        long lastCell = CellAtPosition(tile.Right);
        for (long cell = CellAtPosition(tile.Left);; cell++)
        {
            PlaceLine(TickOfCell(cell), 1, minor);
            if (cell >= lastCell)
                break;
        }

        long firstTick = long.CreateSaturating(Math.Floor(tile.Left / BeatWidth * _ticksPerBeat));
        long lastTick = long.CreateSaturating(Math.Ceiling(tile.Right / BeatWidth * _ticksPerBeat));
        for (long tick = _meterMap.PositionAt(firstTick).BeatTick; tick <= lastTick;)
        {
            bool bar = tick == _meterMap.PositionAt(tick).BarTick;
            PlaceLine(tick, bar ? 2 : 1, bar ? major : beat);
            long next = _meterMap.NextBeatTick(tick);
            if (next <= tick)
                break;
            tick = next;
        }

        void PlaceLine(long tick, float width, SKColor color)
        {
            paint.Color = color;
            canvas.DrawRect((float)TickX(tick), y, width, height, paint);
        }
    }

    private void DrawNotes(SKCanvas canvas, SKPaint paint, Rect tile)
    {
        CollectVisibleNotes(tile);
        if (_notesInTile.Count == 0)
            return;

        paint.IsAntialias = true;
        var normal = (BrushColor(_palette.Note), BrushColor(_palette.NoteOutline));
        var selected = (BrushColor(_palette.SelectedNote), BrushColor(_palette.SelectedOutline));
        var handle = BrushColor(_palette.Handle);
        var text = BrushColor(_palette.NoteText);
        using var font = new SKFont(SKTypeface.Default, 11);
        foreach (var item in _notesInTile)
            if (!_selectedIds.Contains(item.Id))
                DrawNote(item.Note, normal);
        foreach (var item in _notesInTile)
            if (_selectedIds.Contains(item.Id))
                DrawNote(item.Note, selected);

        void DrawNote(SongNote note, (SKColor Fill, SKColor Outline) colors)
        {
            float x = (float)TickX(note.StartTick);
            float y = (float)PitchY(note.NoteNumber) + 2;
            float width = (float)NoteWidth(note);
            const float height = (float)RowHeight - 4;
            var body = SKRect.Create(x, y, width, height);
            paint.Style = SKPaintStyle.Fill;
            paint.Color = colors.Fill;
            canvas.DrawRoundRect(body, 4, 4, paint);
            paint.Color = colors.Outline;
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1;
            body.Inflate(-0.5f, -0.5f);
            canvas.DrawRoundRect(body, 3.5f, 3.5f, paint);
            paint.Style = SKPaintStyle.Fill;
            if (width >= 8)
            {
                paint.Color = handle;
                canvas.DrawRoundRect(SKRect.Create(x + width - 5, y + (height - 8) / 2, 2, 8), 1, 1, paint);
            }
            if (width >= 42)
            {
                paint.Color = text;
                canvas.Save();
                canvas.ClipRect(SKRect.Create(x + 6, y, width - 13, height));
                canvas.DrawText(Caption(note.NoteNumber, note.Channel == SongTrackEditor.DrumChannel),
                    x + 6, y + 14, SKTextAlign.Left, font, paint);
                canvas.Restore();
            }
        }
    }

    private static SKColor BrushColor(Brush brush)
    {
        var color = ((SolidColorBrush)brush).Color;
        return new SKColor(color.R, color.G, color.B, color.A);
    }

    private sealed class ScoreCanvas(Action<SKCanvas> draw) : SKCanvasElement
    {
        protected override void RenderOverride(SKCanvas canvas, Size area) => draw(canvas);
    }
}
