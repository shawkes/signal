namespace Signal.Native.Engine;

// Producers must be serialized by the caller. Readers and writers never take a shared lock.
internal sealed class SingleConsumerRing<T>(int capacity) where T : struct
{
    private readonly T[] _buffer = new T[capacity + 1];
    private int _read;
    private int _write;

    public bool TryWrite(T value)
    {
        var write = _write;
        var next = Advance(write);
        if (next == Volatile.Read(ref _read))
        {
            return false;
        }

        _buffer[write] = value;
        Volatile.Write(ref _write, next);
        return true;
    }

    public bool TryRead(out T value)
    {
        var read = _read;
        if (read == Volatile.Read(ref _write))
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
