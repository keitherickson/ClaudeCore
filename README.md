# ClaudeCore / KeithVision

A local, single-machine web application for **AI video generation and post-processing** on an NVIDIA RTX 5090. It is an ASP.NET Core MVC front-end that **orchestrates three external engines** — it does no model inference itself; instead it drives those processes over HTTP and the command line and manages their inputs/outputs on disk.

> The repository is `ClaudeCore`; the running app's display name is **KeithVision** (configurable via `Branding:AppName`).

### The three external engines

| Engine | What it is | How the app drives it | Used for |
|---|---|---|---|
| **LTX-2.3 inference server** | A self-hosted Python server that reuses **LTX Desktop**'s bundled engine + LTX-2.3 / Gemma weights, listening on `127.0.0.1:8765`. | HTTP REST (typed `HttpClient`) | Text-to-video & image-to-video generation |
| **NVIDIA Maxine Video Effects SDK** | NVIDIA's AI video-effects SDK, invoked through its prebuilt `VideoEffectsApp.exe` sample. | Per-job **child process** (CLI args) | Super-resolution upscaling (to 4K), artifact reduction |
| **ffmpeg** | Standard ffmpeg (Gyan full build, via winget). | Per-job **child process** (CLI args) | Re-timing clips faster: the standalone **Speed Up** page and the post-upscale **Play faster** option |

Everything else (UI, request handling, file orchestration) is the .NET app itself.

- **Generate Video** — text-to-video and image-to-video via a self-hosted **LTX-2.3** inference server.
- **Upscale** — super-resolution up to 4K via the **NVIDIA Maxine Video Effects SDK**.
- **Play faster** — optional re-time of an upscaled clip via **ffmpeg** (within the Generate / Upscale flows).
- **Speed Up** — standalone page to re-time **any** uploaded video via **ffmpeg** (audio kept and re-timed when present).
- **Admin** — health dashboard and in-place restart of the LTX server.

For step-by-step setup see [InstallationSteps.md](InstallationSteps.md); for LTX specifics see [LTX-Integration.md](LTX-Integration.md).

---

## Architecture

The web app is a thin orchestrator. The heavy lifting runs in separate processes that it talks to but does not host:

```
                         ┌──────────────────────────────────────────────────┐
   Browser  ──HTTP:80──► │  KeithVision (ASP.NET Core MVC, .NET 10)           │
   (localhost)           │  Controllers: Video · Upscale · Speed · Admin      │
                         │           └──────► Services (orchestration)        │
                         └───┬───────────────┬───────────────┬───────────────┘
                             │ HTTP          │ child process │ child process
                             ▼               ▼               ▼
                   LTX-2.3 server      VideoEffectsApp.exe   ffmpeg.exe
                   127.0.0.1:8765      (Maxine VFX SDK)      (setpts / atempo
                   (Python, LTX        super-resolution       re-time; Speed
                    Desktop engine)                           page + Play faster)
                             │
                             ▼
                   Output files on disk  ──►  served back to the browser
                   C:\Users\keith\Videos\LTX-Generated\…
```

- **No database.** All state is files on disk plus one tiny JSON state file (the remembered starting image).
- **Localhost only.** Kestrel binds `127.0.0.1` — the app is not exposed to the network. There is intentionally **no authentication**; the trust boundary is "processes on this PC."
- **Synchronous engine calls.** The LTX `/api/generate` and the Maxine/ffmpeg subprocesses block until done, so HTTP timeouts and subprocess timeouts are configured in minutes.

### Tech stack

