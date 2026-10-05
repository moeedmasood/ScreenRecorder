# Screen Recorder: Technical Spec and Backlog

Stack: C# / .NET 8 / WPF (MVVM). Windows 10 1903+ and Windows 11, x64. Distribution: free tier plus paid Pro tier.

---

## Part 1: Technical Specification

### 1. Solution structure

```
ScreenRecorder.sln
├─ src/
│  ├─ ScreenRecorder.App            WPF UI, MVVM views, DI composition root
│  ├─ ScreenRecorder.Core           Interfaces, models, settings, feature flags (no Windows deps)
│  ├─ ScreenRecorder.Capture        Screen, webcam, audio capture
│  ├─ ScreenRecorder.Compositing    D3D11 compositor (screen + webcam + annotations)
│  ├─ ScreenRecorder.Encoding       Media Foundation sink writer, muxer
│  ├─ ScreenRecorder.Overlay        Drawing overlay window, tools
│  ├─ ScreenRecorder.Hotkeys        Global hotkey service
│  └─ ScreenRecorder.Licensing      Pro/free feature gating
├─ tests/
│  ├─ ScreenRecorder.Core.Tests
│  ├─ ScreenRecorder.Capture.Tests
│  └─ ScreenRecorder.Integration.Tests   FFprobe-based output validation
└─ installer/                        MSIX or WiX
```

NuGet: NAudio, Vortice.Direct3D11 / Vortice.MediaFoundation, CommunityToolkit.Mvvm, Microsoft.Extensions.DependencyInjection / Logging, Serilog, xUnit, FluentAssertions.

### 2. Core interfaces (Core project)

```csharp
public interface IVideoSource { event Action<VideoFrame> FrameArrived; void Start(CaptureTarget t); void Stop(); }
public interface IAudioSource { event Action<AudioBuffer> DataAvailable; void Start(); void Stop(); string DeviceId { get; } }
public interface ICompositor  { void SetLayers(ScreenLayer s, WebcamLayer? w, AnnotationLayer? a); ComposedFrame Render(long ptsHns); }
public interface IAudioMixer  { void AddInput(IAudioSource s, float gain); AudioBuffer Read(long ptsHns); }
public interface IEncoder     { void Start(EncoderOptions o); void WriteVideo(ComposedFrame f); void WriteAudio(AudioBuffer b); void Finish(); }
public interface IRecordingSession { Task StartAsync(); void Pause(); void Resume(); Task<string> StopAsync(); RecordingState State { get; } }
```

### 3. Pipeline and timing

1. A single `MasterClock` wraps `Stopwatch` (QPC). All timestamps are in 100 ns units.
2. Video: Windows.Graphics.Capture `Direct3D11CaptureFramePool` delivers frames. Each frame is stamped with the clock and pushed to a bounded queue (capacity about 4).
3. Compositor thread: on a fixed fps tick (30/60), take the latest screen frame (repeat the last one if none arrived), draw the webcam quad and the annotation texture, then output a BGRA/NV12 texture.
4. Audio: WASAPI loopback and mic each run in their own thread, with a 10 ms buffer. Resample to 48 kHz stereo float. The mixer sums the streams with gain and clipping protection. Gaps in loopback (silence emits no data) are filled with zeros using the clock.
5. Encoder: Media Foundation sink writer with an H.264 MFT (hardware first, software fallback) and AAC. Audio is the timing master; video PTS is derived from the clock.
6. Pause: record the pause duration and subtract it from subsequent timestamps. No frames are written while paused.
7. Output: write `*.tmp.mp4` in the temp or target folder, finalise on stop, then rename to the final name. Use fragmented MP4 so a crash leaves a playable file.

### 4. Capture targets

| Target | API | Notes |
|---|---|---|
| Monitor | `GraphicsCaptureItem.CreateFromHandle` via `IGraphicsCaptureItemInterop` (HMONITOR) | Primary path |
| Window | Same interop with HWND | Handle minimise/resize; reconfigure the frame pool on size change |
| Region | Capture the monitor and crop in the compositor | Region selector overlay |
| Fallback | DXGI Desktop Duplication | For exclusive-fullscreen or older systems |

Notes:
- Disable the yellow capture border where supported (`IsBorderRequired = false`, Windows 11 and newer).
- Per-monitor DPI awareness (PerMonitorV2) is mandatory in the app manifest.
- Cursor capture is a toggle (`IsCursorCaptureEnabled`).

