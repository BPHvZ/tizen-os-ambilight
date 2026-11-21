# HyperHDR output

The TV service can stream to a [HyperHDR](https://github.com/awawa-dev/HyperHDR) server instead of — or in addition to — this project's ESP32 firmware. HyperHDR then drives whatever LEDs it is set up for (WLED, Hyperk, Adalight, Philips Hue…), with its own smoothing and color calibration.

> [!WARNING]
> **Experimental full-plane capture is opt-in.** Samsung's private capture ABI is undocumented and may change between firmware builds. An earlier S95C prototype produced 960×540 HyperHDR inputs while non-DRM video played and 64×36 inputs after returning to the app, consistent with the video plane and PPI fallback respectively; direct backend telemetry was unavailable. The isolated helper and downsampling have not been tested on a TV.

The existing sampled output was tested on a Samsung S95B (2022) with HyperHDR in [#14](https://github.com/exzos28/tizen-os-ambilight/issues/14). HyperHDR v22 transport was also tested in CI against registration, streaming, freezing, crashing and restarting.

## How it works

```
TV service ── valid NV12 frame ──► RGB RawImage (up to 160×90) ──┐
                                                                  ├─► HyperHDR FlatBuffers server (TCP 19400)
TV service ── PPI edge samples ──► configured edge image ─────────┘
                 └──────────────────────────────────────────────────► ESP32 (unchanged)
```

When full-frame video is enabled in Outputs, the service launches a separate Tizen helper process to check the private video-capture API and receive video-plane frames. Every native video capture call remains in that helper process, not just the first check. It averages the NV12 plane down to at most 160×90 and converts it to RGB for HyperHDR. The existing PPI sampler continues independently and feeds the ESP32. If the helper is missing, rejects a frame, crashes or times out, HyperHDR falls back to an image synthesized from the 14 PPI points and Outputs shows the reason. Three consecutive usable or rejected frames are required before switching sources; brief failures hold the last video image. A dark plane is rejected when several fresh, corresponding PPI samples are bright. If the video worker stalls, PPI resumes after one second. After three failed helper launches or disconnects, automatic retries stop; switch full-frame off and on to try again.

The helper attempts the video-plane API only in a 32-bit ARM process. Process isolation limits a native crash to the helper, but a successful check cannot guarantee future calls or the TV's shared video subsystem will be safe. If a native call hangs, closing the connection does not guarantee that the helper or call stops immediately. A firmware or app update may change the result. No fingerprint needs to be configured.

The dark-plane comparison is a heuristic: PPI samples are taken sequentially and may include overlays or black bars that the video plane does not. It is not a DRM detector or a guarantee that every black frame is withheld video.

## Setup

**1. HyperHDR (v20 or newer)**

- *Network services* → *Flatbuffers server*: enabled (default), port `19400` (default).
- *LED layout*: set it up for your strip as for any video source.
- *Black border detection*: **off**. With only 14 samples, a dark top or bottom edge can look like a letterbox bar and get cropped.
- *Flatbuffers server* → *HDR tone mapping*: off for the existing PPI path. The video-plane conversion uses an SDR matrix; HDR color accuracy has not been verified.
- *Smoothing*: consider raising the time from the default to ~250 ms. Frames arrive every ~140 ms, so a longer smoothing window hides the steps.
- If the TV is in a different subnet than HyperHDR, allow its address in HyperHDR's network settings — otherwise HyperHDR closes the connection right after it opens (the service log then says so).

**2. On the TV: the launcher's Outputs screen**

Open the Ambilight app on the TV, then *Outputs* in the menu (on the first start the setup
wizard asks *What lights do you have?*: pick *HyperHDR server* or *Both*). In the *HyperHDR*
card enter the host (OK opens the TV keyboard), the port (`19400`) and, if needed, the
priority. Turn on *Full-frame video* to opt in; the card shows whether it is active or why
spot colors are being used. Then *Apply and test*: the card shows *Connected · answering* once HyperHDR replies,
or the connection error. The switch turns the output on and off; the ESP32 card has its own
switch, so both can run at once. Changes apply at once — no rebuild or reinstall — and are
saved on the TV, where they take precedence over those keys of `res/ambilight.json` (the
image size still comes from the file) ([ARCHITECTURE.md § Outputs](ARCHITECTURE.md#outputs-1)).

**Or: the package defaults**

`tv/service/res/ambilight.json` holds the outputs a fresh install starts with (bundled into
the package, so changing it means rebuilding and reinstalling — see
[INSTALL.md § TV apps](INSTALL.md#4-tv-apps)):

```jsonc
{
  "esp32":    { "enabled": true },          // set to false if you have no ESP32 from this project
  "hyperhdr": {
    "enabled": true,
    "host": "192.168.1.50",                 // IP of the HyperHDR machine
    "port": 19400,
    "priority": 150,                        // 50–250, lower wins
    "fullFrame": false,                     // opt in to the isolated video-plane check
    "imageWidth": 64,
    "imageHeight": 36
  }
}
```

| Key | Default | Notes |
|-----|---------|-------|
| `esp32.enabled` | `true` | Stream to ESP32 controllers found by discovery |
| `hyperhdr.enabled` | `false` | |
| `hyperhdr.host` | — | Required when enabled. An IP address is safest; a host name must be resolvable by the TV |
| `hyperhdr.port` | `19400` | HyperHDR's Flatbuffers server port |
| `hyperhdr.priority` | `150` | HyperHDR shows the source with the **lowest** number. Its own USB/system grabbers use 240/245, so 150 overrides them while the TV is streaming |
| `hyperhdr.imageWidth` / `imageHeight` | `64` / `36` | 8–256. Used for the sampled-edge fallback; full-frame capture sends the downsampled video-plane dimensions |
| `hyperhdr.fullFrame` | `false` | Opt in to the isolated video-plane check. The setting is saved by the launcher; no fingerprint is needed. |

An invalid section is disabled and reported as a `ConfigWarning` event in the service log (see [tools/server.py](../tools/README.md)). Settings sent with `set-outputs` use the same keys, ranges and defaults (without the image size, which always comes from `res/ambilight.json`), but an invalid value there rejects the whole change instead.

## Behaviour

- **Connection**: connects with a 3 s timeout, registers as `Tizen Ambilight` with the configured priority, then sends one image per captured frame.
- **Dead server detection**: HyperHDR answers every message. If no answer arrives within 3 s (e.g. the HyperHDR machine lost power), the connection is dropped and re-established.
- **Reconnect**: after 1 s, then 2 s, 4 s and every 5 s while the server stays down. Launching the service again (AppControl `command=start`, the launcher's *Start* while the service is not running) reconnects immediately.
- **Releasing the LEDs**: when the TV service stops or the connection drops, HyperHDR removes the source and falls back to its next one.
- **ESP32 in parallel**: with `esp32.enabled` the ESP32 output keeps working at the same time; neither output can slow the other down.
- **Diagnostics**: `HyperHdrConnected`, `HyperHdrDisconnected` (with the reason; repeated failures are logged about once a minute) and `HyperHdrReplyError` events go to the service log. Events from before a log host was found (including `ConfigWarning`) are queued and delivered once `tools/server.py` is discovered.

## Limitations

- **The isolated helper and 160×90 downsampling are not yet validated on a TV** — the earlier prototype's 960×540 / 64×36 evidence above is based on HyperHDR's received image sizes.
- **Full-frame availability is opt-in and experimental**: a separate process contains helper crashes, but cannot guarantee the TV's shared video subsystem will be unaffected.
- **TV standby**: what capture returns while the TV is in standby has not been measured. If the service keeps sending black frames, the TV source keeps its priority in HyperHDR until the service stops.
- **PPI edge samples are shared with ESP32**: changing their count affects the fallback image and ESP32 frame rate; it does not change full-frame resolution.

## Third-party code

The TV service uses [Google.FlatBuffers](https://www.nuget.org/packages/Google.FlatBuffers) (Apache-2.0) to encode messages; it is bundled into the TV package. The message layout follows HyperHDR's `hyperhdr_request.fbs` (MIT).

The full-frame capture investigation was informed by [RitwickVerma/TizenAmbilight](https://github.com/RitwickVerma/TizenAmbilight) (Ritwick Verma, MIT), particularly its reverse-engineering notes on Samsung's `secvideo_api_capture_video_main` API, NV12 video plane, and DRM-related PPI fallback. The adapter and HyperHDR integration here are a separate implementation for this project; no TizenAmbilight source code is copied.
