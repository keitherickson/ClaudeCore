# LTX-2 Video Generation Integration

ClaudeCore talks to a **locally self-hosted LTX-2.3 inference server** to do
text-to-video and image-to-video, saving results to your local drive. It does
**not** depend on the LTX Desktop UI at runtime — it reuses the same engine and
model weights LTX Desktop already installed, but runs them as our own server on
a fixed port.

## Architecture

```
Browser ──HTTP──> ClaudeCore (ASP.NET, :5080/5001)
                      │  typed HttpClient (LtxVideoClient)
                      ▼
              LTX-2 server (FastAPI, 127.0.0.1:8765)   ← we launch this
                      │  loads ltx-2.3-22b-distilled
                      ▼
              RTX 5090  →  outputs\*.mp4  →  copied to OutputDirectory
```

- The server is the exact `ltx2_server.py` LTX Desktop ships, launched by us with
  auth disabled (localhost only) and pointed at the existing models/outputs dir.
- `POST /api/generate` is **synchronous** — it returns only when the video is
  finished, so the HttpClient timeout is set to `GenerationTimeoutMinutes` (30).
- The conditioning image is passed by **file path** (`imagePath`); the app stages
  the uploaded image to `InputDirectory` so the server can read it.

## Extend & stitch (longer-than-one-generation clips)

The server's REST API caps a single generation at the duration matrix (≤20s).
**Extend** chains several generations to get past that, on the client side:

1. Generate segment 1 (text- or image-to-video).
2. ffmpeg extracts that clip's **last frame** to a PNG
   (`VideoSpeedService.ExtractLastFrameAsync`).
3. That PNG becomes the **starting image** (`imagePath`, frame 0) of segment 2 —
   the *same* conditioning primitive image-to-video uses, so the clips continue.
4. Repeat for N segments, then ffmpeg concatenates them into one MP4
   (`VideoSpeedService.ConcatAsync`, re-encoded to H.264 across the seams).

`POST /Video/Extend` drives this (2–8 segments). `extraPrompts` (one line per
extension segment) gives a rough **prompt timeline**; blank lines reuse the main
prompt. Optional upscale/play-faster run once on the stitched result. Note this
is *not* the engine's native multi-keyframe Extend (the REST API exposes only a
single frame-0 conditioning image), but it reuses the identical primitive and
needs no backend changes. Uploaded/AI audio isn't supported in Extend (it can't
be sliced per segment); the synchronized-audio toggle still applies per clip.

## One-time / per-session: start the LTX server

> ⚠️ **Quit LTX Desktop first.** Both load the 22B model into VRAM and two copies
> won't fit on a single 32 GB GPU.

```powershell
C:\ClaudeCore\ClaudeCore\tools\run-ltx-server.ps1
```

Leave that window open (Ctrl+C to stop). First generation loads the model
(~tens of seconds); after that the model stays warm and only inference time is
spent per clip. Verify it's up:

```powershell
Invoke-RestMethod http://127.0.0.1:8765/health
```

## Run the app

```powershell
dotnet run --project C:\ClaudeCore\ClaudeCore\ClaudeCore.csproj
```

Open the app, click **Generate Video** in the nav (`/Video`):

- Enter a **prompt** → text-to-video.
- Also attach a **starting image** → image-to-video.
- Pick resolution / duration / fps / aspect / camera motion, optional audio.
- A progress bar polls `GET /api/generation/progress` while it runs.
- The finished `.mp4` is copied to `OutputDirectory` and previewed inline.

## Configuration (`appsettings.json` → `Ltx`)

| Key | Default | Meaning |
|-----|---------|---------|
| `BaseUrl` | `http://127.0.0.1:8765` | LTX server URL (must match `-Port` in the launcher). |
| `GpuIndex` | `0` | CUDA device the video model is pinned to (passed to the launcher as `-Gpu` → `CUDA_VISIBLE_DEVICES`). Keep distinct from `LocalAudio:GpuIndex` to put video and audio on separate GPUs. |
| `OutputDirectory` | `C:\Users\keith\Videos\LTX-Generated` | Where finished videos are saved. |
| `InputDirectory` | `…\LTX-Generated\_inputs` | Where uploaded images are staged for the server. |
| `GenerationTimeoutMinutes` | `30` | HttpClient timeout for a single generation. |

## Code map

| File | Role |
|------|------|
| `tools/run-ltx-server.ps1` / `tools/ltx_launch.py` | Launch our LTX server instance. |
| `Services/LtxVideoOptions.cs` | Bound config. |
| `Models/Ltx/LtxModels.cs` | Request/response DTOs matching the LTX API. |
| `Services/LtxVideoClient.cs` | Typed HttpClient over the LTX REST API. |
| `Services/LtxVideoService.cs` | Stages image, calls server, copies result to disk. |
| `Controllers/VideoController.cs` | `Index`, `Generate`, `Extend`, `Progress`, `Health`, `Download`. |
| `Views/Video/Index.cshtml` | Form + progress bar + inline preview. |

## Notes / limits

- Local generation uses the **fast distilled** pipeline; supported resolutions are
  540p / 720p / 1080p, aspect 16:9 or 9:16.
- The text encoder is the local Gemma model, so generation is fully offline.
- The `model: "pro"` option and the cloud path require an LTX API key + credits;
  this integration is wired for **local** generation.