### 5. Audio details
- System audio: `WasapiLoopbackCapture` on the default render endpoint. Re-bind on default device changes through `IMMNotificationClient`.
- Mic: `WasapiCapture` on the selected capture endpoint. Show a level meter (RMS/peak) at about 20 Hz.
- Tracks: the default is a single mixed track. Pro can write separate tracks (system, mic).
- Optional processing: noise suppression (RNNoise), gain, mute.
- Sample-rate mismatch handling: `MediaFoundationResampler` or WDL resampler.

### 6. Webcam
- Enumerate through Media Foundation (`MFEnumDeviceSources`). Let the user choose a format (resolution and fps).
- Frames arrive as NV12/YUY2/MJPG. Convert on the GPU (video processor MFT or shader) to BGRA.
- Layout model:

```csharp
record WebcamLayout(Anchor Corner, double SizeFraction /*0.15,0.25,0.5*/, Shape Shape, bool Mirror, Point? CustomPos);
```
- Presets: thumbnail 15%, quarter 25%, half 50%, custom (drag and resize). Shapes: rectangle, rounded, circle.
- A webcam preview and positioning surface lives in the overlay, so the user can drag it live. The composited position updates immediately.
- Webcam-only mode (v2): the webcam fills the frame.

### 7. Drawing and annotation overlay
- A borderless, transparent, topmost WPF window per monitor with `AllowsTransparency=True`.
- Two modes:
  - **Pass-through (default):** `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE`, so clicks reach the apps underneath.
  - **Draw mode:** remove `WS_EX_TRANSPARENT` and capture mouse, pen and touch input.
- Tools: pen, highlighter (about 40% alpha, wide, with a multiply look), line, arrow, rectangle, ellipse, text box, eraser, laser pointer. Controls: colour, thickness, undo/redo, clear, save snapshot.
- Rendering: WPF `InkCanvas` for the MVP. Consider a Direct2D-rendered layer composited on the GPU if there's a performance problem.
- Annotations are included in the recording by rendering the annotation layer in the compositor from a shared bitmap/texture, or simply by capturing the overlay as part of the screen. The first is cleaner. It requires the overlay to be marked excluded from capture (`WDA_EXCLUDEFROMCAPTURE`) so annotations aren't doubled.
- Hotkey: default `Ctrl+Shift+D` toggles draw mode, and `Esc` exits it. Bare `Shift` can't be registered as a global hotkey. A modifier-only trigger would need a low-level keyboard hook (`WH_KEYBOARD_LL`), which is more fragile. Offer it as an advanced option.
- The toolbar for drawing is a small floating palette, excluded from capture.

### 7a. Control bar and region selector
- Floating, draggable control bar (record, pause, stop, timer, mic/system toggles, webcam toggle, draw toggle). Set `WDA_EXCLUDEFROMCAPTURE`.
- It auto-hides during recording if the user chooses, and is reachable from the tray icon.
- Region selector: full-screen dim overlay with a draggable rectangle and size readout.

### 8. Hotkeys

| Action | Default |
|---|---|
| Start/stop | Ctrl+Shift+R |
| Pause/resume | Ctrl+Shift+P |
| Toggle drawing | Ctrl+Shift+D |
| Toggle webcam | Ctrl+Shift+W |
| Clear drawing | Ctrl+Shift+C |

`RegisterHotKey` via a hidden message window. Detect conflicts (registration failure) and show them in settings. All bindings are configurable.

### 9. Settings (JSON in `%AppData%\ScreenRecorder\settings.json`)
Output folder, file name template (`{date}_{time}_{target}`), format, fps, resolution scale, bitrate or quality, encoder preference, audio devices and gains, webcam device and layout, hotkeys, cursor/click highlight, countdown, tray behaviour, theme.
- Versioned schema with migrations.
- Validate the output folder (exists, writable, free space) before every recording.

### 10. Encoding defaults

| Setting | Default |
|---|---|
| Container / video / audio | MP4 / H.264 / AAC 192 kbps |
| Resolution | Native, scalable 100/75/50% |
| FPS | 30 (60 optional) |
| Rate control | CBR/VBR about 8 Mbps at 1080p30 |
| Encoder order | NVENC, QuickSync, AMF (via MF hardware MFTs), then software |

