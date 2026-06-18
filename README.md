# ClaudeCore / KeithVision

A local, single-machine web application for **AI video generation and post-processing** on an NVIDIA RTX 5090. It is an ASP.NET Core MVC front-end that **orchestrates four external engines** — it does no model inference itself; instead it drives those processes over HTTP and the command line and manages their inputs/outputs on disk.

> The repository is `ClaudeCore`; the running app's display name is **KeithVision** (configurable via `Branding:AppName`).

### The three external engines

| Engine | What it is | How the app drives it | Used for |
|---|---|---|---|
| **LTX-2.3 inference server** | A self-hosted Python server that reuses **LTX Desktop**'s bundled engine + LTX-2.3 / Gemma weights, listening on `127.0.0.1:8765`. | HTTP REST (typed `HttpClient`) | Text-to-video & image-to-video generation |
| **Stable Audio Open server** | A self-hosted Python server (diffusers) generating sound effects from text on `127.0.0.1:8770` *(optional)*. | HTTP REST (typed `HttpClient`) | Text-to-sound-effect audio for audio-to-video |
| **NVIDIA Maxine Video Effects SDK** | NVIDIA's AI video-effects SDK, invoked through its prebuilt `VideoEffectsApp.exe` sample. | Per-job **child process** (CLI args) | Super-resolution upscaling (to 4K), artifact reduction |
| **ffmpeg** | Standard ffmpeg (Gyan full build, via winget). | Per-job **child process** (CLI args) | Re-timing clips faster: the standalone **Speed Up** page and the post-upscale **Play faster** option |

Everything else (UI, request handling, file orchestration) is the .NET app itself.

- **Generate Video** — text-to-video and image-to-video via a self-hosted **LTX-2.3** inference server.
- **AI sound** — generate a sound effect from a text prompt with a **self-hosted Stable Audio** model on your GPU, and use it as the audio track (audio-to-video), right from the Generate page.
- **Upscale** — super-resolution up to 4K via the **NVIDIA Maxine Video Effects SDK** (source audio is preserved).
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

- **Audio (optional).** An additional local Stable Audio server (`127.0.0.1:8770`) generates sound effects from text on the same HTTP pattern as LTX; omitted from the diagram above for clarity. See §6.
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
   - An optional **audio track** switches the server to **audio-to-video** (the audio drives the render, an image becomes a conditioning frame, and the generate-audio flag is ignored). It comes from either an uploaded file (`StageAudioAsync`, `audioPath`) or a clip **generated from a text prompt** by the self-hosted Stable Audio server (staged server-side, passed as `audioStagedPath`; an upload wins if both are present). See §6.
   - The request is POSTed to the LTX server (`/api/generate`); the call blocks until the `.mp4` is rendered.
   - The finished file is copied from the server's output path into the app's **output** directory, then **converted to H.264** in place if the LTX engine emitted HEVC (`VideoSpeedService.ConvertToH264InPlaceAsync`, audio preserved) — H.264 plays in all browsers and is what Maxine accepts.
   - *(optional)* If "Upscale" is checked → run Maxine (see below). If "Play faster" is also checked → re-time the upscaled file (see below).
3. The browser polls `/Video/Progress` during the run; because the engine reports coarse progress, the bar is a smoothed client-side estimate. A **Cancel** button posts to `/Video/Cancel`, which **hard-stops** the render by killing + relaunching the LTX server (`LtxServerControl.RestartDetached` — fire-and-forget so the request returns instantly and the relaunched server is never torn down). The server's own cooperative cancel can't interrupt an in-progress video inference (no per-step hook), so restarting the process is the only way to actually free the GPU mid-render — at the cost of reloading models on the next generation.
4. `GET /Video/Download?name=…` streams the result (range-enabled, with a path-traversal guard).

### 2. Upscale — `UpscaleController` + `MaxineUpscaleService`
Super-resolution via the Maxine SDK's `VideoEffectsApp.exe`, run as a per-job child process.

