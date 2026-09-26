using System;
using System.Collections.Generic;
using System.Threading;
using NAudio.Wave;

namespace MultiBtOut.Audio;

/// <summary>
/// The fan-out point of the pipeline: the loopback capture thread broadcasts every PCM
/// slice into one <b>private ring buffer per registered sink</b>, and each sink's renderer
/// drains only its own ring. Every ring is classic SPSC — producer is the WASAPI capture
/// thread, consumer is that device's WasapiOut render thread — so readers never consume
/// each other's data, and a stalled sink drops its own slices only: it can never block
/// the producer or steal/corrupt another device's stream.
/// </summary>
public sealed class FanOutTap
{
    private readonly object _gate = new();
    private readonly List<SingleProducerRingBuffer> _sinks = new();
    private SingleProducerRingBuffer[] _snapshot = Array.Empty<SingleProducerRingBuffer>();
    private long _underflows;

    /// <param name="sourceFormat">Format of the PCM flowing through this tap (loopback mix format).</param>
    /// <param name="capacityBytesPerSink">Ring capacity handed to each registered sink (bytes).</param>
    public FanOutTap(WaveFormat sourceFormat, int capacityBytesPerSink)
    {
        SourceFormat = sourceFormat;
        CapacityBytesPerSink = capacityBytesPerSink;
    }

    /// <summary>Format of the PCM flowing through this tap (the loopback mix format).</summary>
    public WaveFormat SourceFormat { get; }

    /// <summary>Ring capacity handed to each newly registered sink (bytes).</summary>
    public int CapacityBytesPerSink { get; }

    /// <summary>Registered sinks (devices currently routed).</summary>
    public int SinkCount
    {
        get { lock (_gate) { return _sinks.Count; } }
    }

    /// <summary>
    /// Registers a new sink and returns its private consumer ring. The producer consumes
    /// a lock-free snapshot, so registering mid-stream is safe; the new ring simply starts
    /// receiving from the next broadcast.
    /// </summary>
    public SingleProducerRingBuffer RegisterSink()
    {
        var ring = new SingleProducerRingBuffer(CapacityBytesPerSink);
        lock (_gate)
        {
            _sinks.Add(ring);
            _snapshot = _sinks.ToArray();
        }

        return ring;
    }

    /// <summary>Removes a sink and its ring (e.g. device disconnected or router disposed).</summary>
    public void UnregisterSink(SingleProducerRingBuffer ring)
    {
        lock (_gate)
        {
            _sinks.Remove(ring);
            _snapshot = _sinks.ToArray();
        }
    }

    /// <summary>Total slices dropped because some sink's ring was full (aggregated).</summary>
    public long OverrunCount
    {
        get
        {
            lock (_gate)
            {
                long total = 0;
                foreach (SingleProducerRingBuffer sink in _sinks)
                {
                    total += sink.OverrunCount;
                }

                return total;
            }
        }
    }

    /// <summary>Render reads that found the ring empty and had to emit silence (aggregated).</summary>
    public long UnderflowCount => Interlocked.Read(ref _underflows);

    /// <summary>Called by renderers when they had to fill with silence (underrun).</summary>
    public void NoteUnderflow() => Interlocked.Increment(ref _underflows);

    /// <summary>
    /// Called on the WASAPI capture thread for every DataAvailable packet. Copies the slice
    /// into every sink's private ring (the copy is unavoidable — each consumer needs its own
    /// cursor). Per-sink all-or-nothing: a full ring (one stalled Bluetooth sink) drops this
    /// slice for itself only; healthy sinks are unaffected and the producer never blocks.
    /// Returns true when at least one sink accepted the slice.
    /// </summary>
    public bool TryBroadcast(ReadOnlySpan<byte> pcmSlice)
    {
        if (pcmSlice.IsEmpty)
        {
            return true;
        }

        SingleProducerRingBuffer[] sinks = Volatile.Read(ref _snapshot);
        if (sinks.Length == 0)
        {
            return true;
        }

        bool delivered = false;
        foreach (SingleProducerRingBuffer ring in sinks)
        {
            delivered |= ring.TryWrite(pcmSlice);
        }

        return delivered;
    }

    /// <summary>Worst-case (highest) fill across all sink rings, in bytes (health metric).</summary>
    public int PeakBufferedBytes
    {
        get
        {
            lock (_gate)
            {
                int peak = 0;
                foreach (SingleProducerRingBuffer sink in _sinks)
                {
                    peak = Math.Max(peak, sink.BufferedBytes);
                }

                return peak;
            }
        }
    }
}