### 11. Free vs Pro feature gating
A `IFeatureService` returns flags such as `Max4K`, `MultiTrackAudio`, `AdvancedWebcamShapes`, `Scheduler`, `HevcEncoding`. Free: 1080p, 30 fps, single mixed audio, basic tools. Pro: unlocked via a signed licence key validated offline, with optional online activation. No watermark in the free tier (a good-will decision you can revisit).

### 12. Error handling and resilience
- Device lost (mic unplugged, camera removed): continue recording, show a notification, and substitute silence or hide the webcam.
- GPU device removed: attempt recreation, otherwise finalise and save what exists.
- Low disk space: warn at 1 GB, stop gracefully at 200 MB.
- Unhandled exceptions: write logs (Serilog rolling file), and attempt recovery of the temp file on next launch.
- DRM black frames: can't be detected reliably, so provide a help note.

### 13. Security and privacy
- All processing is local, with no telemetry by default.
- Clear indicators when the mic or camera is live.
- Don't log recorded content or file paths in shared logs.
- Sign binaries and updater packages. Use HTTPS only for updates.

### 14. Performance budget (1080p30, hardware encoder)
CPU under 10%, GPU under 10% on mid-range, memory under 300 MB, dropped frames under 1%, A/V offset within 40 ms, UI thread never blocks on capture.

### 15. Test plan
- **Unit:** clock, pause arithmetic, mixer (gain, clipping, resample), layout maths, settings migration, hotkey parsing.
- **Integration (automated):** record 10 s with a synthetic screen and tone, then use FFprobe to assert duration, resolution, fps, codec, audio presence and A/V offset.
- **Manual matrix:** windowed, borderless and exclusive-fullscreen games; Chrome/Edge video; VLC; multi-monitor with mixed DPI; 125–200% scaling; Bluetooth headset (sample-rate changes); device hot-plug; sleep/resume; 2-hour soak; low disk.
- **Hardware:** NVIDIA, AMD, Intel iGPU, hybrid-GPU laptops.

---

## Part 2: Backlog

Priorities: **P0** must-have MVP, **P1** MVP-complete, **P2** v2. Sizes: S (about 1 day), M (2–4 days), L (about 1 week).

### Epic A: Foundation
| ID | Story | Pri | Size | Acceptance criteria |
|---|---|---|---|---|
| A1 | Create solution, projects, DI, logging, CI build | P0 | S | Builds on CI, tests run |
| A2 | PerMonitorV2 manifest, app shell, tray icon | P0 | S | Correct scaling at 100–200% |
| A3 | Settings service with JSON persistence and migrations | P0 | M | Round-trip and migration tests pass |
| A4 | Master clock and queue primitives | P0 | S | Unit tests for monotonic time and pause offset |

### Epic B: Screen capture
| ID | Story | Pri | Size | Acceptance criteria |
|---|---|---|---|---|
| B1 | Monitor capture via Graphics Capture | P0 | M | Frames at target fps on 1080p |
| B2 | Window capture, handling resize/minimise | P0 | M | Recording continues across resize |
| B3 | Region selector and crop | P0 | M | Output matches selected rectangle |
| B4 | Cursor toggle and border-removal | P1 | S | Option respected |
| B5 | Desktop Duplication fallback | P1 | L | Captures exclusive-fullscreen test app |
| B6 | Multi-monitor selection | P1 | M | Picks any monitor, mixed DPI correct |

### Epic C: Audio
| ID | Story | Pri | Size | Acceptance criteria |
|---|---|---|---|---|
| C1 | System loopback capture | P0 | M | Audible app audio in file |
| C2 | Mic capture and device selection | P0 | M | Voice present, device switchable |
| C3 | Mixer with gain, mute, resample | P0 | M | Mixer tests pass; no clipping |
| C4 | Silence-gap filling for loopback | P0 | S | No drift when system is silent |
| C5 | Level meters | P1 | S | Meter reacts within 100 ms |
| C6 | Device-change handling | P1 | M | Unplug mic mid-recording does not crash |
| C7 | Separate audio tracks (Pro) | P2 | M | Two tracks visible in FFprobe |
| C8 | Noise suppression | P2 | M | Toggle reduces steady noise |

