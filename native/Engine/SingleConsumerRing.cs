namespace Signal.Native.Engine;

// Producers must be serialized by the caller. Readers and writers never take a shared lock.
internal sealed class SingleConsumerRing<T>(int capacity) where T : struct
{
    private readonly T[] _buffer = new T[capacity + 1];
    private int _read;
    private int _write;
    private int _published;

    public bool IsEmpty => Volatile.Read(ref _read) == Volatile.Read(ref _published);

    public bool TryWrite(T value, bool publish = true)
    {
        var write = _write;
        var next = Advance(write);
        if (next == Volatile.Read(ref _read))
        {
            return false;
        }

        _buffer[write] = value;
        _write = next;
        if (publish)
            PublishWrites();
        return true;
    }

    // Only the producer moves these cursors. Readers can never observe canceled writes.
    public void PublishWrites() => Volatile.Write(ref _published, _write);
    public void DiscardUnpublishedWrites() => _write = _published;

    public bool TryRead(out T value)
    {
        var read = _read;
        if (read == Volatile.Read(ref _published))
        {
            value = default;
            return false;
        }

        value = _buffer[read];
        Volatile.Write(ref _read, Advance(read));
        return true;
    }

    private int Advance(int index) => index + 1 == _buffer.Length ? 0 : index + 1;
}
