using System.Collections.Concurrent;

namespace Signal.Native.Engine;

/// <summary>
/// Moves the engine's completed notes off its bounded audio ring while a take is active.
/// This must be the sole reader of the supplied engine. The editor can read from this
/// queue at its own pace. The growing queue allocates on the worker, never on the audio thread.
/// </summary>
public sealed class RecordedNotePump : IDisposable
{
    private readonly LatencyEngine _engine;
    private readonly ConcurrentQueue<RecordedNote> _ready = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private long _requestedTakeId;
    private long _completedTakeId;
    private bool _transferring;
    private bool _stopping;

    public RecordedNotePump(LatencyEngine engine)
    {
        _engine = engine;
        _worker = new Thread(Run) { IsBackground = true, Name = "MIDI recording" };
        _worker.Start();
    }

    /// <summary>A stopped take reaches this watermark after all of its final notes are queued.</summary>
    public long CompletedTakeId => Volatile.Read(ref _completedTakeId);

    /// <summary>Used after reading held keys to wait for released keys to reach the completed-note preview.</summary>
    public bool HasCaughtUp => !_engine.HasUnreadRecordedNotes && !Volatile.Read(ref _transferring);

    /// <summary>Wake the collector when starting a take. Start and dispose are owned by the control thread.</summary>
    public void BeginTake(long takeId)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        Volatile.Write(ref _requestedTakeId, takeId);
        _wake.Set();
    }

    public bool TryReadNote(out RecordedNote note) => _ready.TryDequeue(out note);

    /// <summary>After the audio producer stops, finish transferring its notes. The queue remains readable.</summary>
    public void Dispose()
    {
        if (_stopping)
            return;
        Volatile.Write(ref _stopping, true);
        _wake.Set();
        _worker.Join();
        _wake.Dispose();
    }

    private void Run()
    {
        while (!Volatile.Read(ref _stopping))
        {
            Transfer();
            int wait = CompletedTakeId < Volatile.Read(ref _requestedTakeId) ? 2 : Timeout.Infinite;
            _wake.WaitOne(wait);
        }
        Transfer();
    }

    private void Transfer()
    {
        // Acquire completion first. If stop races this transfer, the next one will
        // observe completion and collect any held notes that stop finished.
        long completed = _engine.CompletedTakeId;
        if (_engine.HasUnreadRecordedNotes)
        {
            Volatile.Write(ref _transferring, true);
            while (_engine.TryReadNote(out var note))
                _ready.Enqueue(note);
            Volatile.Write(ref _transferring, false);
        }
        Volatile.Write(ref _completedTakeId, completed);
    }
}
