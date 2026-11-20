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
        if (_loop is { } loop)
            _loop = loop with { NoteReady = null };

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
        long capturedAttack = held.ReceivedFrame - _songOriginFrame.GetValueOrDefault();
        long capturedRelease = capturedAttack + lengthFrames;
        long completedPass = (capturedAttack + Math.Max(0, lengthFrames - 1)) / loop.FrameCount;
        long attackPhase = capturedAttack % loop.FrameCount;
        long sourceAttack = loop.StartFrame + attackPhase;
        long rawAttackTick = LoopTick(sourceAttack, loop);
        long rawReleaseTick = LoopTick(loop.StartFrame + capturedRelease % loop.FrameCount, loop);
        long passes = capturedRelease / loop.FrameCount - capturedAttack / loop.FrameCount;
        Int128 duration = (Int128)passes * loop.TickCount + rawReleaseTick - rawAttackTick;
        duration = Int128.Max(1, duration);

        double compensatedTick = _takeTempoMap.SampleToTick(sourceAttack - _offsetFrames, sampleRate);
        long attackTick = Math.Max(loop.Range.StartTick, RoundTick(QuantizeStartTick(compensatedTick)));
        // The exclusive end is also the next pass's downbeat. A snapped attack there belongs at the start.
        if (attackTick >= loop.Range.EndTick)
            attackTick = loop.Range.StartTick;
        long firstLength = (long)Int128.Min(duration, loop.Range.EndTick - attackTick);
        var first = Segment(attackTick, firstLength, 0);
        PublishNote(first, loop.NoteReady, completedPass);

        Int128 consumed = firstLength;
        Int128 remaining = duration - consumed;
        SongNote? wholeLoop = null;
        if (remaining >= loop.TickCount)
        {
            // Consecutive full-cycle segments occupy the same edited notes on the song timeline.
            // Emit one regardless of the number of passes for which the key was held.
            var whole = Segment(loop.Range.StartTick, loop.TickCount, consumed);
            wholeLoop = whole.ToSongNote();
            if (wholeLoop != first.ToSongNote())
                PublishNote(whole, loop.NoteReady, completedPass);

            remaining %= loop.TickCount;
            consumed = duration - remaining;
        }

        if (remaining > 0)
        {
            var last = Segment(loop.Range.StartTick, (long)remaining, consumed);
            var lastNote = last.ToSongNote();
            if (lastNote != first.ToSongNote() && lastNote != wholeLoop)
                PublishNote(last, loop.NoteReady, completedPass);
        }

        RecordedNote Segment(long start, long length, Int128 precedingTicks)
        {
            // Split the measured input interval as well as the edited interval, so diagnostics
            // keep reporting the played durations even when quantization moves the split point.
            long firstFrame = precedingTicks == 0 ? 0 : FrameAt(precedingTicks);
            Int128 endingTicks = precedingTicks + length;
            long lastFrame = endingTicks == duration ? lengthFrames : FrameAt(endingTicks);
            return LoopNote(key, held with { ReceivedFrame = held.ReceivedFrame + firstFrame },
                start, length, lastFrame - firstFrame,
                precedingTicks == 0 ? sourceAttack : loop.StartFrame, precedingTicks == 0 ? _offsetFrames : 0);
        }

        long FrameAt(Int128 elapsedTicks)
        {
            Int128 ticks = elapsedTicks + (rawAttackTick - loop.Range.StartTick);
            long tick = loop.Range.StartTick + (long)(ticks % loop.TickCount);
            long source = tick == loop.Range.StartTick ? loop.StartFrame : _takeTempoMap.TickToSample(tick, sampleRate);
            Int128 elapsed = ticks / loop.TickCount * loop.FrameCount + source - sourceAttack;
            return (long)Int128.Clamp(elapsed, 0, lengthFrames);
        }
    }

    private RecordedNote LoopNote(int key, HeldNote held, long startTick, long lengthTicks,
        long lengthFrames, long sourceReceipt, long offset)
    {
        long compensatedStart = sourceReceipt - offset;
        var (beat, beatFrame) = NearestBeat(compensatedStart,
            _takeTempoMap.SampleToTick(compensatedStart, sampleRate), clampToSong: true);
        return new RecordedNote(
            _takeId, startTick, lengthTicks,
            (byte)(key / NotesPerChannel), (byte)(key % NotesPerChannel), held.Velocity,
            held.ReceivedFrame, held.ReceivedFrame - offset, lengthFrames, beat,
            (compensatedStart - beatFrame) * 1000.0 / sampleRate,
            (sourceReceipt - beatFrame) * 1000.0 / sampleRate);
    }

    private long LoopTick(long frame, LoopCapture loop)
    {
        // The loop's rounded PCM start identifies the exact starting song tick.
        if (frame == loop.StartFrame)
            return loop.Range.StartTick;

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
        public long TickCount => Range.EndTick - Range.StartTick;
    }

    private readonly record struct HeldNote(long ReceivedFrame, byte Velocity);
}
