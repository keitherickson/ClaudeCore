# ClaudeCore — Installation & Setup Steps

A running record of everything done to set up this application, updated as we go.

**Last updated:** 2026-06-13

Status legend: ✅ done · ⏳ pending a manual step · ⏸️ deferred · 🔁 per-session

---

## Downloads index

Every external thing that has to be downloaded, in one place.

| What | Link | Used by | Status |
|---|---|---|---|
| .NET 10 SDK | https://dotnet.microsoft.com/download/dotnet/10.0 | the web app | ✅ present |
| Git for Windows | https://git-scm.com/download/win | source control | ✅ present |
| GitHub CLI (`gh`) | https://cli.github.com/ | push to GitHub | ✅ installed |
| Claude Code VS Code extension | https://marketplace.visualstudio.com/items?itemName=anthropic.claude-code | editor | ✅ installed |
| NVIDIA GPU driver (≥ 521.98) | https://www.nvidia.com/download/index.aspx | GPU | ✅ 610.47 |
| LTX Desktop (bundled engine + weights) | https://ltx.io/ltx-desktop · repo: https://github.com/Lightricks/LTX-Desktop | LTX generation | ✅ installed |
| LTX-2.3 model weights | https://huggingface.co/Lightricks/LTX-2.3 | LTX generation | ✅ downloaded |
| Gemma text encoder weights | https://huggingface.co/Lightricks/gemma-3-12b-it-qat-q4_0-unquantized | LTX generation | ✅ downloaded |
| ffmpeg (Gyan full build) | `winget install Gyan.FFmpeg` · https://www.gyan.dev/ffmpeg/builds/ | "play faster" re-time | ✅ installed (8.1.1) |
| NVIDIA Maxine Video Effects SDK redistributable | https://www.nvidia.com/broadcast-sdk-resources | upscaling | ✅ installed |
| Maxine VFX SDK source (samples/exe) | https://github.com/NVIDIA-Maxine/Maxine-VFX-SDK | upscaling | ✅ cloned |
| VS 2022 C++ Build Tools (fallback) | https://visualstudio.microsoft.com/downloads/ | optional recompile | ◻️ optional |

Deferred items' downloads are listed in section 6.

---

## Quick start — bootstrap installer

`install.ps1` automates this whole setup (prereqs incl. ffmpeg → LTX-2.3 → Maxine → build + configure). It's an **online bootstrapper**: it downloads from official sources and **guides you through the two EULA-gated installs** (LTX Desktop, Maxine redistributable), then continues. It also auto-detects the installed ffmpeg and writes its path into `appsettings.json` (`VideoSpeed:FfmpegPath`). Idempotent — safe to re-run.

```powershell
powershell -ExecutionPolicy Bypass -File C:\ClaudeCore\ClaudeCore\install.ps1
# flags: -SkipPrereqs (skip winget installs)  -SkipWeights (skip LTX weight download)
```

Sections 0–5 below are the same steps the installer performs, for reference / manual setup.

---

## 0. Environment

