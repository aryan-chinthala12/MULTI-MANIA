using System;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MultiBtOut.Audio;

/// <summary>
/// NAudio source that feeds one target device. Each instance drains its <b>own private
/// ring buffer</b> handed out by the <see cref="FanOutTap"/> (one ring per device, so
/// readers can never consume each other's data). Implements both
/// <see cref="ISampleProvider"/> (float API) and <see cref="IWaveProvider"/>
/// (byte API — the only overload <c>WasapiOut.Init</c> offers in NAudio 2.2.1). The byte
/// overload simply reinterprets the destination as float32 samples, so there is a single
/// read path and zero intermediate copies.
///
/// Responsibilities:
///   1. Pull whole PCM frames from the tap (float32 in, matching the loopback mix format).
///   2. Map channels from the source layout onto the device's channel count
///      (5.1/7.1 fold-down to stereo — including the center/dialogue channel, mono
///      duplication, arbitrary up-mix).
///   3. Apply the per-device volume <see cref="Gain"/> (stream-level, never the system master).
///   4. On underrun, emit silence instead of starving: WasapiOut would otherwise treat a
///      zero-length read as "end of stream" and tear down the renderer. Silence keeps the
///      WASAPI render clock and the Bluetooth link alive so playback resumes glitch-free.
/// </summary>
public sealed class FanOutSampleProvider : ISampleProvider, IWaveProvider
{
    private readonly FanOutTap _tap;
    private readonly SingleProducerRingBuffer _ring; // this device's private consumer ring
    private readonly float[] _sourceFrame;   // one interleaved source frame, reused
    private readonly int _sourceChannels;
    private float _gain = 1f;                // benign racy read across render threads

    public FanOutSampleProvider(FanOutTap tap, SingleProducerRingBuffer ring, int outputSampleRate, int outputChannels)
    {
        _tap = tap ?? throw new ArgumentNullException(nameof(tap));
        _ring = ring ?? throw new ArgumentNullException(nameof(ring));
        _sourceChannels = tap.SourceFormat.Channels;
        _sourceFrame = new float[_sourceChannels];
        // 32-bit float at the device's own rate; WASAPI shared mode (AutoConvertPcm)
        // performs the rate/format conversion to the endpoint mix format for us.
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputSampleRate, outputChannels);
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>
    /// Per-device output gain (0..1). Applied after channel mapping so balance and level
    /// are independent per earbud/headphone. Deliberately NOT WasapiOut.Volume, which
    /// would stomp the endpoint's master volume.
    /// </summary>
    public float Gain
    {
        get => _gain;
        set => _gain = Math.Clamp(value, 0f, 1f);
    }

    // ---- ISampleProvider (float API) ----

    public int Read(float[] buffer, int offset, int count) =>
        ReadCore(buffer.AsSpan(offset, count));

    // ---- IWaveProvider (byte API — the overload WasapiOut.Init actually calls) ----

    /// <summary>
    /// Byte-level bridge: this stream is always IEEE float32, so the destination bytes are
    /// reinterpreted as float samples. Returns BYTES written (IWaveProvider contract);
    /// always a whole number of frames, never zero for a non-zero request, so WasapiOut
    /// never interprets our output as "end of stream".
    /// </summary>
    public int Read(byte[] buffer, int offset, int count) =>
        ReadCore(MemoryMarshal.Cast<byte, float>(buffer.AsSpan(offset, count))) * sizeof(float);

