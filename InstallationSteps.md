# ClaudeCore — Installation & Setup Steps

A running record of everything done to set up this application, updated as we go.

**Last updated:** 2026-06-18

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

## 7. Feature (in progress): LTX-2 NVFP4 via ComfyUI ⏳

Goal: run LTX-2 in **NVFP4** to exploit the 5090's native FP4 tensor cores (~2–3× over the current BF16 path) for video generation. Current `ltx-2.3-22b-distilled.safetensors` is **BF16** (verified from the safetensors header: 5,657 BF16 + 290 F32 tensors), and LTX Desktop's bundled engine has no NVFP4 loader — so this path means a **separate ComfyUI install** with the NVFP4 model, validated standalone *before* wiring ClaudeCore to it.

Plan: validate each model standalone in ComfyUI first (the make-or-break is the cu130 PyTorch build engaging real low-precision kernels vs. silent emulation), then add a `ComfyUiVideoBackend` behind a new `ILtxVideoBackend` interface so `LtxVideoService`/`VideoController` are unchanged and the existing `ltx2_server.py` stays as one of the selectable backends.

**End goal — multiple models + an /Admin switch:** host several LTX models and pick which the Generate flow uses *at runtime* from the Admin page (not a config/restart choice): (1) **LTX-2.3 BF16** (existing LTX Desktop server), (2) **LTX-2.3 FP8** (ComfyUI — installing now, the safe Blackwell speedup), (3) **LTX-2 19B NVFP4/FP4** (ComfyUI — *next*; true FP4 only exists on the older 19B line, full ~2–3× payoff but older/lower quality). One ComfyUI server can host both ComfyUI models; the active model is persisted to a state file and toggled on `/Admin`, with `ComfyUiVideoBackend` mapping the selection → workflow + checkpoint + encoder.