| Layer | Choice |
|---|---|
| Runtime | .NET 10 (`net10.0`), nullable + implicit usings enabled |
| Web | ASP.NET Core **MVC** (controllers + Razor views) |
| Front-end | Bootstrap + vanilla JS (no SPA framework); progress driven by `fetch` polling |
| Inference | External **LTX-2.3** server (reuses LTX Desktop's bundled Python + weights) |
| Upscaling | External **NVIDIA Maxine Video Effects SDK** sample exe |
| Re-time | External **ffmpeg** (Gyan build) |

---

## Features & request flows

### 1. Generate Video — `VideoController`
Text-to-video, or image-to-video when a starting image is supplied.

1. `GET /Video` renders the form. The capability matrix (which resolutions allow which fps/durations) is fetched live from the server (`/Video/Specs`) with a hard-coded fallback, so the form self-adapts.
2. `POST /Video/Generate`:
   - An uploaded image is staged to the LTX **input** directory (`LtxVideoService.StageImageAsync`) so the server process can read it. With no upload, the last-used image is reused (`LastImageStore`).
   - The request is POSTed to the LTX server (`/api/generate`); the call blocks until the `.mp4` is rendered.
   - The finished file is copied from the server's output path into the app's **output** directory, then **converted to H.264** in place if the LTX engine emitted HEVC (`VideoSpeedService.ConvertToH264InPlaceAsync`, audio preserved) — H.264 plays in all browsers and is what Maxine accepts.
   - *(optional)* If "Upscale" is checked → run Maxine (see below). If "Play faster" is also checked → re-time the upscaled file (see below).
3. The browser polls `/Video/Progress` during the run; because the engine reports coarse progress, the bar is a smoothed client-side estimate.
4. `GET /Video/Download?name=…` streams the result (range-enabled, with a path-traversal guard).

### 2. Upscale — `UpscaleController` + `MaxineUpscaleService`
Super-resolution via the Maxine SDK's `VideoEffectsApp.exe`, run as a per-job child process.

- Effects: **SuperRes** (model; `--mode` 0 compressed / 1 clean), **Upscale** (fast; `--strength`), **ArtifactReduction**.
- SuperRes requires an output height that is an integer **2×/3×/4×** of the source, so both the Generate flow and the standalone page take a **factor** (not an absolute height) and compute `target = sourceHeight × factor` — the source height is read with [`VideoProbe`](Services/VideoProbe.cs), a dependency-free MP4 `tkhd`-box reader. (An arbitrary absolute height could be a non-integer multiple, which the SDK rejects.)
- **Input is normalized to H.264 first.** `VideoEffectsApp` only accepts H.264 ("Filters only target H264 videos, not HEVC"), so the source is probed with `ffprobe` and transcoded to a temp H.264 copy (via `VideoSpeedService.EnsureH264Async`) when it isn't already H.264.
- The SDK's runtime/OpenCV DLLs are injected onto the child process `PATH`; models must live in a **writable** directory (TensorRT caches its engine on first run — see InstallationSteps for that gotcha).
- Standalone page at `GET /Upscale`; uploads accepted up to 2 GB.

### 3. Play faster (re-time) — `VideoSpeedService`
Optional step that runs **after** upscaling, on both the Generate flow and the Upscale page.

- Shells out to ffmpeg with `setpts=(1/N)*PTS` and re-encodes to browser-friendly H.264, writing a `…_xN.mp4` next to the upscaled file (so the existing download route serves it with no new endpoint).
- Video-only (`-an`): the Maxine output has no audio track (its OpenCV writer drops it), so nothing is lost.
- Factors offered: 1.5× / 2× / 3× / 4×.

### 4. Admin — `AdminController` + `LtxServerControl`
A local operations dashboard at `GET /Admin`.

- `GET /Admin/Status` aggregates: LTX reachability + raw `/health` (model/GPU/VRAM) and whether the port is listening (cheap TCP-listener check, no HTTP); **Maxine** and **ffmpeg** readiness; **live GPU** stats from `nvidia-smi` (name, VRAM used/total, utilization, temperature — independent of the LTX server); web-app version/uptime; output-disk free space; and **staging** file count/size (reclaimable uploads + temp transcodes).
- `POST /Admin/RestartLtx` runs [`tools/restart-ltx-server.ps1`](tools/restart-ltx-server.ps1) (kill the process on the port → wait for it to free → relaunch hidden with the same env/log contract as logon startup → wait for it to listen again). The page shows a client-side progress bar during the restart.
- `POST /Admin/CleanStaging` deletes staged uploads (the `_inputs`, `_upscale_inputs`, `_speed_inputs` dirs) and temp H.264 transcodes — never the finished output videos.

### 5. Speed Up — `SpeedController` + `VideoSpeedService`
Standalone page at `GET /Speed` to re-time **any** uploaded video (independent of upscaling).

- `POST /Speed/Run` stages the upload, then runs the same ffmpeg `setpts` re-time as above — but **keeps and re-times audio when present**: it probes for an audio stream with `ffprobe` and, if found, applies an `atempo` chain (chained because `atempo` only accepts 0.5–2.0 per instance, e.g. 3× → `atempo=2.0,atempo=1.5`). Output goes to the configured `VideoSpeed:OutputDirectory`.
- The page shows the result inline with **Download** and **Clear** buttons; `GET /Speed/Download?name=…` streams it (range-enabled, path-traversal guard). Uploads accepted up to 2 GB.

---

## Components

| Component | Role |
|---|---|
| `LtxVideoClient` | Typed `HttpClient` over the LTX server REST API (`/health`, `/api/generate`, `/api/generation/progress`, `/api/generate/models-specs`). |
| `LtxVideoService` | Orchestrates a generation: stage image → call server → copy result to output dir. Path-traversal guards on download/reuse. |
| `LtxServerControl` | Port-listening check + restart of the LTX server process. |
| `MaxineUpscaleService` | Runs `VideoEffectsApp.exe` per job; builds args per effect; sets up the DLL `PATH`. |
| `VideoSpeedService` | Runs ffmpeg `setpts` re-time. Video-only after upscaling; keeps + re-times audio (`atempo` chain, with `ffprobe` detection) for the standalone Speed Up page. |
| `VideoProbe` | Reads MP4 display height (no external dependency) to choose a valid SuperRes factor. |
| `LastImageStore` | Remembers the last starting image across requests/restarts (persisted to a state file). |
| `*Options` classes | Strongly-typed config bound from `appsettings.json`. |

---

## Configuration (`appsettings.json`)

| Section | Purpose | Key settings |
|---|---|---|
| `Kestrel:Endpoints` | HTTP binding | `http://127.0.0.1:80` + `:5080` |
| `Branding` | UI display name | `AppName` |
| `VideoDefaults` | Generate-form defaults | `Resolution`, `Duration`, `Fps`, `AspectRatio`, `CameraMotion`, `Audio` |
| `Ltx` | LTX server + I/O | `BaseUrl` (`:8765`), `OutputDirectory`, `InputDirectory`, `GenerationTimeoutMinutes`, `RestartScriptPath` |
| `Maxine` | Upscaler exe + SDK | `ExecutablePath`, `ModelDir` (writable copy), `SdkBinDir`, `OpenCvBinDir`, `OutputDirectory`, `Codec` (`avc1`), `TimeoutMinutes` |
| `VideoSpeed` | ffmpeg re-time | `FfmpegPath`, `OutputDirectory`, `InputDirectory` (Speed page staging), `TimeoutMinutes` |

> `VideoSpeed:FfmpegPath` points at an absolute, **version-specific** path under the winget package folder; a `winget upgrade` of ffmpeg changes it and must be updated.

---

## External dependencies (not in this repo)

These run on the host and are configured by path:

- **LTX Desktop** — provides the bundled Python engine and LTX-2.3 / Gemma weights; the inference server is launched from `tools/`.
- **NVIDIA Maxine Video Effects SDK** — redistributable (AI models + DLLs) plus the prebuilt `VideoEffectsApp.exe`.
- **ffmpeg** — `winget install Gyan.FFmpeg`.
- **NVIDIA GPU driver** + an RTX (Blackwell-class) GPU.

---

## Build, run, publish

```powershell
# Dev: run from source (LTX server started separately for generation)
dotnet run --project ClaudeCore.csproj
#   → http://127.0.0.1:80  (and :5080)

# Start the LTX inference server (per session)
tools\run-ltx-server.ps1            #  → http://127.0.0.1:8765

# Publish a Release build the logon auto-start uses
tools\publish-keithvision.ps1       #  → C:\ClaudeCore\KeithVision
```

**Auto-start:** a Task Scheduler task ("KeithVision Startup", logon trigger) launches the LTX server and the **published** web app via `tools/start-keithvision.ps1`. Because that runs the *published* build, re-publish after code changes.

**Local domain:** `tools/add-keithvision-host.ps1` (run elevated) maps `www.keithvision.com → 127.0.0.1` for this PC, so the app is reachable at **http://www.keithvision.com**.

---

## Repository layout

```
Controllers/      VideoController, UpscaleController, SpeedController, AdminController, HomeController
Services/         orchestration + typed-HttpClient + *Options + VideoProbe + LastImageStore
Models/Ltx/       LTX request/response/health/progress DTOs
Views/            Video, Upscale, Speed, Admin, Shared (_Layout)
tools/            PowerShell: run/restart LTX server, publish, host mapping, ltx_launch.py
wwwroot/          static assets (Bootstrap, jQuery validation)
appsettings.json  all configuration
```