- Effects: **SuperRes** (model; `--mode` 0 compressed / 1 clean), **Upscale** (fast; `--strength`), **ArtifactReduction**.
- SuperRes requires an output height that is an integer **2×/3×/4×** of the source, so both the Generate flow and the standalone page take a **factor** (not an absolute height) and compute `target = sourceHeight × factor` — the source height is read with [`VideoProbe`](Services/VideoProbe.cs), a dependency-free MP4 `tkhd`-box reader. (An arbitrary absolute height could be a non-integer multiple, which the SDK rejects.)
- **Input is normalized to H.264 first.** `VideoEffectsApp` only accepts H.264 ("Filters only target H264 videos, not HEVC"), so the source is probed with `ffprobe` and transcoded to a temp H.264 copy (via `VideoSpeedService.EnsureH264Async`) when it isn't already H.264.
- The SDK's runtime/OpenCV DLLs are injected onto the child process `PATH`; models must live in a **writable** directory (TensorRT caches its engine on first run — see InstallationSteps for that gotcha).
- **Audio is restored afterward.** Maxine's OpenCV writer produces a video-only file, so the original upload's audio is re-muxed back on (`VideoSpeedService.RestoreAudioAsync`: `-c:v copy` so the upscaled video isn't re-encoded, audio → AAC). No-op when the source has no audio track.
- Standalone page at `GET /Upscale`; uploads accepted up to 2 GB.

### 3. Play faster (re-time) — `VideoSpeedService`
Optional step that runs **after** upscaling, on both the Generate flow and the Upscale page.

- Shells out to ffmpeg with `setpts=(1/N)*PTS` and re-encodes to browser-friendly H.264, writing a `…_xN.mp4` next to the upscaled file (so the existing download route serves it with no new endpoint).
- Keeps and re-times audio when present (the upscaled clip has its audio restored first), applying an `atempo` chain so sound stays in sync; falls back to video-only on silent inputs.
- Factors offered: 1.5× / 2× / 3× / 4×.

### 4. Admin — `AdminController` + `LtxServerControl` + `AudioServerControl`
A local operations dashboard at `GET /Admin`.

- `GET /Admin/Status` aggregates: LTX reachability + raw `/health` (model/GPU/VRAM) and whether the port is listening (cheap TCP-listener check, no HTTP); the **Stable Audio** server's port-listening + model-loaded state; **Maxine** and **ffmpeg** readiness; web-app version/uptime; output-disk free space; and **staging** file count/size (reclaimable uploads + temp transcodes). (Live GPU is a separate endpoint — see below.)
- `POST /Admin/RestartLtx` runs [`tools/restart-ltx-server.ps1`](tools/restart-ltx-server.ps1) (kill the process on the port → wait for it to free → relaunch hidden with the same env/log contract as logon startup → wait for it to listen again). The page shows a client-side progress bar during the restart.
- `POST /Admin/StartAudio` / `POST /Admin/StopAudio` start/stop the local **Stable Audio** server (`AudioServerControl` → [`tools/run-audio-server.ps1`](tools/run-audio-server.ps1) / [`tools/stop-audio-server.ps1`](tools/stop-audio-server.ps1)). It's **not** an auto-start service, so the Admin page is where you bring it up on demand: Start is detached (the model loads in the background while the page polls until online), Stop frees its VRAM.
- `POST /Admin/CleanStaging` deletes staged uploads (the `_inputs`, `_upscale_inputs`, `_speed_inputs` dirs) and temp H.264 transcodes — never the finished output videos.
- `GET /Admin/SystemStats` is a lightweight live snapshot: **GPU** via nvidia-smi (name · VRAM · utilization · temperature), **CPU** utilization (sampled once a second by `SystemStatsService` via the Win32 `GetSystemTimes` API), and **system RAM** used/total (Win32 `GlobalMemoryStatusEx`). The shared layout footer polls it on an interval (default **1s**, configurable via `Gpu:PollIntervalMs`), so **every page shows live GPU + CPU + RAM usage**.

### 5. Speed Up — `SpeedController` + `VideoSpeedService`
Standalone page at `GET /Speed` to re-time **any** uploaded video (independent of upscaling).

- `POST /Speed/Run` stages the upload, then runs the same ffmpeg `setpts` re-time as above — but **keeps and re-times audio when present**: it probes for an audio stream with `ffprobe` and, if found, applies an `atempo` chain (chained because `atempo` only accepts 0.5–2.0 per instance, e.g. 3× → `atempo=2.0,atempo=1.5`). Output goes to the configured `VideoSpeed:OutputDirectory`.
- The page shows the result inline with **Download** and **Clear** buttons; `GET /Speed/Download?name=…` streams it (range-enabled, path-traversal guard). Uploads accepted up to 2 GB.

### 6. AI sound generation — `SoundGenController` + `SoundGenService`
Generate a sound effect from a text prompt using a **self-hosted Stable Audio Open** model (no API key, no per-call cost — runs on the local GPU), and use it as the audio track, from a panel on the Generate page.

- A small local Python server (`tools/audio_server.py`, launched by `tools/run-audio-server.ps1` on `127.0.0.1:8770`) loads Stable Audio Open via **diffusers** — pure PyTorch, so it runs on the Blackwell GPU with a torch cu128 build, avoiding the flash-attn/Apex deps that block other models. ClaudeCore calls it with a typed `HttpClient` (`LocalAudioClient`), mirroring the LTX integration.
- `POST /SoundGen/Generate` (prompt + optional duration) calls the server's `/generate`, which returns **WAV** bytes; the service writes them into the LTX input dir (`sfxgen_*.wav`) and returns the staged path. The LTX server already accepts WAV, so no conversion is needed. Duration is optional — a non-positive value lets the model choose, larger values are capped (`LocalAudio:MaxDurationSeconds`).
- The Generate form submits that path as `audioStagedPath`, and `VideoController` feeds it into the same audio-to-video path as an upload (validated to live inside the staging dir via `LtxVideoService.IsStagedInputFile`).
- `GET /SoundGen/Preview?name=…` streams a generated clip back so the page can play it inline before generating the video.
- `GET /SoundGen/Health` proxies the audio server's `/health`; if the server is down or the model hasn't loaded, the generator disables itself with a hint.
- **Requires the local audio server** (venv + weights) — see External dependencies and InstallationSteps.md. Start/stop it on demand from the **Admin** page (it's not an auto-start service).

---

## Components

| Component | Role |
|---|---|
| `LtxVideoClient` | Typed `HttpClient` over the LTX server REST API (`/health`, `/api/generate`, `/api/generation/progress`, `/api/generate/models-specs`). |
| `LtxVideoService` | Orchestrates a generation: stage image → call server → copy result to output dir. Path-traversal guards on download/reuse. |
| `LtxServerControl` | Port-listening check + restart of the LTX server process. |
| `AudioServerControl` | Port-listening check + detached start / scripted stop of the local Stable Audio server (Admin page). |
| `MaxineUpscaleService` | Runs `VideoEffectsApp.exe` per job; builds args per effect; sets up the DLL `PATH`. |
| `VideoSpeedService` | Runs ffmpeg `setpts` re-time; keeps + re-times audio (`atempo` chain, with `ffprobe` detection). Also `RestoreAudioAsync` re-muxes the source audio onto the video-only Maxine output. |
| `VideoProbe` | Reads MP4 display height (no external dependency) to choose a valid SuperRes factor. |
| `SoundGenService` / `LocalAudioClient` | Generate a sound effect from a text prompt via the self-hosted Stable Audio server (typed `HttpClient`) and stage the WAV into the LTX input dir for audio-to-video. |
| `LastImageStore` | Remembers the last starting image across requests/restarts (persisted to a state file). |
| `*Options` classes | Strongly-typed config bound from `appsettings.json`. |

---

## Configuration (`appsettings.json`)

| Section | Purpose | Key settings |
|---|---|---|
| `Kestrel:Endpoints` | HTTP binding | `http://127.0.0.1:80` + `:5080` |
| `Branding` | UI display name | `AppName` |
| `VideoDefaults` | Generate-form defaults | `Resolution`, `Duration`, `Fps`, `AspectRatio`, `CameraMotion`, `Audio` |
| `Gpu` | Live GPU footer readout | `PollIntervalMs` (default 1000) |
| `Ltx` | LTX server + I/O | `BaseUrl` (`:8765`), `GpuIndex` (CUDA device for the video model, default `0`), `OutputDirectory`, `InputDirectory`, `GenerationTimeoutMinutes`, `RestartScriptPath`, `RestartReadyDelayMs` (post-cancel "ready" delay) |
| `Maxine` | Upscaler exe + SDK | `ExecutablePath`, `ModelDir` (writable copy), `SdkBinDir`, `OpenCvBinDir`, `OutputDirectory`, `Codec` (`avc1`), `TimeoutMinutes` |
| `VideoSpeed` | ffmpeg re-time | `FfmpegPath`, `OutputDirectory`, `InputDirectory` (Speed page staging), `TimeoutMinutes` |
| `LocalAudio` | AI sound generation (self-hosted) | `BaseUrl` (`:8770`), `GpuIndex` (CUDA device for the audio model, default `1`), `MaxDurationSeconds`, `TimeoutMinutes`, `StartScriptPath`, `StopScriptPath` |

> `VideoSpeed:FfmpegPath` points at an absolute, **version-specific** path under the winget package folder; a `winget upgrade` of ffmpeg changes it and must be updated.

### Targeting separate GPUs for video and audio

On a multi-GPU machine the LTX **video** server and the Stable Audio **audio** server can each be pinned to their own card so both models stay resident at once (no per-generation reload, no shared-VRAM OOM). Each launcher exports `CUDA_VISIBLE_DEVICES`, so the model only ever sees its assigned GPU:

- `Ltx:GpuIndex` (default `0`) → passed to `run-ltx-server.ps1` / `restart-ltx-server.ps1` as `-Gpu`; the logon auto-start (`start-keithvision.ps1`) hard-codes the matching `0`.
- `LocalAudio:GpuIndex` (default `1`) → passed to `run-audio-server.ps1` as `-Gpu` when the Admin page starts it.

Swap the two indices (or set both to `0`) to change the mapping; the C# server-control services read these values and pass them through automatically. On a single-GPU box, set both to `0`.

---

## External dependencies (not in this repo)

These run on the host and are configured by path:

- **LTX Desktop** — provides the bundled Python engine and LTX-2.3 / Gemma weights; the inference server is launched from `tools/`.
- **NVIDIA Maxine Video Effects SDK** — redistributable (AI models + DLLs) plus the prebuilt `VideoEffectsApp.exe`.
- **ffmpeg** — `winget install Gyan.FFmpeg`.
- **NVIDIA GPU driver** + an RTX (Blackwell-class) GPU.
- **Stable Audio Open** *(optional)* — the model + Python venv behind AI sound generation; runs locally (no key, no per-call cost). Needs gated weights from Hugging Face and a dedicated `torch` cu128 venv, started via `tools/run-audio-server.ps1` (setup in InstallationSteps.md). Leave the server stopped and the generator stays disabled with a hint.

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
Controllers/      VideoController, UpscaleController, SpeedController, AdminController, SoundGenController, HomeController
Services/         orchestration + typed-HttpClient + *Options + VideoProbe + LastImageStore
Models/Ltx/       LTX request/response/health/progress DTOs
Models/SoundGen/  ElevenLabs sound-generation request + staged-clip records
Views/            Video, Upscale, Speed, Admin, Shared (_Layout)
tools/            PowerShell: run/restart LTX server, run/stop audio server, publish, host mapping; ltx_launch.py + audio_server.py
wwwroot/          static assets (Bootstrap, jQuery validation)
appsettings.json  all configuration
```
