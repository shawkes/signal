using Signal.Native.Engine;
using Xunit;

namespace Signal.Native.Engine.Tests;

public sealed class MeterMapTests
{
    [Fact]
    public void ThreeFourCountsThreeBeatsPerBarAndKeepsExistingMidiTicks()
    {
        var map = new MeterMap(480, [new(0, new SongMeter(3, 4))]);
        Assert.Equal(new MeterPosition(1, 3, 960, 0), map.PositionAt(1439));
        Assert.Equal(new MeterPosition(2, 1, 1440, 1440), map.PositionAt(1440));
        Assert.Equal(1440, map.NextBeatTick(1439));
        Assert.Equal(2880, map.NextBarTick(1440));
    }

    [Fact]
    public void BarStartAndBeatQueriesAgreeThroughPartialBarsAndOddTickResolution()
    {
        var map = new MeterMap(101, [new(0, new(3, 8)), new(253, new(5, 4)), new(800, new(7, 16))]);
        Assert.Equal(new MeterPosition(1, 1, 0, 0), map.PositionAt(50));
        Assert.Equal(new MeterPosition(1, 2, 51, 0), map.PositionAt(51));
        Assert.Equal(new MeterPosition(2, 2, 202, 152), map.PositionAt(252));
        Assert.Equal(new MeterPosition(3, 1, 253, 253), map.PositionAt(253));
        Assert.Equal(253, map.NextBarTick(152));
        Assert.Equal(800, map.NextBeatTick(798));
        Assert.Equal(1, map.IndexAt(799.999));
        Assert.Equal(2, map.IndexAt(800.0));
        Assert.Equal(new long[] { 0, 152, 253, 758, 800, 977 }, Enumerable.Range(1, 6).Select(i => map.TickAtBar(i)));
        for (int bar = 1; bar <= 100; bar++)
        {
            long start = map.TickAtBar(bar);
            Assert.Equal(new MeterPosition(bar, 1, start, start), map.PositionAt(start));
            Assert.Equal(map.TickAtBar(bar + 1), map.NextBarTick(start));
        }
    }

    [Fact]
    public void DefaultAndExplicitRepeatedSignaturesEstablishBarStarts()
    {
        var map = new MeterMap(480, [new(2200, SongMeter.CommonTime)]);
        Assert.Equal(new MeterChange(0, SongMeter.CommonTime), map.Changes[0]);
        Assert.Equal(new MeterPosition(2, 1, 1920, 1920), map.PositionAt(2199));
        Assert.Equal(new MeterPosition(3, 1, 2200, 2200), map.PositionAt(2200));
        Assert.Equal(2200, map.NextBeatTick(2100));
        Assert.Equal(2200, map.TickAtBar(3));
    }

    [Fact]
    public void SubTickMetersAdvanceAndBoundaryQueriesSaturateAtMaximumTick()
    {
        var map = new MeterMap(1, [new(0, new(1, 32)), new(10, new(3, 4))]);
        Assert.Equal(new MeterPosition(1, 1, 0, 0), map.PositionAt(0));
        Assert.Equal(1, map.NextBeatTick(0));
        Assert.Equal(1, map.NextBarTick(0));
        Assert.Equal(81, map.PositionAt(10).Bar);
        Assert.Equal(long.MaxValue, map.NextBarTick(long.MaxValue));
        Assert.Equal(long.MaxValue, map.TickAtBar(long.MaxValue));

        var fractional = new MeterMap(11, [new(0, new(1, 64)), new(10, new(3, 64))]);
        for (long tick = 0; tick < 30; tick++)
        {
            var position = fractional.PositionAt(tick);
            Assert.Equal(tick, position.BeatTick);
            Assert.Equal(position.BarTick, fractional.TickAtBar(position.Bar));
            Assert.Equal(position.Bar, fractional.PositionAt(position.BarTick).Bar);
        }
    }

    [Fact]
    public void EditingMeterRestoresSavedMapIndependentlyOfTempoNotesAndTracks()
    {
        var song = new Song(TempoMap.Constant(120, 101), [new SongTrack("Track", [new(230, 90, 0, 60, 127)])], new(3, 4));
        var editor = new SongTrackEditor(song);
        Assert.Equal(new SongMeter(3, 4), editor.OpeningMeter);
        Assert.True(editor.SetMeterChanges([new(0, new(6, 8)), new(606, new(3, 4))]));
        var saved = editor.MeterMap;
        editor.MarkSaved();
        Assert.False(editor.IsDirty);
        Assert.True(editor.SetMeterChanges([new(0, new(7, 8))]));
        Assert.True(editor.IsDirty);
        Assert.True(editor.UndoMeterEdit());
        Assert.Same(saved, editor.MeterMap);
        Assert.False(editor.IsDirty);
        Assert.True(editor.EditTempoMarker(100, 740_000));
        Assert.True(editor.UndoMeterEdit());
        Assert.Same(song.MeterMap, editor.SnapshotSong().MeterMap);
        Assert.Equal(new SongMeter(3, 4), editor.SnapshotSong().OpeningMeter);
        Assert.Equal(song.Tracks[0].Notes, editor.SnapshotSong().Tracks[0].Notes);
        Assert.True(editor.CanUndoTempoEdit);
        Assert.False(editor.CanUndoTrackEdit);
        Assert.False(editor.CanUndoMeterEdit);
        Assert.False(editor.SetMeterChanges(song.MeterMap.Changes));
        Assert.Throws<ArgumentException>(() => editor.SetMeterChanges([new(20, new(4, 4)), new(20, new(3, 4))]));
        Assert.Throws<ArgumentOutOfRangeException>(() => editor.SetMeterChanges([new(0, new(7, 256))]));
        Assert.Same(song.MeterMap, editor.MeterMap);
        Assert.False(editor.CanUndoMeterEdit);
    }
}