### Epic D: Encoding and output
| ID | Story | Pri | Size | Acceptance criteria |
|---|---|---|---|---|
| D1 | Media Foundation H.264/AAC MP4 writer | P0 | L | Plays in Windows media apps |
| D2 | Hardware encoder selection with fallback | P0 | M | Uses GPU when available |
| D3 | Pause/resume | P0 | M | No visible gap; A/V in sync |
| D4 | Temp file, fragmented MP4, atomic finalise | P0 | M | Killed process leaves playable file |
| D5 | Output folder and name template, disk checks | P0 | M | Saved to configured folder |
| D6 | Quality presets (fps, bitrate, scale) | P1 | S | Presets change output as expected |
| D7 | HEVC / 4K60 (Pro) | P2 | M | Gated by feature flag |

### Epic E: Webcam
| ID | Story | Pri | Size | Acceptance criteria |
|---|---|---|---|---|
| E1 | Enumerate cameras, preview | P0 | M | Live preview in app |
| E2 | GPU compositing into the recording | P0 | L | Webcam appears in output |
| E3 | Presets: thumbnail/quarter/half + corners | P0 | M | Sizes within 1% of target |
| E4 | Drag/resize live; custom position | P1 | M | Position persists |
| E5 | Mirror, shapes (round/circle) | P1 | S | Visible in output |
| E6 | Webcam-only mode, background blur | P2 | L | |

### Epic F: Annotation
| ID | Story | Pri | Size | Acceptance criteria |
|---|---|---|---|---|
| F1 | Transparent overlay with pass-through/draw modes | P0 | M | Clicks pass through when off |
| F2 | Pen, highlighter, colours, thickness | P0 | M | Drawn strokes appear in recording |
| F3 | Text tool | P0 | M | Type and place text |
| F4 | Shapes, arrows, eraser, undo/redo/clear | P1 | M | |
| F5 | Draw hotkey and floating palette | P0 | S | Toggle works over a fullscreen app |
| F6 | Exclude control surfaces from capture | P0 | S | Control bar not visible in output |
| F7 | Auto-fade ink, laser pointer, click effects | P2 | M | |

### Epic G: UI and hotkeys
| ID | Story | Pri | Size | Acceptance criteria |
|---|---|---|---|---|
| G1 | Main window and settings pages | P0 | M | All options editable |
| G2 | Floating control bar | P0 | M | Start/pause/stop, timer, size |
| G3 | Global hotkeys with conflict detection | P0 | M | Conflict displayed |
| G4 | Countdown and post-save actions (open folder/play) | P1 | S | |
| G5 | Recording library (recent files) | P2 | M | |

### Epic H: Quality, release, commercial
| ID | Story | Pri | Size | Acceptance criteria |
|---|---|---|---|---|
| H1 | FFprobe integration test suite | P0 | M | CI verifies duration, A/V offset |
| H2 | Game and browser compatibility pass | P1 | L | Matrix documented |
| H3 | Performance and soak tests | P1 | M | Meets budget |
| H4 | Crash reporting (opt-in) and log export | P1 | S | |
| H5 | Installer (MSIX/WiX), code signing, auto-update | P1 | L | Clean install/uninstall |
| H6 | Licensing and feature flags | P1 | L | Free/Pro gating verified |
| H7 | Privacy policy, EULA, third-party notices | P1 | S | |

### Suggested sprint order (2-week sprints)
1. **Sprint 1:** A1–A4, B1, C1, C2, D1 (the Phase 0 spike: screen and audio to MP4).
2. **Sprint 2:** C3, C4, D2–D5, B2, B3, G1.
3. **Sprint 3:** G2, G3, E1–E3, F1, F2, F5, F6.
4. **Sprint 4:** E4, E5, F3, F4, C5, C6, B5, B6, D6.
5. **Sprint 5:** H1–H3, G4, hardening and bug fixing.
6. **Sprint 6:** H4–H7 and release candidate. P2 items after the first release.

### Decisions (confirmed: all recommendations accepted)
1. Hardware encoding through Media Foundation only, or also bundle FFmpeg (LGPL) for more codecs? Recommendation: Media Foundation first.
2. Draw hotkey: combination (recommended) or modifier-only through a low-level hook?
3. Free-tier limits (for example 1080p/30 fps) and pricing model (one-time vs subscription).
4. Minimum OS: Windows 10 1903 (recommended) or 20H2+.

