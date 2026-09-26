using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MultiBtOut.Audio;

/// <summary>
/// System-wide loopback capture: the shared-mode capture client plus the
/// AUDCLNT_STREAMFLAGS_LOOPBACK flag. Deliberately pull-driven (no event callback) —
/// WASAPI does not support event-driven loopback capture (the event handle would never
/// signal), so the base class's sleep loop is the correct model. A 40 ms endpoint buffer
/// (half of NAudio's 100 ms default) bounds each packet's age to ~20 ms for lip-sync.
/// </summary>
internal sealed class SystemLoopbackCapture : WasapiCapture
{
    public SystemLoopbackCapture(MMDevice renderDevice)
        : base(renderDevice, useEventSync: false, audioBufferMillisecondsLength: 40)
    {
    }

    protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
        AudioClientStreamFlags.Loopback | base.GetAudioClientStreamFlags();
}

/// <summary>
/// Top-level orchestrator:
///
///   [App render engine]  -->  SystemLoopbackCapture (default render endpoint, loopback)
///                                   |  DataAvailable (WASAPI capture thread)
///                                   v
///                             FanOutTap (lock-free SPSC ring buffer, ~300 ms)
///                                   |  one independent reader per device
///          +------------------------+------------------------+
///          v                        v                        v
///    FanOutSampleProvider     FanOutSampleProvider     FanOutSampleProvider
///          |                        |                        |
///      WasapiOut #1             WasapiOut #2             WasapiOut #3   (BT earbuds...)
///
/// The capture side never blocks on a slow output: if a ring write would overwrite
/// unread data the slice is dropped (overrun counter incremented) and every renderer
/// continues from the next slice boundary. Conversely, if all consumers drain faster
/// than capture produces, renderers emit silence (underrun) instead of starving.
/// </summary>
public sealed class RouterEngine : IDisposable
{
    // NOTE: no shared MMDeviceEnumerator field on purpose. WASAPI COM objects created on
    // the STA UI thread cannot be QueryInterface'd from MTA worker threads (E_NOINTERFACE
    // — the classic apartment mismatch), so every COM object this engine touches is
    // created and used on worker threads only.
    private readonly List<DeviceRouter> _routers = new();
    private readonly object _stateGate = new();

    private WasapiCapture? _capture;
    private FanOutTap? _tap;
    private System.Timers.Timer? _healthTimer;
    private IReadOnlyList<string>? _lastDeviceIds;
    private IReadOnlyDictionary<string, float>? _lastVolumes;
    private long _lastOverruns;
    private int _restarting;
    private bool _disposed;

    public bool IsRunning { get; private set; }

    /// <summary>One-line diagnostics for the log panel (any thread).</summary>
    public event Action<string>? Log;

    /// <summary>Raised roughly once per second so the UI can refresh device health.</summary>
    public event Action? HealthSample;

