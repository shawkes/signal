namespace Signal.Native.Engine;

/// <summary>One captured note and its timing relative to the nearest beat after compensation.</summary>
public readonly record struct RecordedNote(
    long TakeId,
    long StartTick,
    long LengthTicks,
    byte Channel,
    byte NoteNumber,
    byte Velocity,
    long ReceivedFrame,
    long CompensatedFrame,
    long PlayedLengthFrames,
    long NearestBeat,
    double MillisecondsFromBeat,
    double RawMillisecondsFromBeat)
{
    public SongNote ToSongNote() => new(StartTick, LengthTicks, Channel, NoteNumber, Velocity);
}

internal sealed class Recording(TempoMap tempoMap, int sampleRate)
{
    private const int NotesPerChannel = 128;
    private readonly HeldNote[] _held = new HeldNote[16 * NotesPerChannel];
    private readonly SingleConsumerRing<RecordedNote> _notes = new(2048);
    private readonly TempoMap _defaultTempoMap = tempoMap;
    private TempoMap _takeTempoMap = tempoMap;
    private long? _songOriginFrame;
    private long _takeId;
    private long _offsetFrames;
    private int _quantizationDivision;
    private long _droppedNotes;

    public long DroppedNotes => Volatile.Read(ref _droppedNotes);

    public void Start(long atFrame, long takeId, long offsetFrames, int quantizationDivision) =>
        StartTake(atFrame, takeId, offsetFrames, quantizationDivision, _defaultTempoMap, null);

    public void Start(long firstSongFrame, long takeId, long offsetFrames, int quantizationDivision, TempoMap songTempoMap) =>
        StartTake(firstSongFrame, takeId, offsetFrames, quantizationDivision, songTempoMap, firstSongFrame);

    private void StartTake(long atFrame, long takeId, long offsetFrames, int quantizationDivision,
        TempoMap takeTempoMap, long? songOriginFrame)
    {
        Stop(atFrame);
        _takeTempoMap = takeTempoMap;
        _songOriginFrame = songOriginFrame;
        _takeId = takeId;
        _offsetFrames = offsetFrames;
        _quantizationDivision = quantizationDivision;
    }

    public void Stop(long frame)
    {
        FinishAll(frame);
        _takeId = 0;
    }

    public void FinishAll(long frame)
    {
        if (_takeId == 0)
        {
            return;
        }

        for (var i = 0; i < _held.Length; i++)
        {
            Finish(i, frame);
        }
    }

    public void ProcessMidi(MidiMessage message, long frame)
    {
        if (_takeId == 0 || (_songOriginFrame is { } firstSongFrame && frame < firstSongFrame))
        {
            return;
        }

        var channelStart = (message.Status & 0x0f) * NotesPerChannel;
        var key = channelStart + message.Data1;
        switch (message.Status & 0xf0)
        {
            case 0x90 when message.Data2 != 0:
                Finish(key, frame);
                _held[key] = new HeldNote(frame, message.Data2);
                break;
            case 0x80:
            case 0x90:
                Finish(key, frame);
                break;
            case 0xb0 when message.Data1 is 120 or 123:
                for (var i = channelStart; i < channelStart + NotesPerChannel; i++)
                {
                    Finish(i, frame);
                }
                break;
        }
    }

    public bool TryReadNote(out RecordedNote note) => _notes.TryRead(out note);

    private void Finish(int key, long releaseFrame)
    {
        var held = _held[key];
        if (held.Velocity == 0)
        {
            return;
        }

        _held[key] = default;
        var playedLengthFrames = Math.Max(0, releaseFrame - held.ReceivedFrame);
        bool onSongTimeline = _songOriginFrame.HasValue;
        var relativeReceipt = held.ReceivedFrame - _songOriginFrame.GetValueOrDefault();
        var compensatedStart = relativeReceipt - _offsetFrames;
        var startTick = _takeTempoMap.SampleToTick(compensatedStart, sampleRate);
        // A tempo change can fall inside the played interval. Measure its duration before moving the note.
        var durationStart = onSongTimeline ? relativeReceipt : compensatedStart;
        var durationStartTick = onSongTimeline
            ? _takeTempoMap.SampleToTick(durationStart, sampleRate)
            : startTick;
        var durationEndTick = _takeTempoMap.SampleToTick(durationStart + playedLengthFrames, sampleRate);
        var lengthTicks = RoundTick(durationEndTick) - RoundTick(durationStartTick);
        var beat = (long)Math.Floor(startTick / _takeTempoMap.TicksPerQuarter);
        if (onSongTimeline)
            beat = Math.Max(0, beat);

        if (_quantizationDivision != 0)
        {
            var grid = 4.0 * _takeTempoMap.TicksPerQuarter / _quantizationDivision;
            startTick = Math.Round(startTick / grid, MidpointRounding.AwayFromZero) * grid;
        }

        var beatFrame = BeatFrame(beat);
        var nextBeatFrame = BeatFrame(beat + 1);
        if (nextBeatFrame - compensatedStart <= compensatedStart - beatFrame)
        {
            beat++;
            beatFrame = nextBeatFrame;
        }

        var note = new RecordedNote(
            _takeId, Math.Max(0, RoundTick(startTick)), lengthTicks,
            (byte)(key / NotesPerChannel), (byte)(key % NotesPerChannel), held.Velocity,
            held.ReceivedFrame, held.ReceivedFrame - _offsetFrames, playedLengthFrames, beat,
            (compensatedStart - beatFrame) * 1000.0 / sampleRate,
            (relativeReceipt - beatFrame) * 1000.0 / sampleRate);

        if (!_notes.TryWrite(note))
        {
            Interlocked.Increment(ref _droppedNotes);
        }
    }

    private long BeatFrame(long beat) => _takeTempoMap.TickToSample((double)beat * _takeTempoMap.TicksPerQuarter, sampleRate);
    private static long RoundTick(double tick) => (long)Math.Round(tick, MidpointRounding.AwayFromZero);

    private readonly record struct HeldNote(long ReceivedFrame, byte Velocity);
}
