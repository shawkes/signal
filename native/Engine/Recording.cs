namespace Signal.Native.Engine;

internal delegate void OverdubNoteReady(RecordedNote note, long completedPass);

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
    private LoopCapture? _loop;
    private long _takeId;
    private long _offsetFrames;
    private int _quantizationDivision;
    private long _droppedNotes;

    public long DroppedNotes => Volatile.Read(ref _droppedNotes);

    public void Start(long atFrame, long takeId, long offsetFrames, int quantizationDivision) =>
        StartTake(atFrame, takeId, offsetFrames, quantizationDivision, _defaultTempoMap, null);

    public void Start(long firstSongFrame, long takeId, long offsetFrames, int quantizationDivision, TempoMap songTempoMap) =>
        StartTake(firstSongFrame, takeId, offsetFrames, quantizationDivision, songTempoMap, firstSongFrame);

    public void Start(long firstSongFrame, long takeId, long offsetFrames, int quantizationDivision,
        TempoMap songTempoMap, LoopRange loop, long loopStartFrame, long loopEndFrame, OverdubNoteReady? noteReady = null) =>
        StartTake(firstSongFrame, takeId, offsetFrames, quantizationDivision, songTempoMap, firstSongFrame,
            new LoopCapture(loop, loopStartFrame, loopEndFrame, noteReady));

    private void StartTake(long atFrame, long takeId, long offsetFrames, int quantizationDivision,
        TempoMap takeTempoMap, long? songOriginFrame, LoopCapture? loop = null)
    {
        Stop(atFrame);
        _takeTempoMap = takeTempoMap;
        _songOriginFrame = songOriginFrame;
        _loop = loop;
        _takeId = takeId;
        _offsetFrames = offsetFrames;
        _quantizationDivision = quantizationDivision;
    }

    public void Stop(long frame)
    {
        FinishAll(frame);
        _takeId = 0;
        _loop = null;
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
        if (_loop is { } loop)
        {
            FinishLooped(key, held, playedLengthFrames, loop);
            return;
        }

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
        var (beat, beatFrame) = NearestBeat(compensatedStart, startTick, onSongTimeline);
        startTick = QuantizeStartTick(startTick);

        var note = new RecordedNote(
            _takeId, Math.Max(0, RoundTick(startTick)), lengthTicks,
            (byte)(key / NotesPerChannel), (byte)(key % NotesPerChannel), held.Velocity,
            held.ReceivedFrame, held.ReceivedFrame - _offsetFrames, playedLengthFrames, beat,
            (compensatedStart - beatFrame) * 1000.0 / sampleRate,
            (relativeReceipt - beatFrame) * 1000.0 / sampleRate);

        PublishNote(note);
    }

    private void FinishLooped(int key, HeldNote held, long lengthFrames, LoopCapture loop)
    {
        long firstPhase = (held.ReceivedFrame - _songOriginFrame.GetValueOrDefault()) % loop.FrameCount;
        long firstLength = Math.Min(lengthFrames, loop.FrameCount - firstPhase);
        long finalCoveredFrame = held.ReceivedFrame + Math.Max(0, lengthFrames - 1);
        long completedPass = (finalCoveredFrame - _songOriginFrame.GetValueOrDefault()) / loop.FrameCount;

        var first = LoopNote(key, held, firstPhase, firstLength, loop, continuation: false);
        PublishNote(first, loop.NoteReady, completedPass);
        long remaining = lengthFrames - firstLength;
        if (remaining == 0)
            return;

        var continuation = held with { ReceivedFrame = held.ReceivedFrame + firstLength };
        SongNote? wholeLoop = null;
        if (remaining >= loop.FrameCount)
        {
            // Consecutive full-cycle segments occupy the same edited notes on the song timeline.
            // Emit one regardless of the number of passes for which the key was held.
            var whole = LoopNote(key, continuation, 0, loop.FrameCount, loop, continuation: true);
            wholeLoop = whole.ToSongNote();
            if (wholeLoop != first.ToSongNote())
                PublishNote(whole, loop.NoteReady, completedPass);

            long finalLength = remaining % loop.FrameCount;
            continuation = continuation with { ReceivedFrame = continuation.ReceivedFrame + remaining - finalLength };
            remaining = finalLength;
        }

        if (remaining > 0)
        {
            var last = LoopNote(key, continuation, 0, remaining, loop, continuation: true);
            var lastNote = last.ToSongNote();
            if (lastNote != first.ToSongNote() && lastNote != wholeLoop)
                PublishNote(last, loop.NoteReady, completedPass);
        }
    }

    private RecordedNote LoopNote(int key, HeldNote held, long phase, long lengthFrames,
        LoopCapture loop, bool continuation)
    {
        long sourceReceipt = loop.StartFrame + phase;
        long offset = continuation ? 0 : _offsetFrames;
        long compensatedStart = sourceReceipt - offset;
        double placedTick = _takeTempoMap.SampleToTick(compensatedStart, sampleRate);
        long startTick = continuation ? loop.Range.StartTick :
            Math.Clamp(RoundTick(QuantizeStartTick(placedTick)), loop.Range.StartTick, loop.Range.EndTick - 1);
        long rawStart = LoopTick(sourceReceipt, loop);
        long rawEnd = LoopTick(sourceReceipt + lengthFrames, loop);
        long lengthTicks = Math.Clamp(rawEnd - rawStart, 1, loop.Range.EndTick - startTick);
        var (beat, beatFrame) = NearestBeat(compensatedStart, placedTick, clampToSong: true);
        return new RecordedNote(
            _takeId, startTick, lengthTicks,
            (byte)(key / NotesPerChannel), (byte)(key % NotesPerChannel), held.Velocity,
            held.ReceivedFrame, held.ReceivedFrame - offset, lengthFrames, beat,
            (compensatedStart - beatFrame) * 1000.0 / sampleRate,
            (sourceReceipt - beatFrame) * 1000.0 / sampleRate);
    }

    private long LoopTick(long frame, LoopCapture loop)
    {
        // The loop's rounded PCM boundaries identify the exact song ticks at either edge.
        if (frame == loop.StartFrame)
            return loop.Range.StartTick;
        if (frame == loop.EndFrame)
            return loop.Range.EndTick;

        return Math.Clamp(RoundTick(_takeTempoMap.SampleToTick(frame, sampleRate)),
            loop.Range.StartTick, loop.Range.EndTick);
    }

    private double QuantizeStartTick(double startTick)
    {
        if (_quantizationDivision == 0)
            return startTick;

        var grid = 4.0 * _takeTempoMap.TicksPerQuarter / _quantizationDivision;
        return Math.Round(startTick / grid, MidpointRounding.AwayFromZero) * grid;
    }

    private (long Beat, long Frame) NearestBeat(long sample, double tick, bool clampToSong)
    {
        var beat = (long)Math.Floor(tick / _takeTempoMap.TicksPerQuarter);
        if (clampToSong)
            beat = Math.Max(0, beat);

        var beatFrame = BeatFrame(beat);
        var nextBeatFrame = BeatFrame(beat + 1);
        return nextBeatFrame - sample <= sample - beatFrame ? (beat + 1, nextBeatFrame) : (beat, beatFrame);
    }

    private void PublishNote(RecordedNote note, OverdubNoteReady? noteReady = null, long completedPass = 0)
    {
        if (_notes.TryWrite(note))
        {
            noteReady?.Invoke(note, completedPass);
        }
        else
        {
            Interlocked.Increment(ref _droppedNotes);
        }
    }

    private long BeatFrame(long beat) => _takeTempoMap.TickToSample((double)beat * _takeTempoMap.TicksPerQuarter, sampleRate);
    private static long RoundTick(double tick) => (long)Math.Round(tick, MidpointRounding.AwayFromZero);

    private readonly record struct LoopCapture(LoopRange Range, long StartFrame, long EndFrame, OverdubNoteReady? NoteReady)
    {
        public long FrameCount => EndFrame - StartFrame;
    }

    private readonly record struct HeldNote(long ReceivedFrame, byte Velocity);
}