    private int ReadCore(Span<float> dest)
    {
        int outChannels = WaveFormat.Channels;
        int framesRequested = dest.Length / outChannels;
        if (framesRequested == 0)
        {
            return 0;
        }

        if (outChannels == _sourceChannels)
        {
            // ---- Fast path: identical layout — bulk-pull straight into the destination.
            // One ring read per WasapiOut fill instead of one per frame.
            Span<byte> destBytes = MemoryMarshal.AsBytes(dest[..(framesRequested * outChannels)]);
            int bytesPerFrame = sizeof(float) * outChannels;
            int framesRead = _ring.Read(destBytes) / bytesPerFrame;

            if (framesRead < framesRequested)
            {
                // Underrun policy: keep the render clock running with silence.
                destBytes[(framesRead * bytesPerFrame)..].Clear();
                _tap.NoteUnderflow();
            }

            if (_gain != 1f && framesRead > 0)
            {
                Span<float> destSlice = dest[..(framesRead * outChannels)];
                for (int i = 0; i < destSlice.Length; i++)
                {
                    destSlice[i] *= _gain;
                }
            }
        }
        else
        {
            // ---- Slow path: channel count differs — per-frame remap.
            int framesRead = 0;
            for (; framesRead < framesRequested; framesRead++)
            {
                if (!ReadSourceFrame())
                {
                    break; // tap momentarily empty -> underrun path below
                }

                MapChannels(dest, framesRead * outChannels);
            }

            if (framesRead < framesRequested)
            {
                dest.Slice(framesRead * outChannels,
                    (framesRequested - framesRead) * outChannels).Clear();
                _tap.NoteUnderflow();
            }
        }

        // Always return the full request: silence-filled if the tap was short.
        return framesRequested * outChannels;
    }

    /// <summary>
    /// Pulls exactly one source frame from the tap. Alignment invariant: the producer only
    /// ever writes whole frames, so once BufferedBytes >= frame size a read of exactly
    /// frameBytes.Length can never be partial. Checking first guarantees the slow path
    /// never consumes a torn frame (which would permanently desync every later read).
    /// </summary>
    private bool ReadSourceFrame()
    {
        Span<byte> frameBytes = MemoryMarshal.AsBytes(_sourceFrame.AsSpan());
        if (_ring.BufferedBytes < frameBytes.Length)
        {
            return false;
        }

        return _ring.Read(frameBytes) == frameBytes.Length;
    }

    private void MapChannels(Span<float> buffer, int offset)
    {
        int outChannels = WaveFormat.Channels;

        if (_sourceChannels >= 6 && outChannels == 2)
        {
            // 5.1/7.1 -> stereo. WAVEFORMATEXTENSIBLE order: FL FR FC LFE BL BR [SL SR].
            // Keep the center (dialogue!) and a bounded rear contribution; normalize so the
            // sum cannot clip.
            const float mix = 0.7f;
            float center = _sourceChannels >= 3 ? _sourceFrame[2] : 0f;
            float k = 1f / (1f + 2f * mix);
            buffer[offset] = (_sourceFrame[0] + mix * center + mix * _sourceFrame[4]) * k * _gain;
            buffer[offset + 1] = (_sourceFrame[1] + mix * center + mix * _sourceFrame[5]) * k * _gain;
            return;
        }

        // Generic fold-down/up: alternate channels into L/R with mild attenuation for the
        // extras (prevents clipping when summing), then duplicate to however many output
        // channels the device has (mono earbuds get the average).
        float left = _sourceFrame[0];
        float right = _sourceChannels > 1 ? _sourceFrame[1] : _sourceFrame[0];
        for (int c = 2; c < _sourceChannels; c += 2)
        {
            left += 0.5f * _sourceFrame[c];
        }
        for (int c = 3; c < _sourceChannels; c += 2)
        {
            right += 0.5f * _sourceFrame[c];
        }

        if (_sourceChannels > 2)
        {
            float k = 1f / (1f + 0.35f * (_sourceChannels - 2));
            left *= k;
            right *= k;
        }

        if (outChannels == 1)
        {
            buffer[offset] = 0.5f * (left + right) * _gain;
        }
        else
        {
            buffer[offset] = left * _gain;
            for (int c = 1; c < outChannels; c++)
            {
                buffer[offset + c] = ((c & 1) == 1 ? right : left) * _gain;
            }
        }
    }
}
