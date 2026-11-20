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
    /// <summary>
    /// Estimated milliseconds from MIDI receipt to the scheduled live synth attack on the audio
    /// presentation timeline. Recorded by the placement test, before recording offset or quantization.
    /// </summary>
    public double? MonitorDelayMilliseconds { get; init; }
    public int DestinationTrack { get; init; } = -1;

    public SongNote ToSongNote() => new(StartTick, LengthTicks, Channel, NoteNumber, Velocity);
}

internal sealed class Recording(TempoMap tempoMap, int sampleRate)
{
    private const int NotesPerChannel = 128;
    private const int NoteCapacity = 2048;
    private readonly HeldNote[] _held = new HeldNote[16 * NotesPerChannel];
    private readonly SingleConsumerRing<RecordedNote> _notes = new(NoteCapacity);
    private readonly TempoMap _defaultTempoMap = tempoMap;
    private RecordingPlacement _placement = new(tempoMap, sampleRate, 0, 0);
    private long? _songOriginFrame;
    private long _firstAcceptedFrame;
    private bool _pendingDownbeat;
    private HeldRecordingNotes? _preview;
    private OverdubNoteReady? _noteReady;
    private long _takeId;
    private long _droppedNotes;

    public long DroppedNotes => Volatile.Read(ref _droppedNotes);

    public void Start(long atFrame, long takeId, long offsetFrames, int quantizationDivision) =>
        StartTake(atFrame, takeId, new(_defaultTempoMap, sampleRate, offsetFrames, quantizationDivision), null, null);

    public void Start(long firstSongFrame, long armedFrame, bool countIn, long takeId,
        HeldRecordingNotes preview, OverdubNoteReady? noteReady)
    {
        StartTake(firstSongFrame, takeId, preview.Placement, preview, noteReady);
        _firstAcceptedFrame = countIn
            ? Math.Max(armedFrame, _placement.EarliestDownbeatReceipt(firstSongFrame, preview.Loop?.Range.StartTick ?? 0))
            : armedFrame;
        _pendingDownbeat = countIn && _firstAcceptedFrame < firstSongFrame;
    }

    private void StartTake(long atFrame, long takeId, RecordingPlacement placement,
        HeldRecordingNotes? preview, OverdubNoteReady? noteReady)
    {
        Stop(atFrame);
        _placement = placement;
        _songOriginFrame = preview is null ? null : atFrame;
        _firstAcceptedFrame = long.MinValue;
        _preview = preview;
        _noteReady = noteReady;
        _takeId = takeId;
    }

    public void Stop(long frame)
    {
        _noteReady = null;
        // Canceling the count-in discards early complete and still-held notes, even when
        // their offset would have put them into the song.
        if (_pendingDownbeat && frame >= _songOriginFrame)
            ReachDownbeat();
        FinishAll(frame);
        if (_pendingDownbeat)
            _notes.DiscardUnpublishedWrites();
        _pendingDownbeat = false;
        _takeId = 0;
        _preview = null;
    }