- **Env**: isolated venv at `C:\ComfyUI\venv` (Python 3.11.9), fully separate from LTX Desktop's Python and the audio venv.
- **Watch list (open Blackwell bugs)**: ComfyUI [#11864](https://github.com/Comfy-Org/ComfyUI/issues/11864) (NVFP4 load failure on 5090), [#11920](https://github.com/Comfy-Org/ComfyUI/issues/11920) (Gemma-3 update breaks LTX-2 FP8/framing — same encoder we use), LTXVideo [#335](https://github.com/Lightricks/ComfyUI-LTXVideo/issues/335) (NVFP4 slower than spec).
- **Downloads still needed** (Step 2): NVFP4 transformer → `ComfyUI/models/checkpoints/`, Gemma text encoder → `models/text_encoders/`, LTX-2 VAE → `models/vae/`. Custom nodes: [Lightricks/ComfyUI-LTXVideo](https://github.com/Lightricks/ComfyUI-LTXVideo).

### Step 0 — environment prerequisite ✅ (verified)

The single make-or-break: NVFP4 only engages on a **cu130** PyTorch build with **sm_120** kernels. Created `C:\ComfyUI\venv` and installed **torch `2.14.0.dev20260618+cu130`** (`pip install --pre torch torchvision torchaudio --index-url https://download.pytorch.org/whl/nightly/cu130`). Verified with `C:\ComfyUI\verify_gpu.py`: `torch.version.cuda` = **13.0**, `is_available()` **True**, device **RTX 5090**, capability **(12, 0) / sm_120**, `get_arch_list()` includes **`sm_120`**, and a real bf16 matmul executed on the GPU. The emulation-risk gate is clear. Recreate: `py -3.11 -m venv C:\ComfyUI\venv` then install the same cu130 nightly.

### Step 1 — ComfyUI + LTX nodes ✅ (nodes import clean)

Cloned ComfyUI → `C:\ComfyUI\ComfyUI` and [Lightricks/ComfyUI-LTXVideo](https://github.com/Lightricks/ComfyUI-LTXVideo) → `custom_nodes\`. Installed deps from a **torch-stripped** copy of ComfyUI's `requirements.txt` (`C:\ComfyUI\req-comfyui-notorch.txt` — removed the bare `torch`/`torchvision`/`torchaudio` lines so the cu130 nightly couldn't be downgraded; kept `torchsde`) plus the LTX node requirements. Confirmed after: torch still `…+cu130`, `pip check` clean, and `main.py --quick-test-for-ci --cpu` loads every node with **no IMPORT FAILED**.

- **Gotcha fixed:** the LTX pack failed to import — `cannot import name 'pad' from 'kornia.geometry.transform.pyramid'` (kornia 0.8.3 no longer re-exports `pad`). Patched `custom_nodes\ComfyUI-LTXVideo\pyramid_blending.py` to `from torch.nn.functional import pad` instead (kornia's `pad` *was* `F.pad`; the same file already calls `F.pad` identically). ⚠️ Local edit to a git-tracked custom node — re-apply if the node pack is updated.
- **Watch:** `triton` is absent (`No module named 'triton'`) — fine for node import, but some FP4/Blackwell kernels may want it; revisit at Step 5 if NVFP4 underperforms (install `triton-windows`).

### Step 2 — model files ⏳ (FP8-2.3 done, FP4-19B downloading)

Both target repos turned out **ungated** (no HF login/license wall). Downloaded with `HF_HUB_DISABLE_XET=1` (the xet backend stalls on this box — same lesson as the audio setup). The node pack's example workflows load an **all-in-one checkpoint via `CheckpointLoaderSimple`** (VAE bundled), so no separate VAE download is needed for either line.

- **Model #2 — LTX-2.3 FP8** ✅ downloaded:
  - `checkpoints\ltx-2.3-22b-distilled-fp8.safetensors` (27.5 GiB) — [Lightricks/LTX-2.3-fp8](https://huggingface.co/Lightricks/LTX-2.3-fp8).
  - `text_encoders\gemma_3_12B_it_fp8_scaled.safetensors` (12.3 GiB) — [Comfy-Org/ltx-2](https://huggingface.co/Comfy-Org/ltx-2) (the 2.3 workflow's `LTXAVTextEncoderLoader`).
- **Model #3 — LTX-2 19B distilled NVFP4** ⏳ downloading:
  - `checkpoints\ltx-2-19b-distilled-nvfp4-fixed-calibrated-v2.safetensors` (~20 GB) — community [szwagros/ltx-2-dist-nvfp4](https://huggingface.co/szwagros/ltx-2-dist-nvfp4) (no official *distilled* FP4 exists; Lightricks only ships dev-fp4). 20 GB ⇒ all-in-one (VAE bundled).
  - **Gemma reused, not re-downloaded:** the 19B workflow's `LTXVGemmaCLIPModelLoader` wants the multi-shard `gemma-3-12b-it-qat-q4_0-unquantized` folder, which already exists at `C:\Users\keith\AppData\Local\LTXDesktop\models\` — wire it in at Step 3 (symlink or `extra_model_paths.yaml`).

### Steps 3+5 — FP8-2.3 validated end-to-end, with a speed caveat ✅/⚠️

Drove ComfyUI **headlessly via its REST API** (no browser): launch `main.py --listen 127.0.0.1 --port 8188`, then a Python script (`C:\ComfyUI\run_val.py`) posts an API-format graph to `/prompt` and polls `/history`. Authored a lean **13-node t2v graph** (CheckpointLoaderSimple→LTXAVTextEncoderLoader→CLIPTextEncode×2→LTXVConditioning→EmptyLTXVLatentVideo→RandomNoise/KSamplerSelect/LTXVScheduler→CFGGuider→SamplerCustomAdvanced→LTXVTiledVAEDecode→SaveWEBM) instead of converting the 44-node example. 1280×704, 97 frames (~4 s), 8 steps, cfg 1.0, euler.

- **Works:** generated a clean clip — vp9, 1280×704, **97 frames**, 1.31 MB (real content, not black/frozen). Cold run 57 s (incl. ~40 GB model load); **warm ~36 s** (use a fresh seed — identical seed/prompt returns ComfyUI's cached output in ~3 s and is NOT a real measurement).
- **Speed caveat (key finding):** ComfyUI logs `model weight dtype torch.bfloat16, manual cast: torch.bfloat16` — the Lightricks all-in-one FP8 checkpoint via `CheckpointLoaderSimple` runs **bf16 compute** (fp8 = storage/VRAM only, ~no speedup). `--fast` did **not** change this (warm 42 s). True fp8 *compute* for LTX needs the `LTXQ8Patch` node + the **`q8_kernels`** package ([Lightricks/LTX-Video-Q8-Kernels](https://github.com/Lightricks/LTX-Video-Q8-Kernels)) — not installed; it's a Blackwell/sm_120 compile (the usual wall).
- **Strategic upside:** the build already exposes **native `scaled_mm_nvfp4` / `mxfp8` kernels** (`INFO Native ops: nvfp4, mxfp8, float8_e5m2, float8_e4m3fn`). So the **NVFP4 (FP4) model — the actual speed target — is natively accelerated on this stack without needing q8_kernels**, unlike fp8. Validates the instinct to chase FP4.

### FP4-19B validation — BLOCKED by ComfyUI #11864 (loader dequantizes NVFP4) ⛔

Wired the 19B path: FP4 checkpoint `ltx-2-19b-distilled-nvfp4-fixed-calibrated-v2.safetensors` (18.6 GiB, community [szwagros](https://huggingface.co/szwagros/ltx-2-dist-nvfp4)) in `checkpoints\`; existing `gemma-3-12b-it-qat-q4_0-unquantized` folder exposed via `extra_model_paths.yaml` (admin-symlink was blocked) and selected with `LTXVGemmaCLIPModelLoader`. Authored `C:\ComfyUI\run_val_fp4.py` (same lean t2v graph, Gemma-CLIP loader swapped in).

**Result: OOM, root-caused to the open bug, not our setup.** ComfyUI logs `model weight dtype torch.bfloat16, manual cast: None` — `CheckpointLoaderSimple` **dequantizes the NVFP4 weights to bf16 on load**, so the 19B took ~30 GB (not the ~10 GB FP4 should), hit `Free: 0 bytes`, and OOM'd at text-encode. This is [ComfyUI #11864](https://github.com/Comfy-Org/ComfyUI/issues/11864) (NVFP4 loading failure on RTX 5090). Installed `triton-windows 3.7.0` (the noted Step-1 watch-item) and retried — no change; backend dump shows the FP4 **GEMM kernels ARE ready** (`comfy_kitchen` *eager* + *cuda* backends enabled, both expose `scaled_mm_nvfp4`; only the *triton* sub-backend is `disabled`). So **FP4 compute is available — the blocker is purely the load path keeping weights quantized.**

Open options (not yet chosen): (a) load as a **diffusion_model via UNETLoader** from `models\diffusion_models\` instead of `CheckpointLoaderSimple` (may preserve quantization); (b) try the **official** `Lightricks/LTX-2/ltx-2-19b-dev-fp4.safetensors` (proper NVFP4 metadata ComfyUI may recognize) vs the community file; (c) pin to a known-good ComfyUI commit / wait for the #11864 fix; (d) stopgap: shrink the text encoder + `--lowvram` to at least run it dequantized (bf16, no FP4 speedup). FP8-2.3 (above) remains the working ComfyUI model meanwhile.

**RESOLVED 2026-06-19 — FP4-19B works end-to-end** (official dev-fp4 + UNETLoader + checkpoint VAE). Downloaded the **official** `Lightricks/LTX-2/ltx-2-19b-dev-fp4.safetensors` (18.6 GiB, ungated). Working graph (`C:\ComfyUI\run_val_fp4_dev.py`): **`UNETLoader`** (weight_dtype=default) for the FP4 transformer + **`LTXVGemmaCLIPModelLoader`** (gemma folder, `ltxv_path`=this file — its connector emits the correct 2048 dim, fixing the community file's mismatch) + **`CheckpointLoaderSimple`** used *only for its VAE output* (slot 2; builds a proper LTX video VAE with `downscale_index_formula`, which the generic `VAELoader` does not — its MODEL output left unconnected so the dequantized transformer never reaches the GPU). Validated: `status=success`, 1280×704 / 97-frame clip, **16.5 GB VRAM resident** (⇒ genuinely FP4, not the ~38 GB bf16 dequant), warm **66 s** for 25 steps + cfg 3.0. **Per-pass ~1.3 s vs the FP8/bf16 path's ~4.5 s — FP4 compute is ~3× faster per step** (the Blackwell payoff); dev is slower *overall* only because it's non-distilled (50 passes). Next optimization: a distilled-FP4 transformer (community szwagros) + this file's connector + this VAE would cut passes ~6× → est. ~11 s. Notes: `triton` backend still `disabled` but `eager`/`cuda` cover NVFP4; `LTXVPatcherVAE` is unusable (needs `q8_kernels`).

**BETTER SOURCE found 2026-06-19 — official `Lightricks/LTX-2.3-nvfp4`** (`ltx-2.3-22b-dev-nvfp4.safetensors`, 21.7 GB, ungated, Mar-2026). Supersedes the 19B: newest 2.3 model **with audio-video**. Validated WORKING via the same pattern (UNETLoader + CheckpointLoaderSimple-for-VAE + `LTXAVTextEncoderLoader` fp8 gemma) **plus the official distilled LoRA** `ltx-2.3-22b-distilled-lora-384-1.1.safetensors` (7 GB, `LoraLoaderModelOnly`, 8 steps cfg 1.0). Generates clean 1280×704/97f clips, FP4 engaged (15.4 GB resident). **Speed caveat — NOT yet the projected ~11 s: warm ~45–51 s.** Diagnosed: (1) 97→25 frames barely changed time (51→45 s) ⇒ VAE decode is *not* the bottleneck; (2) ~45 s is fixed per-run cost — ComfyUI's **DynamicVRAM re-stages the 16.7 GB transformer + re-applies 1660 distilled-LoRA patches every run** (`Model LTXAV ... 16744MB Staged. 1660 patches attached`). Swapping the unquantized→fp8 gemma did NOT help (so gemma wasn't it). `--highvram` to force-resident **crashed/OOM'd** because the CheckpointLoaderSimple-for-VAE trick also loads the full dequantized transformer (fine when CPU-offloaded, fatal when forced resident). **Open optimization to hit ~11 s:** (i) source the VAE WITHOUT loading the full checkpoint (standalone LTX VAE / extract VAE keys) so the bloated model never loads, then keep the FP4 transformer resident; (ii) bake the distilled LoRA into the weights once instead of 1660 patches/run. Until then FP4-2.3 *works* at ~50 s (FP4 sampling itself is fast; the tax is staging/patching).

**Earlier attempt (community distilled file) — option (a): it CLEARS the #11864 OOM.** Loaded the NVFP4 transformer via `UNETLoader` (`weight_dtype=default`) + pulled the VAE from the same all-in-one file via `VAELoader` (both exposed from `checkpoints\` through a second `extra_model_paths.yaml` entry — no copy/download). The run got **past model-load and text-encode all the way to the sampler** (no OOM) before hitting a *different* error: `mat1 and mat2 shapes cannot be multiplied (11440x4096 and 2048x4096)` in `SamplerCustomAdvanced`. Root cause: the 19B example points `LTXVGemmaCLIPModelLoader.ltxv_path` at the **BF16 19B checkpoint** (`ltx-2-19b-distilled.safetensors`) — which holds the gemma→cross-attn **text-projection/connector** weights — whereas I pointed it at the NVFP4 file, whose connector emits 4096-dim where the 19B transformer wants 2048. So the loader/OOM blocker is solved; the remaining gap is the **text-connector config**, which likely needs the official 19B workflow's setup (and possibly the BF16 19B checkpoint, ~43 GB, just for the connector). At that point it's no longer "quick/no-download." Recommended pivot: build the C# /Admin switch against the two working models (BF16-2.3, FP8-2.3) and slot FP4 in once the connector is sorted or #11864 is fixed upstream.

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
- **2026-06-15** — **AI sound generation (self-hosted Stable Audio Open)**: the Generate page can generate a sound effect from a text prompt on the local GPU and use it as the audio track for audio-to-video — no API key, no per-call cost. New local Python server `tools/audio_server.py` (FastAPI + diffusers `StableAudioPipeline`) launched by `tools/run-audio-server.ps1` on `127.0.0.1:8770`, serving `GET /health` + `POST /generate {text,seconds}` → WAV; ClaudeCore calls it via a typed `LocalAudioClient` (mirrors the LTX integration). `SoundGenController`/`SoundGenService` write `sfxgen_*.wav` into the LTX input dir; Generate uses it via `audioStagedPath` (an uploaded file still wins). `LtxVideoService.IsStagedInputImage` generalized to `IsStagedInputFile`. Chose diffusers (pure PyTorch) specifically to dodge the Blackwell/sm_120 flash-attn+Apex blockers that paused SeedVR2. .NET side builds clean.
- **2026-06-15** — **AI sound env (steps 1–2 done, Blackwell verified)**: installed **Python 3.11.9** (none was present — only the Windows Store stub), created the venv at `C:\ClaudeCore\audio-venv`, and `pip install`ed **torch 2.11.0+cu128** + numpy/diffusers (0.38.0)/transformers/accelerate/soundfile/fastapi/uvicorn. Verified `torch.cuda.is_available()` True, device RTX 5090, capability **(12, 0)** with **sm_120** in `torch.cuda.get_arch_list()` — the flash-attn/Apex wall that paused SeedVR2 does not apply to this diffusers stack. `StableAudioPipeline` imports cleanly.
- **2026-06-15** — **AI sound server live (Stable Audio Open 1.0 generating on GPU)** ✅: finished the self-host setup. Logged in to Hugging Face with the new `hf` CLI (`hf auth login`; `huggingface-cli` is deprecated). Switched the default model to **`stabilityai/stable-audio-open-1.0`** — the `-small` checkpoint ships in stable-audio-tools format (no `model_index.json`), so `StableAudioPipeline` can't load it; 1.0 has the full diffusers layout. **Two gotchas resolved:** (1) the HF **`hf_xet`** fast-transfer backend **stalled** mid-download (a file stuck at 0 bytes, no progress, no open connections) → set **`HF_HUB_DISABLE_XET=1`** in `tools/run-audio-server.ps1` to use the plain HTTPS downloader; (2) the Stable Audio scheduler needs **`torchsde`** (`CosineDPMSolverMultistepScheduler`) → `pip install torchsde` into the venv. Note 1.0's license is **separately gated** from -small (must accept both pages). Model loads on CUDA (RTX 5090); `POST /generate` produced a valid clip — *"distant thunder…"*, 5.0 s, 44.1 kHz stereo, **6.1 s** to generate at 100 steps. Weights cached (~9.7 GB) under `C:\Users\keith\.cache\huggingface`. Verified through the app: `/SoundGen/Health`, `/SoundGen/Generate` (WAV staged into the LTX input dir), and `/SoundGen/Preview` all pass. Committed + pushed on branch `feature/selfhosted-audio`.
- **2026-06-15** — **Admin start/stop for the audio server** ✅: added `AudioServerControl` (port check + **detached start** via `run-audio-server.ps1` + **scripted stop** via new `tools/stop-audio-server.ps1`, which kills the `audio_server.py` python by command line and the port owner) and an "AI Sound Server (Stable Audio)" card on `/Admin` with status + Start/Stop buttons (`POST /Admin/StartAudio` / `StopAudio`, antiforgery). `/Admin/Status` now reports the audio server's port-listening + model-loaded state; `SoundGenService.GetHealthAsync` got a 5s cap so a wedged server can't block the dashboard. Kept **not** auto-start on logon — start on demand from Admin. Verified end to end on the dev build: Status / Stop / Start all work, and the Admin-started server reached `model_loaded` on CUDA.
- **2026-06-16** — Froze the audio venv to **`tools/audio-requirements.txt`** (exact pins, incl. `torch==2.11.0+cu128` via `--extra-index-url https://download.pytorch.org/whl/cu128` — the CUDA 12.8/Blackwell wheel isn't on PyPI). Recreate with `py -3.11 -m venv C:\ClaudeCore\audio-venv` then `…\python -m pip install -r tools\audio-requirements.txt`. Guards against version drift (transformers 5.x / diffusers churn, the `torchsde` dep) if the env is ever rebuilt. Also revoked + cleared the temporary HF token used for the gated download (`hf auth logout`).
- **2026-06-18** — **LTX-2 NVFP4 (ComfyUI) — Step 0 done**: starting the FP4 path to exploit the 5090's native FP4 cores (~2–3× over today's BF16 LTX). Confirmed the shipped `ltx-2.3-22b-distilled.safetensors` is BF16 (header: 5,657 BF16 + 290 F32), so NVFP4 needs a separate ComfyUI install. Built the isolated env: venv `C:\ComfyUI\venv` (Python 3.11.9) + **torch 2.14.0.dev20260618+cu130** nightly. Verified the make-or-break gate — cu130, RTX 5090, capability (12,0)/**sm_120** present in `get_arch_list()`, real GPU matmul — so FP4 will run natively, not emulated. Plan: validate the NVFP4 workflow in ComfyUI's UI, then add a `ComfyUiVideoBackend` behind a new `ILtxVideoBackend` interface (existing `ltx2_server.py` stays as a config-selectable fallback). See section 7. Watching ComfyUI #11864/#11920 and LTXVideo #335 on Blackwell.
- **2026-06-18** — **LTX-2 NVFP4 (ComfyUI) — Step 1 done**: cloned ComfyUI + the Lightricks LTX node pack into `C:\ComfyUI\ComfyUI`; installed all deps without disturbing the cu130 nightly torch (installed from a torch-stripped requirements copy + verified torch unchanged and `pip check` clean). Hit and fixed a kornia 0.8.3 incompatibility in the LTX nodes (`pad` no longer re-exported from `kornia.geometry.transform.pyramid`) by sourcing `pad` from `torch.nn.functional` in `pyramid_blending.py` — a local edit to a git-tracked node, re-apply on update. `main.py --quick-test-for-ci` now imports every node with no failures. `triton` missing noted as a Step-5 watch item. See section 7.
- **2026-06-18** — **LTX-2 NVFP4 (ComfyUI) — Steps 3+5, FP8-2.3 validated end-to-end (speed caveat)**: drove ComfyUI headlessly via its REST API with a hand-authored 13-node t2v graph; generated a clean 1280×704 / 97-frame clip on the 5090 (warm ~36 s, 8 steps). Key finding: the Lightricks all-in-one FP8 checkpoint via `CheckpointLoaderSimple` runs **bf16 compute** (`model weight dtype torch.bfloat16, manual cast`) — fp8 buys VRAM, not speed; `--fast` doesn't change it. Real fp8 compute needs `LTXQ8Patch` + the separately-compiled `q8_kernels` (Blackwell wall). Upside: the build exposes **native `scaled_mm_nvfp4` kernels**, so the FP4 model (the real speed target) should accelerate natively without q8_kernels. See section 7.
- **2026-06-18** — **LTX-2 NVFP4 (ComfyUI) — FP4-19B blocked by ComfyUI #11864**: wired the 19B NVFP4 checkpoint (community szwagros) + the existing Gemma folder (via `extra_model_paths.yaml`) and ran the t2v graph → OOM. Root-caused: `CheckpointLoaderSimple` **dequantizes the NVFP4 weights to bf16 on load** (`model weight dtype torch.bfloat16, manual cast: None`), bloating the 19B to ~30 GB → 0 bytes free → OOM. This is the open RTX-5090 bug #11864, not our config. Installed `triton-windows 3.7.0` (Step-1 watch item) — no change; the FP4 GEMM kernels are confirmed ready (`comfy_kitchen` eager+cuda backends expose `scaled_mm_nvfp4`), so the blocker is the load path, not compute. Options logged in section 7 (UNETLoader/diffusion_models load, official dev-fp4 file, known-good commit, or low-vram dequantized stopgap). FP8-2.3 remains the working ComfyUI model.
- **2026-06-19** — **/Admin model switch built (BF16-2.3 default + NVFP4-2.3 fast)** ✅: added `ILtxVideoBackend` (interface) with two impls — `LtxVideoClient` (Key "LtxDesktop", the existing BF16 LTX Desktop REST server) and new `ComfyUiVideoBackend` (Key "ComfyUI", builds the validated NVFP4 t2v graph, submits to ComfyUI `/prompt`, polls `/history`, downloads via `/view`, saves `.mp4` so the existing H.264 normalization applies). `LtxVideoService` now routes Generate/Progress/Health/Specs to whichever model `ActiveModelStore` (persisted `.active-model.txt`) selects; staging/dirs unchanged; Admin LTX card still reads the LTX Desktop server specifically. Model registry `VideoModelsOptions` is **code-defined** (Backend keys couple to impls; a `VideoModels:Models` appsettings array would duplicate via .NET list-append — caught + fixed in testing). New `ComfyUI` + `VideoModels:Default` appsettings sections. `AdminController` got `Models`/`SetModel`; `/Admin` has a radio-card switch (btn-primary style). Build clean (0 warnings), app starts (DI validated), endpoints verified on an isolated dev port — no regression to the BF16 path. **Limitation:** the NVFP4 backend is **text-to-video only** (ignores image/audio; Extend degrades to t2v) — BF16 default keeps full features. **TODO: re-publish** `C:\ClaudeCore\KeithVision` for the live logon-start app to pick this up.
- **2026-06-19** — **FP4-19B validated end-to-end** ✅: the official `ltx-2-19b-dev-fp4` loads as genuine FP4 (16.5 GB VRAM, no #11864 dequant/OOM) with the correct text-connector. Cracked the VAE-decode failure: `UNETLoader` for the FP4 transformer + `CheckpointLoaderSimple` used solely for its VAE output (the generic `VAELoader` produced a VAE without `downscale_index_formula`). Clean 1280×704/97f clip, warm 66 s (25 steps+CFG); per-pass ~1.3 s vs FP8/bf16 ~4.5 s → **FP4 compute ~3× faster per step**. So the switch now has two working models (BF16-2.3 + FP4-19B). Next: distilled-FP4 for ~6× fewer passes (~11 s est.). See section 7.
- **2026-06-18** — **Plan trimmed to two models — FP8-2.3 dropped**: decided the /Admin switch will host **BF16-2.3 (default) + FP4-19B (speed)** only. FP8-2.3 dropped: it runs bf16 compute (VRAM savings, no speedup) and making it fast needs `q8_kernels` — a from-source CUDA build targeting cu128 (stack is cu130), needing CUDA Toolkit + MSVC, with no Blackwell/sm_120 guarantee (the compile wall). Not worth it for a model that's slower-or-equal to BF16 and lower quality. Its ~40 GB of files (`checkpoints\ltx-2.3-22b-distilled-fp8.safetensors`, `text_encoders\gemma_3_12B_it_fp8_scaled.safetensors`) are now unused. Consequence: the switch's second model is FP4-19B, so **unblocking FP4 (the #11864 connector issue) is the gate to the switch having anything to switch to**.
- **2026-06-18** — **Per-GPU targeting for video vs audio**: on a multi-GPU box the LTX **video** server and the Stable Audio **audio** server can now be pinned to separate cards so both models stay resident at once. Each launcher exports `CUDA_VISIBLE_DEVICES` from a new `-Gpu` parameter — `run-ltx-server.ps1` / `restart-ltx-server.ps1` default to `0`, `run-audio-server.ps1` defaults to `1`, and `start-keithvision.ps1` pins the logon-started LTX server to `0`. New config `Ltx:GpuIndex` (default `0`) and `LocalAudio:GpuIndex` (default `1`); the C# control services (`LtxServerControl`, `AudioServerControl`) read them and pass `-Gpu` through on restart/start. Swap the indices to flip the mapping, or set both to `0` on a single-GPU machine.
- **2026-06-20** — **Logon LTX launch hardened against the console-close kill**: the LTX server kept exiting cleanly a few minutes after a successful start (uvicorn "Server running", then gone, nothing in stderr). Root cause: `start-keithvision.ps1` launched it with `Start-Process`, which leaves the python child attached to the launcher's hidden console — when that console goes away the child receives `CTRL_CLOSE_EVENT` and uvicorn shuts itself down gracefully (the console-less .NET web app is immune, which is why only LTX died). Replaced that with a `Start-Detached` helper that creates the process via **WMI `Win32_Process.Create`** (parented by the WMI service, its own console — no tie to the launcher's process tree or job), applying env vars + stdout/stderr redirection through a `cmd /c` wrapper that lives for the child's lifetime. Wrapped in a retry loop that waits up to ~60 s for port 8765 to bind and re-checks after a grace period, so a failed/early-exiting start self-heals. Verified the detached server survives the launcher exiting (same PID persists). Lives in the source `tools\` dir the logon task + publish script invoke directly, so it's live at next logon with no app rebuild.
- **2026-06-20** — **Topology-aware model switch (1-or-2 GPU) — backend lifecycle on switch**: the `/Admin` model switch now owns the backend *processes*, not just a pointer. New `VideoBackendCoordinator`: selecting a model stops every **co-resident** backend (one sharing the target's `GpuIndex`) to free VRAM, then starts the selected backend **detached**; the page polls `/Admin/Status` until its port binds. Behavior is derived purely from each server's `GpuIndex` — **no 1-vs-2-GPU flag**: same-GPU ⇒ mutually exclusive (today's single 5090, both video backends on it), different-GPU ⇒ coexist (future 4090 hosts audio concurrently). New `tools/run-comfyui.ps1` + `tools/stop-comfyui.ps1` (ComfyUI on 8188, GPU-pinned) and `tools/stop-ltx-server.ps1` (stop without relaunch); new `ComfyUiServerControl` + `StopAsync`/`GpuIndex` on `LtxServerControl`; `ComfyUI:GpuIndex`/`StartScriptPath`/`StopScriptPath` config. `VideoBackendReconciler` (hosted service) re-applies the active model's backend on startup so the choice survives reboots (logon always starts BF16/LTX; if NVFP4 is persisted, it swaps). All GPU launchers now export `CUDA_DEVICE_ORDER=PCI_BUS_ID` for stable indices across the mixed 5090/4090. **NVFP4/ComfyUI is Blackwell-only — pinned to the 5090 (the Ada 4090 has no native FP4).** Verified end-to-end on a dev instance: round-trip BF16⇄NVFP4 switch correctly stops the co-resident backend and brings the other up (`stopped:["LtxDesktop"]` → ComfyUI up / `stopped:["ComfyUI"]` → LTX up), each in ~6 s; `run-comfyui.ps1` binds 8188 in ~21 s; build clean (0 warnings). **Two-GPU rollout (~within a week):** install the 4090, confirm `nvidia-smi` indices, set `LocalAudio:GpuIndex` to the 4090 (keep video on the 5090) — config only, no code change.