**Status:** Media Foundation only for v1; Ctrl+Shift+D draw hotkey; Free tier 1080p/30 fps (pricing model TBD); minimum Windows 10 1903. Sprint 1 spike implemented; Sprint 2 implemented (window/region capture, fragmented MP4 with verified crash recovery, settings persistence, quality presets, disk guards, WPF target/mic/quality UI). Sprint 3 implemented (webcam PiP thumbnail/quarter/half with corner and mirror, draw overlay with pen/highlighter/eraser, floating control bar, global hotkeys Ctrl+Shift+R/P/D/X; control surfaces excluded from capture). Known limitation: the draw overlay is not captured in Window-mode recordings. Sprint 4 implemented: shapes/arrows/text with undo/redo (F3, F4), live audio level meters (C5), device-loss warning (C6), round/circle webcam (E5). Also: E4 live webcam move/resize (Cam button on control bar; not in Window mode), B5 Desktop Duplication compatibility capture (auto-fallback or 'Compatibility' checkbox; not for Window mode), B6 multi-monitor selection. Untested on this hardware: real multi-monitor/mixed DPI, exclusive-fullscreen games, mic metering with speech.

## Sprint 5 results (testing and hardening)

**Delivered:** G4 (countdown 0/3/5/10 s with an on-screen number that is excluded from capture; after-save action None / Show in folder / Play), H1 (`tests\ScreenRecorder.Integration.Tests`: real recordings checked with ffmpeg for duration, A/V start offset, region size, pause behaviour and compatibility capture; set `FFMPEG_PATH` or `REQUIRE_FFMPEG=1` in CI; run with `dotnet test --filter Category=Integration`), H3 (spike prints CPU, memory and effective fps).

**Soak result (1080p30, mic + system audio + webcam, Standard quality, 4 min, hardware encoder):** CPU about 7% of the machine, working set 276 -> 292 MB (no growth), 29.9 fps effective, audio and video durations within 20 ms of each other.

**Compatibility matrix (H2) - status:**

| Scenario | Status |
|---|---|
| Desktop / apps, Monitor and Region | Verified |
| Single window (WGC) | Verified |
| Compatibility (Desktop Duplication) capture | Verified on desktop; not on a real exclusive-fullscreen game |
| Browser video / game audio via loopback | Loopback verified (silence fill and mixing); not run against specific titles |
| Multi-monitor / mixed DPI | Implemented, not testable on this single-monitor machine |
| Games with anti-cheat, protected (DRM) video | Not tested; DRM video is expected to record black |
| 60 fps and 4K | 60 fps selectable, not soak-tested |

Remaining before release: run the matrix on real games/browsers and a second monitor; long soak (1 h+); disk-full flow; mic unplug.


## Sprint 6 results (H4–H7)

**Delivered**
- H4: rolling logs (14-day prune), local crash reports for all unhandled exceptions, opt-in upload (off by default, https only), "Export logs" zip, About window.
- H5: `installer\build.ps1` (self-contained win-x64 publish, portable ZIP, Inno Setup installer, optional signtool signing, `update.json` generation) and `installer\ScreenRecorder.iss`. Built and verified: silent per-user install, app launches, uninstall removes files and keeps user data. Update flow is notify-and-download with SHA-256 in the manifest (not silent update).
- H6: offline ECDSA P-256 licence keys; `tools\ScreenRecorder.LicenseTool` (`genkey`, `issue`). Free tier: 30 fps, Standard/Low quality. Pro: 60 fps, High quality. `SeparateAudioTracks` and `Hevc4K` are reserved, not implemented. Output resolution is not capped (free "1080p" limit not enforced).
- H7: drafts in `docs\PRIVACY_POLICY.md`, `docs\EULA.md`, `THIRD_PARTY_NOTICES.md` (legal review required).
- Tests: 29 unit, 5 integration, all passing.

**Needed before release**
- Code-signing certificate (set `SIGN_PFX`/`SIGN_PFX_PASSWORD` or `SIGN_CERT_THUMBPRINT`; needs Windows SDK signtool). Installer currently unsigned, so SmartScreen will warn.
- Fill `ProductConfig` `UpdateManifestUrl`, `CrashReportUrl`, `PurchaseUrl`; Pro pricing; legal review and placeholders in docs.
- Back up the private licence key at `%USERPROFILE%\ScreenRecorder-license-private.pem`; losing it means no new keys can be issued, leaking it lets anyone forge keys.

**Still unverified**: About window and edition-gating dialogs (visually), crash handler UI, countdown overlay, post-save actions, mic with real speech/unplug, Rounded webcam shape, multi-monitor/mixed DPI, exclusive-fullscreen games, 1 h+ recordings, disk-full flow.
