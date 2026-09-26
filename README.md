# MultiBtOut — mirror Windows system audio to multiple Bluetooth sinks

A .NET 8 WPF application that captures **everything Windows is playing** (VLC, Chrome,
Netflix, games — anything that goes through the default render endpoint) via WASAPI
loopback and streams it **simultaneously** to several output devices — the target
scenario being 3–4 pairs of Bluetooth earbuds/headphones.

## 1. Prerequisites

- Windows 10 (build 17763+) or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (x64)
- Bluetooth adapters/sinks already paired and connected

## 2. Build & run

```bash
cd MultiBtOutVC
dotnet restore        # pulls NAudio 2.2.1 from NuGet
dotnet run -c Release
```

To produce a single-file executable:

```bash
dotnet publish -c Release -r win-x64 --self-contained false -o publish
```

The only NuGet dependency is **NAudio 2.2.1** (MIT). It provides the WASAPI bindings
(`MMDeviceEnumerator`, `WasapiLoopbackCapture`, `WasapiOut`, COM error codes).

## 3. Using the app

1. **Refresh devices** — enumerates every *active* render endpoint
   (`MMDeviceEnumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)`).
   Wired, Bluetooth and virtual (e.g. Voicemeeter) devices all appear.
2. Tick the sinks you want (3–4 Bluetooth headsets/earbuds is the design target).
3. **Start mirroring.** The app:
   - opens WASAPI loopback capture on the **default render device**
     (post-mix system audio — exactly what your speakers would play);
   - creates one independent, self-healing output graph per selected device;
   - starts capture. Play your movie.
4. Adjust the **per-device volume** sliders (0–100 %) at any time — they are applied
   in the sample stream, they do **not** touch each endpoint's system master volume.
5. The **Health & log** panel shows the ring-buffer fill in milliseconds, overrun /
   underrun counters, per-device state (`streaming (40 ms)`, `reconnecting (…)`) and
   a timestamped event log.

> **Bluetooth note (important):** A2DP Bluetooth sinks are *sink-only*. Windows will
> not let an app re-render captured system audio to the *same* endpoint that is being
> captured without creating a feedback loop. That is why the capture source is the
> default render endpoint and the targets should be **other** devices (e.g. BT earbuds
> while the default is your speakers — or any non-default sink). Selecting the default
> endpoint itself is technically possible but will feed the output back into the input.

## 4. How it works

```
[VLC/Chrome/Netflix] → Windows mixer → default render endpoint
                                            │  SystemLoopbackCapture (shared, pull-driven, 40 ms)
                                            ▼  DataAvailable on the WASAPI capture thread
                                    ┌───────────────┐
                                    │   FanOutTap   │  broadcast → one PRIVATE ring per device
                                    │               │  (lock-free SPSC, ~300 ms each)
                                    └───┬───────┬───┘
             one independent puller     │       │     each reader drains only its own ring
      ┌─────────────────┬───────────────┘       └──────────────┐
      ▼                 ▼                                      ▼
 FanOutSampleProvider  FanOutSampleProvider            FanOutSampleProvider   channel map + gain
      │                 │                                      │
 WasapiOut #1      WasapiOut #2                       WasapiOut #3/#4   (shared, event sync, 40 ms)
      ▼                 ▼                                      ▼
  BT earbuds A       BT earbuds B                     BT headphone C / Speaker
```

Key design decisions:

- **Lock-free handoff, one ring per device.** `FanOutTap` gives every routed device a
  private `SingleProducerRingBuffer` (classic SPSC ring: producer = the WASAPI capture
  thread, consumer = that device's render thread). Readers therefore never consume each
  other's data and can never stall one another; the capture thread publishes through a
  lock-free snapshot, so devices can join/leave mid-stream. Only aligned 32-bit cursor
  integers are shared; there are no locks and no allocations on the hot path.
- **Back-pressure policy.** If one Bluetooth sink stalls, only *its* ring fills up and
  only *its* copies of capture slices are dropped (all-or-nothing, never partially
  written) — every healthy sink stays sample-accurate and unaffected. If a renderer
  drains faster than capture produces, it **emits silence** for the gap; a zero-length
  read would make `WasapiOut` treat the stream as ended and tear down the renderer.
- **Native device formats.** Each `WasapiOut` is initialized at the *device's own
  shared-mode mix format* (sample rate + channel count). WASAPI shared mode
  (`AutoConvertPcm`) performs format conversion for us, so a 44.1 kHz stereo earbud
  and a 48 kHz mono headset both work from the same capture stream. Channel mapping
  (5.1/7.1 → stereo fold-down, mono duplication, up-mix) happens in
  `FanOutSampleProvider`.
- **Low latency.** Capture uses a 40 ms pull-driven loopback buffer (WASAPI does not
  support event-driven loopback — the event handle would never signal — so the sleep
  loop is the correct model) and each output runs a 40 ms event-driven shared-mode
  `WasapiOut`; on transient failures the router retries at 60/100 ms (some BT stacks
  cannot hold 40 ms under congestion). End-to-end added latency is roughly 40–80 ms.
- **Capture source.** `SystemLoopbackCapture` (an `NAudio.Wave.WasapiCapture` subclass
  adding `AUDCLNT_STREAMFLAGS_LOOPBACK`) captures the post-mix stream of the *default*
  render endpoint, with a 40 ms buffer instead of NAudio's 100 ms default.
- **Self-healing.** `DeviceRouter` rebuilds its own graph on any transient failure
  (Bluetooth power-off, `AUDCLNT_E_DEVICE_INVALIDATED`, format negotiation hiccups)
  with exponential back-off, and the whole capture is auto-restarted if the capture
  thread dies (default-device change, driver reset). Failures are isolated: one dead
  earbud never stops the others or the capture.

## 5. Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `error CS0246: type 'WaveFormat' could not be found` or similar on `dotnet run` | Stale build artifacts — run `dotnet clean` then `dotnet run -c Release` again (fixed in current source). |
| `You must install .NET to run this application` / `Microsoft.WindowsDesktop.App 8.0.0 was not found` | The .NET **8 Desktop Runtime** is not installed. Either install it (https://dotnet.microsoft.com/download/dotnet/8.0, "Run desktop apps") or install the .NET 10 Desktop Runtime — the project has `RollForward=LatestMajor` set, so it will use the newest installed desktop runtime. |
| No sound on one earbud | Check it is the *active* audio endpoint in Windows sound settings (connected ≠ active). Press Refresh. |
| `reconnecting (…)` forever on a device | The BT link is gone; re-pair or re-select the device. |
| Periodic dropouts on all devices simultaneously | Ring overruns in the log — the PC cannot keep up (rare); close the heaviest app or raise `LatencyCandidates`. |
| Slight (≈40 ms) delay vs. video | Expected — use a player-side audio delay of -40 ms if it bothers you, or lower the latency constants at your own risk. |
| Echo | You mirrored to the device that is also the capture source (see the note above). |

## 6. Project layout

```
MultiBtOut/
├─ MultiBtOut.csproj              net8.0-windows, WPF, NAudio 2.2.1
├─ App.xaml(.cs)                  application entry
├─ MainWindow.xaml(.cs)           UI: device checklist, volumes, health/log
├─ Audio/
│  ├─ SingleProducerRingBuffer.cs lock-free SPSC byte ring (one instance per device)
│  ├─ FanOutTap.cs                broadcast fan-out: private ring per registered sink + counters
│  ├─ FanOutSampleProvider.cs     per-device puller: channel map + gain + silence-fill
│  ├─ DeviceRouter.cs             per-device WasapiOut lifecycle + auto-reconnect + volume
│  └─ RouterEngine.cs             enumeration, capture, fan-out, health sampling
└─ ViewModels/
   ├─ DeviceVm.cs                 checkable device row + volume %
   ├─ MainViewModel.cs            commands, log, health, start/stop orchestration
   └─ RelayCommand.cs             ICommand plumbing
```