| | |
|---|---|
| Machine | Windows 11 Pro (x64) |
| GPU | NVIDIA GeForce RTX 5090 — 32 GB VRAM (Blackwell), driver 610.47 |
| App root | `C:\ClaudeCore\ClaudeCore` |
| Framework | ASP.NET Core MVC on **.NET 10** ([SDK](https://dotnet.microsoft.com/download/dotnet/10.0)) |
| Repo | https://github.com/keitherickson/ClaudeCore (public) |

---

## 1. Tooling & source control ✅

- **[Git for Windows](https://git-scm.com/download/win)** + identity set globally: `Keith Erickson <keitherickson@hotmail.com>`.
- **[GitHub CLI](https://cli.github.com/)** installed (`winget install GitHub.cli`) and authenticated (`gh auth login`, account `keitherickson`).
- Repo initialized on `main`, pushed to GitHub (public). See `GitHubInformation.MD` for the everyday loop.
- **Version 1** committed + tagged `v1` (commit `be79bd2`).
- (Editor) **[Claude Code VS Code extension](https://marketplace.visualstudio.com/items?itemName=anthropic.claude-code)** installed (`code --install-extension anthropic.claude-code`).

**Everyday save loop:**
```powershell
git add -A
git commit -m "..."
git push
```

---

## 2. Feature: LTX-2.3 local video generation ✅

Text-to-video and image-to-video, generated locally and saved to disk. Full detail in **`LTX-Integration.md`**.

**How it works:** ClaudeCore calls a self-hosted LTX-2.3 server (reuses **[LTX Desktop](https://ltx.io/ltx-desktop)**'s bundled Python engine + downloaded weights) over HTTP; the finished `.mp4` is copied to the output folder.

**Weights** (already downloaded by LTX Desktop):
- [Lightricks/LTX-2.3](https://huggingface.co/Lightricks/LTX-2.3) — video model + spatial upscaler
- [Lightricks/gemma-3-12b-it-qat-q4_0-unquantized](https://huggingface.co/Lightricks/gemma-3-12b-it-qat-q4_0-unquantized) — text encoder

**Run the LTX server** 🔁 (per session; quit LTX Desktop first — VRAM):
```powershell
C:\ClaudeCore\ClaudeCore\tools\run-ltx-server.ps1   # serves http://127.0.0.1:8765
```

**Config** — `appsettings.json` → `Ltx`: `BaseUrl`, `OutputDirectory` (`C:\Users\keith\Videos\LTX-Generated`), `InputDirectory`, `GenerationTimeoutMinutes`. Form defaults live in `appsettings.json` → `VideoDefaults` (`Resolution`, `Duration`, `Fps`, `AspectRatio`, `CameraMotion`, `Audio`). UI name in `Branding:AppName`.

**Notes:** local "fast" pipeline; resolution caps duration (1080p→5 s, 720p→10 s, 540p→20 s); 24 fps only. The form pulls this matrix live from the server.

**Integrated upscaling:** the Generate form has an optional **"Also upscale with NVIDIA Maxine"** toggle (factor 2×/3×/4×). When on, it generates + saves the original, reads its real height (`VideoProbe`), then runs Maxine SuperRes at `factor × height` and saves an **upscaled copy** too (in `…\LTX-Generated\upscaled`). Both are shown/downloadable. Requires the Maxine SDK (section 4).

---

## 3. App cleanup ✅

- Removed the scaffold **Home** and **Privacy** pages.
- Landing page is now **Generate Video** (`{controller=Video}` default route).
- `Home/Error` kept (backs the global exception handler).

---

## 4. Feature: NVIDIA Maxine video upscaling ✅ (validated end to end)

Super-resolution up to 4K on the RTX 5090 via the **[NVIDIA Maxine Video Effects SDK](https://github.com/NVIDIA-Maxine/Maxine-VFX-SDK)** (free SDK; Blackwell supported). ClaudeCore shells out to the SDK's `VideoEffectsApp.exe` per job.

**Setup performed:**
1. SDK source cloned to `C:\ClaudeCore\maxine-vfx` (includes the prebuilt `VideoEffectsApp.exe`). No compiling needed.
2. [Video Effects SDK redistributable](https://www.nvidia.com/broadcast-sdk-resources) installed (EULA accepted) to `C:\Program Files\NVIDIA Corporation\NVIDIA Video Effects` — provides the AI models + runtime DLLs.
3. **⚠️ Gotcha — models must live in a WRITABLE folder.** TensorRT builds/caches the engine on first run, and `Program Files` is read-only without admin (fails as `NVCV_ERR_FILE` "file could not be found"). Fix: copy the models out and point the app there:
   ```powershell
   robocopy "C:\Program Files\NVIDIA Corporation\NVIDIA Video Effects\models" "C:\ClaudeCore\maxine-models" /E
   ```
4. .NET wrapper: `MaxineUpscaleService`, `MaxineUpscaleOptions`, `UpscaleController`, `Views/Upscale/Index.cshtml`; nav link + `appsettings.json` → `Maxine`.

**Validated:** image SR (1080p→4K), 100-frame video SR (~5 s), and full `/Upscale` upload→4K download. `/Upscale` health badge shows "SDK ready".

**Config** — `appsettings.json` → `Maxine`:
- `ExecutablePath` `…\maxine-vfx\samples\VideoEffectsApp\VideoEffectsApp.exe`
- `ModelDir` **`C:\ClaudeCore\maxine-models`** (the writable copy)
- `SdkBinDir` `C:\Program Files\NVIDIA Corporation\NVIDIA Video Effects` (DLLs, added to PATH)
- `OpenCvBinDir`, `OutputDirectory` (`…\LTX-Generated\upscaled`), `InputDirectory`, `Codec` (`avc1` = H.264, browser-friendly), `TimeoutMinutes`

**Effects / limits:** `SuperRes` (model, mode 0=compressed / 1=clean) needs output height = an **integer 2×/3×/4×** of the source (e.g. 1080p→2160 only); `Upscale` (fast, `--strength`) supports 1.33/1.5/2/3/4×; `ArtifactReduction`.

**Optional "Play faster" (re-time):** both the Generate flow and the `/Upscale` page have a **Play faster** toggle (1.5×/2×/3×/4×) that runs **after** upscaling — it re-times the upscaled clip with **ffmpeg** (`setpts`, re-encoded to H.264) and saves a `…_xN.mp4` next to the upscaled file. Maxine output is video-only (OpenCV writer drops audio), so the re-time is video-only (`-an`). Needs ffmpeg (`winget install Gyan.FFmpeg`); the exe path is configurable via `appsettings.json` → `VideoSpeed:FfmpegPath`. Code: `VideoSpeedService` / `VideoSpeedOptions`. Validated the re-time command (5.04 s → 2.58 s at 2×).

**Optional/insurance:** [VS 2022 C++ Build Tools](https://visualstudio.microsoft.com/downloads/) were installed as a fallback in case the prebuilt exe ever needs recompiling — turned out unnecessary.

---

## 5. Running the application 🔁

```powershell
# 1. (for generation) start the LTX server in its own window — LTX Desktop closed
C:\ClaudeCore\ClaudeCore\tools\run-ltx-server.ps1

# 2. start the web app
dotnet run --project C:\ClaudeCore\ClaudeCore\ClaudeCore.csproj
# then open the URL it prints (dev: http://127.0.0.1:5080 when ASPNETCORE_URLS is set)
```

Pages: **Generate Video** (`/Video`, landing) · **Upscale Video** (`/Upscale`).

> The Maxine upscaler runs in-process (shells out to the exe per job) — no separate server to start.

---

## 5b. Auto-start at Windows logon ✅

A Task Scheduler task **"KeithVision Startup"** (logon trigger, runs as the current user, hidden) launches both services in the background via `tools/start-keithvision.ps1`:
- LTX-2.3 server on `:8765`
- the web app (published build) on `http://127.0.0.1:5080`

Setup performed:
1. Published a Release build: `dotnet publish ClaudeCore.csproj -c Release -o C:\ClaudeCore\KeithVision`.
2. `tools/start-keithvision.ps1` starts each service only if its port isn't already listening; logs to `C:\ClaudeCore\logs`.
3. Registered the task (`Register-ScheduledTask`, no elevation needed).

> ⚠️ **The task runs the PUBLISHED build**, not the live source. After code changes, re-publish so startup picks them up — use the helper script (stops the app, publishes, relaunches):
> ```powershell
> powershell -ExecutionPolicy Bypass -File C:\ClaudeCore\ClaudeCore\tools\publish-keithvision.ps1
> ```
> Keep **LTX Desktop closed** (VRAM).

Manage it:
```powershell
Start-ScheduledTask  -TaskName "KeithVision Startup"   # run now
Disable-ScheduledTask -TaskName "KeithVision Startup"  # stop auto-start
Unregister-ScheduledTask -TaskName "KeithVision Startup" -Confirm:$false  # remove
```

---

## 5c. Local domain — http://www.keithvision.com (this PC) ✅ / ⏳

Reach the app at a friendly name **on this machine** (not the public internet; no domain ownership needed).

- **App side (done):** binds **port 80** + 5080 via `appsettings.json` → `Kestrel:Endpoints`, and accepts the host (AllowedHosts `*`). Verified `Host: www.keithvision.com` on `:80` → HTTP 200.
- **⏳ One admin step (you):** add the hosts mapping `127.0.0.1 → www.keithvision.com`. From an **elevated** PowerShell:
  ```powershell
  powershell -ExecutionPolicy Bypass -File C:\ClaudeCore\ClaudeCore\tools\add-keithvision-host.ps1
  ```
  Then open **http://www.keithvision.com**.

> Only this PC resolves the name (hosts file). For other devices or the public internet you'd need real DNS + exposure + TLS + a login (not set up).

---

## 5d. Admin dashboard — /Admin ✅

A local operations page (nav link **Admin**) showing the health of each moving part, plus a one-click recovery for the LTX server.

- **What it shows** (`/Admin`, polled from `/Admin/Status`): LTX server — reachable, port `:8765` listening, model loaded, GPU/VRAM (from `/health`); Maxine — SDK ready/not-ready; Web App — assembly version, machine, uptime; Output disk — free/total GB on the output drive.
- **Restart LTX server** button → `POST /Admin/RestartLtx` runs `tools/restart-ltx-server.ps1`: kills whatever holds the port, waits for it to free, relaunches the server hidden with the same env/log contract as `start-keithvision.ps1`, then waits for the port to listen again (~30 s). Recovers a hung/stopped server without touching LTX Desktop, while the web app keeps running.
- **No separate auth** — Kestrel binds `127.0.0.1`, same localhost trust model as the rest of the site.

**Code:** `AdminController`, `LtxServerControl` (port check + restart), `LtxVideoClient.GetHealthRawAsync`, `Views/Admin/Index.cshtml`; registered `LtxServerControl` (singleton) in `Program.cs`; nav link in `_Layout`. **Config** — `appsettings.json` → `Ltx:RestartScriptPath` (`…\tools\restart-ltx-server.ps1`).

**Validated:** `/Admin` HTTP 200; `/Admin/Status` returns live app/Maxine/disk health (LTX correctly shown offline when its server isn't running). Restart-button path not yet exercised on a live server.

---

## 6. Deferred / future ⏸️

- **SeedVR2 upscaling** — paused (Blackwell + ~80 GB VRAM blockers on the official repo). Links if revisited: source [ByteDance-Seed/SeedVR](https://github.com/ByteDance-Seed/SeedVR), weights [ByteDance-Seed/SeedVR2-3B](https://huggingface.co/ByteDance-Seed/SeedVR2-3B). Maxine (section 4) chosen instead.

---

## Change log

- **2026-06-13** — Initial setup: git/GitHub + CLI, .NET app to GitHub (Version 1). LTX-2.3 generation integrated. Home/Privacy removed; Video is landing page. Resolution-aware duration/fps. NVIDIA Maxine upscaling wrapper added. Document created; added a Downloads index with links to every prerequisite.
- **2026-06-13** — Maxine SDK redistributable installed; **validated upscaling end to end** (image + video + `/Upscale` pipeline) on the RTX 5090. Found the writable-models-dir gotcha (copied models to `C:\ClaudeCore\maxine-models`); added configurable output `Codec` (avc1). Section 4 marked ✅.
- **2026-06-13** — Built the **`install.ps1` bootstrap installer** (prereqs → LTX-2.3 → Maxine → build/configure), including the upscaling setup. Dry-run validated on this machine (all steps detected/built, "Setup complete"). Added a Quick start section; moved the installer out of Deferred.
- **2026-06-13** — **Integrated upscaling into the Generate flow**: optional toggle generates + saves the original, then auto-upscales (Maxine SuperRes, factor 2/3/4×) and saves an upscaled copy. Added `VideoProbe` (mp4 height reader) to pick a valid factor. Validated end to end (540p generate + 2× → original 0.5 MB + upscaled 22.9 MB in ~139 s).
- **2026-06-13** — Config-driven UI: `Branding:AppName` (display name "KeithVision"), `VideoDefaults` (form defaults: 540p / 20s / focus_shift / audio on). Full dark theme. Smoothed the generation progress bar (client-side estimate, since the LTX engine doesn't report per-step progress).
- **2026-06-13** — Persistence: prompt remembered in browser localStorage; last starting image remembered **server-side** (`LastImageStore`, persisted to a state file) and **reused automatically** for the next generation. Both saved only when a generation completes.
- **2026-06-13** — **Auto-start at logon**: published a Release build to `C:\ClaudeCore\KeithVision`, added `tools/start-keithvision.ps1`, and registered the "KeithVision Startup" scheduled task (web app + LTX server, background). Validated via Start-ScheduledTask (both services up, result 0x0).
- **2026-06-13** — Added `tools/publish-keithvision.ps1` (+ `.cmd`) one-command re-publish; "Clear Video" button on the result; **local domain**: app binds port 80 (Kestrel config), `tools/add-keithvision-host.ps1` maps www.keithvision.com → 127.0.0.1 (run as admin) for this-PC access.
- **2026-06-13** — **Admin dashboard** (`/Admin`): live health of LTX server (port/model/GPU/VRAM), Maxine SDK readiness, web-app version/uptime, and output-disk space, plus a **Restart LTX server** button (`tools/restart-ltx-server.ps1`) to recover a hung server in place. New `AdminController` + `LtxServerControl`; `Ltx:RestartScriptPath` config. Validated `/Admin` + `/Admin/Status` (LTX shown offline correctly when its server is down). Added a client-side progress bar to the restart (shows during, hides when done). All buttons standardized to `btn-primary`.
- **2026-06-13** — **"Play faster" re-time**: optional toggle (1.5×/2×/3×/4×) on the Generate flow and `/Upscale` that runs *after* upscaling, re-timing the upscaled clip with **ffmpeg** (`setpts`, H.264) and saving a `…_xN.mp4`. Installed ffmpeg (winget Gyan.FFmpeg 8.1.1); new `VideoSpeedService`/`VideoSpeedOptions` + `VideoSpeed:FfmpegPath` config. Validated the re-time (5.04 s → 2.58 s at 2×).
- **2026-06-13** — **Installer updated for ffmpeg**: `install.ps1` now installs ffmpeg (winget Gyan.FFmpeg) in the prereqs step and auto-detects its absolute path (PATH or the WinGet package folder), writing it into `appsettings.json` → `VideoSpeed:FfmpegPath` during configure. Added a technical root `README.md`.
- **2026-06-13** — **Standalone Speed Up page** (`/Speed`): upload any video, pick a speed (1.5×/2×/3×/4×), get a re-timed copy shown inline with Download + Clear. New `SpeedController`; `VideoSpeedService` extended to stage uploads, write to `VideoSpeed:OutputDirectory`, and **keep + re-time audio** when present (`ffprobe` detection + chained `atempo`). Nav link added. Validated end to end through the live app (upload → x2 → download) and the audio path (5.04 s → 1.75 s at 3× with AAC audio).
- **2026-06-14** — **Upscale preserves audio**: Maxine's OpenCV writer emits a video-only file, so upscaled clips lost their sound. Added `VideoSpeedService.RestoreAudioAsync` to re-mux the original upload's audio onto the upscaled video (`-c:v copy`, audio → AAC), called from `UpscaleController` after upscaling; `RetimeAsync` now keeps + `atempo`s audio so "Play faster" stays in sync. Verified end to end (AAC source → silent Maxine output → audio restored, exit 0). Re-published.
