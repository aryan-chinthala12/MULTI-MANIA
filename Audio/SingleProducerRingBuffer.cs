using System;
using System.Threading;

namespace MultiBtOut.Audio;

/// <summary>
/// Single-producer / single-consumer byte ring buffer with a power-of-two capacity.
///
/// Producer = the WASAPI capture thread (DataAvailable).
/// Consumer = ONE sink's FanOutSampleProvider.Read, driven by that device's WasapiOut
/// render thread. Each routed device owns a private instance (handed out by
/// FanOutTap.RegisterSink), so consumers never interact.
///
/// Synchronization model: exactly one thread writes <see cref="_head"/>, exactly one thread
/// writes <see cref="_tail"/>, both are aligned 32-bit ints (atomic reads/writes on all
/// supported runtimes), and capacity is a power of two so index masking needs no division.
/// That yields a data-race-free FIFO with zero locks and zero allocations on the hot path.
/// One byte slot is always left unused so "full" and "empty" are distinguishable.
/// </summary>
public sealed class SingleProducerRingBuffer
{
    private readonly byte[] _buffer;
    private readonly int _mask;
    private int _head; // advanced by the producer only
    private int _tail; // advanced by the consumer only
    private long _overruns;

    public SingleProducerRingBuffer(int capacityBytes)
    {
        if (capacityBytes < 4096)
        {
            capacityBytes = 4096;
        }

        int size = 1;
        while (size < capacityBytes)
        {
            size <<= 1;
        }

        _buffer = new byte[size];
        _mask = size - 1;
    }

    /// <summary>Total storage in bytes (always a power of two).</summary>
    public int Capacity => _buffer.Length;

    /// <summary>Bytes currently buffered and not yet consumed.</summary>
    public int BufferedBytes
    {
        get
        {
            // Snapshot with volatile-style ordering: producer may bump _head mid-read,
            // which only ever makes the computed fill *larger* or *smaller* by one
            // slice — fine for a health metric.
            int head = Volatile.Read(ref _head);
            int tail = Volatile.Read(ref _tail);
            return (head - tail) & _mask;
        }
    }

    /// <summary>Number of write attempts rejected because the buffer was full.</summary>
    public long OverrunCount => Interlocked.Read(ref _overruns);

    /// <summary>
    /// Writes the whole slice atomically (all-or-nothing). Returns false when there is
    /// not enough free space; the caller decides the drop policy (we drop the slice so a
    /// slow Bluetooth sink can never corrupt the stream for the healthy sinks).
    /// </summary>
    public bool TryWrite(ReadOnlySpan<byte> slice)
    {
        if (slice.Length == 0)
        {
            return true;
        }

        if (slice.Length > Capacity - 1)
        {
            return false; // can never fit, don't bother
        }

        int head = _head;
        int tail = Volatile.Read(ref _tail);
        int used = (head - tail) & _mask;
        int free = _buffer.Length - 1 - used;
        if (slice.Length > free)
        {
            Interlocked.Increment(ref _overruns);
            return false;
        }

        int offset = head & _mask;
        int firstChunk = Math.Min(slice.Length, _buffer.Length - offset);
        slice[..firstChunk].CopyTo(_buffer.AsSpan(offset));
        if (firstChunk < slice.Length)
        {
            slice[firstChunk..].CopyTo(_buffer);
        }

        // Publish after the data. On x86/x64 and ARM (with the runtime's memory model
        // for int writes on the publishing side) consumers observe data before head.
        Volatile.Write(ref _head, head + slice.Length);
        return true;
    }

    /// <summary>
    /// Reads up to <paramref name="destination.Length"/> bytes. Returns the number of
    /// bytes actually read (0 when empty). Never returns a partial slice in practice
    /// because every producer slice is a whole number of PCM frames; callers assert
    /// full reads.
    /// </summary>
    public int Read(Span<byte> destination)
    {
        if (destination.IsEmpty)
        {
            return 0;
        }

        int head = Volatile.Read(ref _head);
        int tail = _tail;
        int available = (head - tail) & _mask;
        int count = Math.Min(available, destination.Length);
        if (count == 0)
        {
            return 0;
        }

        int offset = tail & _mask;
        int firstChunk = Math.Min(count, _buffer.Length - offset);
        _buffer.AsSpan(offset, firstChunk).CopyTo(destination);
        if (firstChunk < count)
        {
            _buffer.AsSpan(0, count - firstChunk).CopyTo(destination[firstChunk..]);
        }

        Volatile.Write(ref _tail, tail + count);
        return count;
    }

    /// <summary>
    /// Drops all buffered data. Only call while the producer is quiesced
    /// (capture stopped) — e.g. when rebuilding the output graph.
    /// </summary>
    public void Clear()
    {
        Volatile.Write(ref _tail, Volatile.Read(ref _head));
    }
}