    /// <summary>All currently active render endpoints (wired, Bluetooth, virtual, ...).</summary>
    public static List<MMDevice> GetActiveRenderDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
    }

    /// <summary>
    /// Applies a stream volume (0..1) to one routed device, identified by WASAPI endpoint
    /// id. Safe to call any time; no-ops while that device is not routed.
    /// </summary>
    public void SetDeviceVolume(string deviceId, float volume)
    {
        lock (_stateGate)
        {
            DeviceRouter? router = _routers.FirstOrDefault(r => r.Id == deviceId);
            router?.SetVolume(volume);
        }
    }

    /// <summary>Routers of the current session (empty when stopped).</summary>
    public IReadOnlyList<DeviceRouter> Routers
    {
        get
        {
            lock (_stateGate)
            {
                return _routers.ToArray();
            }
        }
    }

    /// <summary>One-line ring buffer summary for the health panel.</summary>
    public string? GetTapSummary()
    {
        FanOutTap? tap = _tap;
        if (tap is null)
        {
            return null;
        }

        double bufferedMs = tap.SourceFormat.AverageBytesPerSecond > 0
            ? 1000.0 * tap.PeakBufferedBytes / tap.SourceFormat.AverageBytesPerSecond
            : 0;
        return $"rings (per device) peak {bufferedMs:F0} ms · overruns {tap.OverrunCount} · underruns {tap.UnderflowCount}";
    }

    /// <summary>
    /// Starts system-wide loopback capture and mirrors it to every selected device.
    /// <paramref name="selectedDeviceIds"/> are WASAPI endpoint IDs (not MMDevice objects —
    /// see the apartment note in the implementation); <paramref name="initialVolumes"/>
    /// maps endpoint id -> volume (0..1) so sliders set before starting take effect
    /// immediately.
    ///
    /// Throws only on fatal setup errors (no default render device, or no selected device
    /// could start); per-device problems are contained inside <see cref="DeviceRouter"/>.
    /// </summary>
    public async Task StartAsync(
        IReadOnlyList<string> selectedDeviceIds,
        IReadOnlyDictionary<string, float>? initialVolumes = null)
    {
        ArgumentNullException.ThrowIfNull(selectedDeviceIds);
        if (selectedDeviceIds.Count == 0)
        {
            throw new ArgumentException("Select at least one output device.", nameof(selectedDeviceIds));
        }

        lock (_stateGate)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("Routing is already running.");
            }

            IsRunning = true;
            _lastDeviceIds = selectedDeviceIds;
            _lastVolumes = initialVolumes;
        }

        try
        {
            // 1) Resolve every WASAPI COM object on an MTA worker thread. COM objects born
            //    on the STA UI thread cannot be QueryInterface'd from worker threads
            //    (E_NOINTERFACE — the apartment mismatch behind "Unable to cast COM object
            //    ... to interface type"), so the UI passes endpoint ID strings and this
            //    engine materializes fresh MMDevice objects here. Using MTA-born objects
            //    from other MTA threads afterwards IS supported (verified empirically).
            (MMDevice Source, Dictionary<string, MMDevice> Targets) resolved = await Task.Run(() =>
            {
                using var enumerator = new MMDeviceEnumerator();
                MMDevice source = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
                var targets = new Dictionary<string, MMDevice>();
                foreach (string id in selectedDeviceIds)
                {
                    try
                    {
                        targets[id] = enumerator.GetDevice(id);
                    }
                    catch (Exception ex)
                    {
                        Log?.Invoke($"Skipping unavailable endpoint: {FirstLine(ex.Message)}");
                    }
                }

                return (source, targets);
            }).ConfigureAwait(false);

            // 2) Capture source: loopback of the system default render endpoint — this is
            //    literally the post-mix audio VLC/Chrome/Netflix send to the speakers.
            var capture = new SystemLoopbackCapture(resolved.Source);
            WasapiCapture? previousCapture = Interlocked.Exchange(ref _capture, capture);
            previousCapture?.Dispose();

            // 3) One fan-out tap; ~300 ms of headroom per sink at the source bitrate.
            WaveFormat sourceFormat = capture.WaveFormat;
            var tap = new FanOutTap(sourceFormat, Math.Max(4096, 300 * sourceFormat.AverageBytesPerSecond / 1000));
            _tap = tap;

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            // 4) One self-healing router per selected device.
            lock (_stateGate)
            {
                foreach (MMDevice device in resolved.Targets.Values)
                {
                    var router = new DeviceRouter(tap, device);
                    if (initialVolumes is not null && initialVolumes.TryGetValue(device.ID, out float volume))
                    {
                        router.SetVolume(volume);
                    }
                    router.FatalFailure += (r, ex) => Log?.Invoke($"{r.DisplayName}: giving up — {FirstLine(ex.Message)}");
                    _routers.Add(router);
                }
            }

            // 5) Health sampler (overrun/underrun visibility) at 1 Hz.
            var healthTimer = new System.Timers.Timer(1000) { AutoReset = true };
            healthTimer.Elapsed += (_, _) => SampleHealth();
            _healthTimer = healthTimer;
            healthTimer.Start();

            // 6) Bring up every renderer concurrently. One dead device must not kill the
            //    session for the others — contain per-router failures and carry on.
            DeviceRouter[] routers = Routers.ToArray();
            bool[] started = await Task.WhenAll(routers.Select(async router =>
            {
                try
                {
                    await router.StartAsync().ConfigureAwait(false);
                    return true;
                }
                catch (Exception ex)
                {
                    Log?.Invoke($"{router.DisplayName}: unavailable — {FirstLine(ex.Message)}");
                    return false;
                }
            })).ConfigureAwait(false);

            if (started.All(ok => !ok))
            {
                throw new InvalidOperationException("None of the selected devices could be started.");
            }

            // 7) Open the capture tap only after renderers exist, so the ring cannot
            //    overrun into an empty reader set during startup.
            capture.StartRecording();

            Log?.Invoke($"Mirroring '{sourceFormat}' → {string.Join(" + ", routers.Select(r => r.DisplayName))}");
        }
        catch
        {
            await StopCoreAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync() => await StopCoreAsync().ConfigureAwait(false);

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        FanOutTap? tap = _tap;
        if (tap is null)
        {
            return;
        }

        if (!tap.TryBroadcast(e.Buffer.AsSpan(0, e.BytesRecorded)))
        {
            // Every sink's ring was full (all stalled — rare): drop the whole slice.
            // Normally only the stalled sink's ring is full and it drops just its own copy.
            Log?.Invoke("Ring overrun — dropped a capture slice (a sink is too slow?)");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed || !IsRunning)
        {
            return; // deliberate Stop — no restart
        }

        if (e.Exception is not null)
        {
            Log?.Invoke($"Capture stopped ({FirstLine(e.Exception.Message)}) — restarting…");
        }

        // Default-device changes and driver hiccups kill the capture thread; rebuild the
        // whole graph automatically so the user never has to babysit it.
        _ = Task.Run(async () =>
        {
            try
            {
                await RestartAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log?.Invoke($"Automatic restart failed: {FirstLine(ex.Message)}");
            }
        });
    }

    private async Task RestartAsync()
    {
        if (Interlocked.CompareExchange(ref _restarting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            IReadOnlyList<string>? deviceIds = _lastDeviceIds;
            if (deviceIds is null || deviceIds.Count == 0 || _disposed)
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false); // let the endpoint settle
            // Re-apply the volumes the user had dialed in before the interruption.
            await StartAsync(deviceIds, _lastVolumes).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _restarting, 0);
        }
    }

    private void SampleHealth()
    {
        FanOutTap? tap = _tap;
        if (tap is not null)
        {
            long overruns = tap.OverrunCount;
            if (overruns != Interlocked.Read(ref _lastOverruns))
            {
                Log?.Invoke($"Ring overruns so far: {overruns} (a consumer is falling behind)");
                Interlocked.Exchange(ref _lastOverruns, overruns);
            }
        }

        HealthSample?.Invoke();
    }

    private async Task StopCoreAsync()
    {
        lock (_stateGate)
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
        }

        WasapiCapture? capture = Interlocked.Exchange(ref _capture, null);
        if (capture is not null)
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;
            try { capture.StopRecording(); } catch { /* device already gone */ }
        }

        // Dispose routers in parallel — WasapiOut.Stop joins its play thread.
        // ConfigureAwait(false): this method is also called from UI-thread Dispose,
        // and continuations must not deadlock on a blocked dispatcher.
        List<DeviceRouter> routers;
        lock (_stateGate)
        {
            routers = new List<DeviceRouter>(_routers);
            _routers.Clear();
        }

        if (routers.Count > 0)
        {
            await Task.WhenAll(routers.Select(r => Task.Run(r.Dispose))).ConfigureAwait(false);
        }

        if (capture is not null)
        {
            try { capture.Dispose(); } catch { /* device already gone */ }
        }

        _ = Interlocked.Exchange(ref _tap, null);
        _healthTimer?.Stop();
        _healthTimer?.Dispose();
        _healthTimer = null;
        Log?.Invoke("Routing stopped.");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            StopCoreAsync().GetAwaiter().GetResult();
        }
        catch
        {
            // best effort during shutdown
        }
    }

    private static string FirstLine(string text)
    {
        int cut = text.IndexOfAny(['\r', '\n']);
        return cut > 0 ? text[..cut] : text;
    }
}
