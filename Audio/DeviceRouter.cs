using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBtOut.Audio;

/// <summary>
/// Owns one target output endpoint: builds a WasapiOut at the device's native shared-mode
/// mix format, drains its own private ring from the <see cref="FanOutTap"/>, applies
/// per-device volume, and self-heals across Bluetooth disconnects / buffer invalidations
/// with exponential back-off. A Bluetooth failure on one device never touches the capture
/// stream or the other devices.
/// </summary>
public sealed class DeviceRouter : IDisposable
{
    /// <summary>Initial WASAPI buffer duration. 40 ms is a good lip-sync/robustness tradeoff for BT.</summary>
    public const int DefaultLatencyMs = 40;
    private const int RetryDelayMs = 1500;
    private const int MaxStartAttempts = 4;
    private static readonly int[] LatencyCandidates = { 40, 60, 100 };

    private readonly FanOutTap _tap;
    private readonly MMDevice _device;
    private readonly System.Timers.Timer _reconnectTimer;
    private readonly CancellationTokenSource _disposeCts = new();
    private WasapiOut? _player;
    private FanOutSampleProvider? _provider;
    private SingleProducerRingBuffer? _ring; // this device's private slice of the capture stream
    private float _desiredVolume = 1f;
    private int _latencyIndex;
    private int _starting;   // 0/1 re-entrancy guard around StartAsync
    private bool _disposed;

    public DeviceRouter(FanOutTap tap, MMDevice device)
    {
        _tap = tap ?? throw new ArgumentNullException(nameof(tap));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        Id = device.ID;
        DisplayName = device.FriendlyName;

        _reconnectTimer = new System.Timers.Timer(RetryDelayMs) { AutoReset = false };
        _reconnectTimer.Elapsed += (_, _) => _ = TryStartAsync();
    }

    /// <summary>WASAPI endpoint id (stable across reconnects for the same physical device).</summary>
    public string Id { get; }

    /// <summary>Friendly name shown in the UI and health panel.</summary>
    public string DisplayName { get; }

    /// <summary>True while a WasapiOut is actively rendering.</summary>
    public bool IsHealthy { get; private set; }

    /// <summary>Human-readable lifecycle state for the health panel.</summary>
    public string Status { get; private set; } = "idle";

    /// <summary>WASAPI buffer duration currently in effect (ms).</summary>
    public int LatencyMs { get; private set; } = DefaultLatencyMs;

    /// <summary>Per-device stream volume 0..1 (persists across reconnects).</summary>
    public float Volume
    {
        get => _provider?.Gain ?? _desiredVolume;
        set
        {
            _desiredVolume = Math.Clamp(value, 0f, 1f);
            if (_provider is not null)
            {
                _provider.Gain = _desiredVolume;
            }
        }
    }

    /// <summary>Raised on state changes (any thread) for the health panel.</summary>
    public event Action<DeviceRouter, string>? StatusChanged;

    /// <summary>Raised when a device gives up for good (non-transient failure).</summary>
    public event Action<DeviceRouter, Exception>? FatalFailure;

    /// <summary>Applies a new stream volume (0..1); survives reconnects.</summary>
    public void SetVolume(float volume) => Volume = volume;