    public void ReachDownbeat()
    {
        if (!_pendingDownbeat)
            return;
        _pendingDownbeat = false;
        _notes.PublishWrites();
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

    public void ProcessMidi(MidiMessage message, long frame, long synthFrame, int destinationTrack = -1)
    {
        if (_takeId == 0 || frame < _firstAcceptedFrame)
        {
            return;
        }

        var channelStart = (message.Status & 0x0f) * NotesPerChannel;
        var key = channelStart + message.Data1;
        switch (message.Status & 0xf0)
        {
            case 0x90 when message.Data2 != 0:
                Finish(key, frame);
                _held[key] = new HeldNote(frame, message.Data2, synthFrame, destinationTrack);
                _preview?.Press(key, frame - _songOriginFrame.GetValueOrDefault(), destinationTrack);
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

    public bool HasUnreadNotes => !_notes.IsEmpty;

    private void Finish(int key, long releaseFrame)
    {
        var held = _held[key];
        if (held.Velocity == 0)
        {
            return;
        }

        _held[key] = default;
        var playedLengthFrames = Math.Max(0, releaseFrame - held.ReceivedFrame);
        if (_preview?.Loop is { } loop)
        {
            FinishLooped(key, held, playedLengthFrames, loop);
            _preview.Release(key);
            return;
        }

        bool onSongTimeline = _songOriginFrame.HasValue;
        var relativeReceipt = held.ReceivedFrame - _songOriginFrame.GetValueOrDefault();
        var compensatedStart = relativeReceipt - _placement.OffsetFrames;
        var compensatedTick = _placement.Tempo.SampleToTick(compensatedStart, sampleRate);
        // A tempo change can fall inside the played interval. Measure its duration before moving the note.
        var durationStart = onSongTimeline ? relativeReceipt : compensatedStart;
        var lengthTicks = _placement.TickAt(durationStart + playedLengthFrames) - _placement.TickAt(durationStart);
        var (beat, beatFrame) = NearestBeat(compensatedStart, compensatedTick, onSongTimeline);

        var note = new RecordedNote(
            _takeId, _placement.StartTick(relativeReceipt), lengthTicks,
            (byte)(key / NotesPerChannel), (byte)(key % NotesPerChannel), held.Velocity,
            held.ReceivedFrame, held.ReceivedFrame - _placement.OffsetFrames, playedLengthFrames, beat,
            (compensatedStart - beatFrame) * 1000.0 / sampleRate,
            (relativeReceipt - beatFrame) * 1000.0 / sampleRate)
        {
            MonitorDelayMilliseconds = onSongTimeline ? null : (held.SynthFrame - held.ReceivedFrame) * 1000.0 / sampleRate,
            DestinationTrack = held.DestinationTrack
        };

        PublishNote(note);
        _preview?.Release(key);
    }

    private void FinishLooped(int key, HeldNote held, long lengthFrames, RecordingLoop loop)
    {
        long capturedAttack = held.ReceivedFrame - _songOriginFrame.GetValueOrDefault();
        long capturedRelease = capturedAttack + lengthFrames;
        long completedPass = Math.Max(0, capturedAttack + Math.Max(0, lengthFrames - 1)) / loop.FrameCount;
        var (sourceAttack, rawAttackTick, duration) = loop.Measure(_placement, capturedAttack, capturedRelease);
        var (attackTick, compensatedSource, placedPass) = loop.Place(_placement, capturedAttack);
        long firstLength = (long)Int128.Min(duration, loop.Range.EndTick - attackTick);
        var first = Segment(attackTick, firstLength, 0);
        PublishNote(first, _noteReady, ReplayAfter(0));

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
                PublishNote(whole, _noteReady, ReplayAfter(consumed));

            remaining %= loop.TickCount;
            consumed = duration - remaining;
        }

        if (remaining > 0)
        {
            var last = Segment(loop.Range.StartTick, (long)remaining, consumed);
            var lastNote = last.ToSongNote();
            if (lastNote != first.ToSongNote() && lastNote != wholeLoop)
                PublishNote(last, _noteReady, ReplayAfter(consumed));
        }

        long ReplayAfter(Int128 precedingTicks)
        {
            // A negative offset can move a note forward by several short passes. Each
            // replay segment must wait for its compensated pass and for the played release.
            Int128 pass = placedPass + (attackTick - loop.Range.StartTick + precedingTicks) / loop.TickCount;
            return Math.Max(completedPass, long.CreateSaturating(pass - 1));
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
                precedingTicks == 0 ? compensatedSource : loop.StartFrame, precedingTicks == 0 ? _placement.OffsetFrames : 0);
        }

        long FrameAt(Int128 elapsedTicks)
        {
            Int128 ticks = elapsedTicks + (rawAttackTick - loop.Range.StartTick);
            if (ticks < 0)
            {
                long relativeFrame = (long)Math.Round((double)ticks * loop.CountInFramesPerTick(_placement), MidpointRounding.AwayFromZero);
                return Math.Clamp(relativeFrame - capturedAttack, 0, lengthFrames);
            }
            long tick = loop.Range.StartTick + (long)(ticks % loop.TickCount);
            long source = tick == loop.Range.StartTick ? loop.StartFrame : _placement.Tempo.TickToSample(tick, sampleRate);
            Int128 elapsed = ticks / loop.TickCount * loop.FrameCount + source - sourceAttack;
            return (long)Int128.Clamp(elapsed, 0, lengthFrames);
        }
    }

    private RecordedNote LoopNote(int key, HeldNote held, long startTick, long lengthTicks,
        long lengthFrames, long compensatedStart, long offset)
    {
        var (beat, beatFrame) = NearestBeat(compensatedStart,
            _placement.Tempo.SampleToTick(compensatedStart, sampleRate), clampToSong: true);
        return new RecordedNote(
            _takeId, startTick, lengthTicks,
            (byte)(key / NotesPerChannel), (byte)(key % NotesPerChannel), held.Velocity,
            held.ReceivedFrame, held.ReceivedFrame - offset, lengthFrames, beat,
            (compensatedStart - beatFrame) * 1000.0 / sampleRate,
            (compensatedStart - beatFrame + offset) * 1000.0 / sampleRate)
        {
            DestinationTrack = held.DestinationTrack
        };
    }

    private (long Beat, long Frame) NearestBeat(long sample, double tick, bool clampToSong)
    {
        var beat = (long)Math.Floor(tick / _placement.Tempo.TicksPerQuarter);
        if (clampToSong)
            beat = Math.Max(0, beat);

        var beatFrame = BeatFrame(beat);
        var nextBeatFrame = BeatFrame(beat + 1);
        return nextBeatFrame - sample <= sample - beatFrame ? (beat + 1, nextBeatFrame) : (beat, beatFrame);
    }

    private void PublishNote(RecordedNote note, OverdubNoteReady? noteReady = null, long completedPass = 0)
    {
        if (_notes.TryWrite(note, publish: !_pendingDownbeat))
        {
            noteReady?.Invoke(note, completedPass);
        }
        else
        {
            Interlocked.Increment(ref _droppedNotes);
        }
    }

    private long BeatFrame(long beat) => _placement.Tempo.TickToSample((double)beat * _placement.Tempo.TicksPerQuarter, sampleRate);

    private readonly record struct HeldNote(long ReceivedFrame, byte Velocity, long SynthFrame, int DestinationTrack);
}