    /// <summary>
    /// Starts (or restarts) rendering, retrying transient failures — device just connected,
    /// format negotiation hiccups, endpoint briefly invalidated — with back-off. Throws
    /// after <see cref="MaxStartAttempts"/> consecutive transient failures (e.g. the device
    /// is unplugged/powered off) so callers are never left waiting forever.
    /// </summary>
    public async Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _starting, 1, 0) != 0)
        {
            return; // a start/reconnect is already in flight
        }

        try
        {
            SetStatus("starting…");
            int attempt = 0;
            while (true)
            {
                ThrowIfDisposed();
                try
                {
                    BuildAndStart();
                    IsHealthy = true;
                    SetStatus($"streaming ({LatencyMs} ms)");
                    return;
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < MaxStartAttempts - 1)
                {
                    // Bluetooth endpoints in particular often need a moment after pairing
                    // and can reject the first format negotiation.
                    _latencyIndex = Math.Min(_latencyIndex + 1, LatencyCandidates.Length - 1);
                    int delay = Math.Min(RetryDelayMs << Math.Min(attempt, 3), 8000);
                    SetStatus($"retrying in {delay / 1000.0:0.#}s ({FirstLine(ex.Message)})");
                    await Task.Delay(delay, _disposeCts.Token).ConfigureAwait(false);
                    attempt++;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _starting, 0);
        }
    }

    private void BuildAndStart()
    {
        ThrowIfDisposed();

        // Target the device's own shared-mode mix format (rate + channel count). WASAPI
        // shared mode then resamples/remaps whatever we feed it, so each earbud gets its
        // native layout regardless of the source format. NOTE: MMDevice.AudioClient is a
        // get-only property that creates a NEW unowned COM AudioClient on every access in
        // NAudio 2.2.1 (CreateAudioClient() only exists in 3.x) — so we must dispose the
        // probe ourselves; WasapiOut creates and owns its own client.
        WaveFormat targetFormat;
        using (AudioClient probe = _device.AudioClient)
        {
            targetFormat = probe.MixFormat;
        }

        LatencyMs = LatencyCandidates[_latencyIndex];

        // Claim this device's private consumer ring. From now on the capture thread
        // broadcasts every slice into it until the graph is disposed.
        SingleProducerRingBuffer ring = _tap.RegisterSink();
        _ring = ring;

        try
        {
            var provider = new FanOutSampleProvider(_tap, ring, targetFormat.SampleRate, targetFormat.Channels)
            {
                Gain = _desiredVolume,
            };

            // useEventSync: true -> the render thread waits on the WASAPI event handle instead
            // of sleeping, which is the lowest-latency, lowest-CPU shared-mode configuration.
            var player = new WasapiOut(_device, AudioClientShareMode.Shared, useEventSync: true, LatencyMs);

            if (_disposed)
            {
                player.Dispose();
                throw new OperationCanceledException("router disposed during start");
            }

            _provider = provider;
            _player = player;
            player.PlaybackStopped += OnPlaybackStopped;
            player.Init(provider);
            player.Play();
        }
        catch
        {
            // Release the ring (and any partially built graph) so a retry cannot leak
            // rings or WasapiOut COM clients.
            DisposeGraph();
            throw;
        }
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        // e.Exception != null => device invalidated / COM failure (typical BT drop).
        // e.Exception == null  => the source provider "ended" — cannot happen here because
        //                         FanOutSampleProvider never returns 0 frames (silence-fill),
        //                         but treat it the same way: rebuild the graph.
        ScheduleReconnect(e.Exception);
    }

    private void ScheduleReconnect(Exception? reason)
    {
        if (_disposed || _reconnectTimer.Enabled)
        {
            return;
        }

        IsHealthy = false;
        DisposeGraph();
        SetStatus(reason is null
            ? "reconnecting…"
            : $"reconnecting ({FirstLine(reason.Message)})…");
        try
        {
            _reconnectTimer.Start();
        }
        catch (ObjectDisposedException)
        {
            // racing Dispose
        }
    }

    private async Task TryStartAsync()
    {
        try
        {
            await StartAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // disposed while a start/retry was pending
        }
        catch (Exception ex)
        {
            SetStatus($"failed: {FirstLine(ex.Message)}");
            FatalFailure?.Invoke(this, ex);
        }
    }

    private void DisposeGraph()
    {
        WasapiOut? player = Interlocked.Exchange(ref _player, null);
        Interlocked.Exchange(ref _provider, null);

        // Stop feeding this device before tearing down its renderer. The producer holds a
        // lock-free snapshot, so unregistering is safe even from a render/cleanup thread.
        SingleProducerRingBuffer? ring = Interlocked.Exchange(ref _ring, null);
        if (ring is not null)
        {
            try { _tap.UnregisterSink(ring); } catch { /* tap already gone */ }
        }

        if (player is not null)
        {
            player.PlaybackStopped -= OnPlaybackStopped;
            try { player.Stop(); } catch { /* already dead */ }
            try { player.Dispose(); } catch { /* already dead */ }
        }
        // provider holds no native resources
    }

    private static bool IsTransient(Exception ex)
    {
        return ex is COMException                     // AUDCLNT_E_* device-invalidated, device-in-use, etc.
            or NotSupportedException                  // format rejected this attempt
            or InvalidOperationException              // endpoint properties unavailable mid-teardown
            or UnauthorizedAccessException
            or TimeoutException;
    }

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(DeviceRouter));
        }
    }

    private static string FirstLine(string text)
    {
        int cut = text.IndexOfAny(['\r', '\n']);
        return cut > 0 ? text[..cut] : text;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reconnectTimer.Stop();
        _reconnectTimer.Dispose();
        _disposeCts.Cancel();
        DisposeGraph();
        _disposeCts.Dispose();
        SetStatus("disposed");
    }
}
