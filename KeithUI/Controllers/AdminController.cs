using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using KeithUI.Services;
using KeithVision.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace KeithUI.Controllers;

/// <summary>
/// KeithUI Studio's own administration dashboard. This is deliberately separate
/// from the v1 KeithVision /Admin page: it lives entirely in the KeithUI project,
/// has its own controller/view/JS, and only reaches the shared Core service layer
/// for backend &amp; model control, diagnostics, storage housekeeping, and studio
/// layout management. Localhost-only trust model (same as the rest of KeithUI),
/// so no separate auth — and POSTs use plain JSON like the studio's other calls.
/// </summary>
public class AdminController : Controller
{
    private readonly LtxVideoService _ltx;
    private readonly LtxServerControl _ltxControl;
    private readonly ComfyUiServerControl _comfyControl;
    private readonly AudioServerControl _audioControl;
    private readonly SoundGenService _soundGen;
    private readonly VideoBackendCoordinator _backends;
    private readonly ActiveModelStore _activeModel;
    private readonly VideoModelsOptions _videoModels;
    private readonly MaxineUpscaleService _maxine;
    private readonly VideoSpeedService _speed;
    private readonly SystemStatsService _stats;
    private readonly LayoutStore _layouts;
    private readonly RunRegistry _runs;
    private readonly PromptServerControl _promptControl;
    private readonly PromptEnhanceService _promptSvc;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        LtxVideoService ltx, LtxServerControl ltxControl, ComfyUiServerControl comfyControl,
        AudioServerControl audioControl, SoundGenService soundGen, VideoBackendCoordinator backends,
        ActiveModelStore activeModel, IOptions<VideoModelsOptions> videoModels, MaxineUpscaleService maxine,
        VideoSpeedService speed, SystemStatsService stats, LayoutStore layouts, RunRegistry runs,
        PromptServerControl promptControl, PromptEnhanceService promptSvc, ILogger<AdminController> logger)
    {
        _ltx = ltx; _ltxControl = ltxControl; _comfyControl = comfyControl;
        _audioControl = audioControl; _soundGen = soundGen; _backends = backends;
        _activeModel = activeModel; _videoModels = videoModels.Value; _maxine = maxine;
        _speed = speed; _stats = stats; _layouts = layouts; _runs = runs;
        _promptControl = promptControl; _promptSvc = promptSvc; _logger = logger;
    }

    [HttpGet]
    public IActionResult Index() => View();

    /// <summary>One aggregated snapshot the page polls: backends, active model, GPU/CPU/RAM, diagnostics, storage, layouts.</summary>
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        // --- LTX server ---
        var ltxListening = _ltxControl.IsPortListening();
        object ltx;
        try
        {
            var raw = await _ltx.GetHealthRawAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            ltx = new { reachable = true, portListening = ltxListening, port = _ltxControl.Port, health = doc.RootElement.Clone(), error = (string?)null };
        }
        catch (Exception ex)
        {
            ltx = new { reachable = false, portListening = ltxListening, port = _ltxControl.Port, health = (object?)null, error = ex.Message };
        }

        // --- Stable Audio server (on-demand; parks on CPU, takes the GPU only during /generate) ---
        var (audioOk, audioErr) = await _soundGen.GetHealthAsync(ct);
        var audio = new { reachable = audioOk, portListening = _audioControl.IsPortListening(), port = _audioControl.Port, error = audioErr };

        // --- ComfyUI server (NVFP4 / Wan / AI-upscale share it) ---
        var comfy = new { portListening = _comfyControl.IsPortListening(), port = _comfyControl.Port, gpuIndex = _comfyControl.GpuIndex };

        // --- Prompt-enhancer LLM server (on-demand; shares the 4090 with audio) ---
        var (promptOk, promptErr) = await _promptSvc.GetHealthAsync(ct);
        var prompt = new { reachable = promptOk, portListening = _promptControl.IsPortListening(), port = _promptControl.Port, error = promptErr };

        var maxineReady = _maxine.IsReady(out var maxineProblem);
        var ffmpegReady = _speed.IsReady(out var ffmpegProblem);

        var proc = Process.GetCurrentProcess();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var app = new { version, machine = Environment.MachineName, startedUtc = proc.StartTime.ToUniversalTime(), uptimeSeconds = (long)(DateTime.Now - proc.StartTime).TotalSeconds };

        // Live generation progress (best-effort — null/idle when nothing is running).
        object? generation = null;
        try
        {
            var p = await _ltx.GetProgressAsync(ct);
            if (p is not null)
                generation = new { status = p.Status, phase = p.Phase, progress = p.Progress, currentStep = p.CurrentStep, totalSteps = p.TotalSteps };
        }
        catch { /* progress is best-effort */ }

        return Json(new
        {
            ok = true,
            timeUtc = DateTime.UtcNow,
            activeId = _activeModel.ActiveId,
            models = _videoModels.Models.Select(m => new { m.Id, m.Label, m.Backend, m.Description }),
            ltx,
            audio,
            comfy,
            prompt,
            maxine = new { ready = maxineReady, error = maxineProblem },
            ffmpeg = new { ready = ffmpegReady, error = ffmpegProblem, path = _speed.FfmpegPath },
            gpu = await GetGpuAsync(ct),
            gpuProcs = await GetGpuProcsAsync(ct),
            generation,
            runs = _runs.List().Select(r => new { id = r.Id, startedUtc = r.StartedUtc }),
            cpu = _stats.Cpu,
            memory = _stats.Memory,
            app,
            output = DiskInfo(_ltx.OutputDirectory),
            staging = StagingInfo(),
            layouts = _layouts.List().Select(l => new { name = l.Name, savedUtc = l.SavedUtc }),
        });
    }

    /// <summary>Lists recent generated clips in the output dir (newest first) for the gallery.</summary>
    [HttpGet]
    public IActionResult Outputs(int take = 24)
    {
        var dir = _ltx.OutputDirectory;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            return Json(new { ok = true, items = Array.Empty<object>() });

        var items = new DirectoryInfo(dir)
            .EnumerateFiles("*.*", SearchOption.TopDirectoryOnly)
            .Where(f => f.Extension is ".mp4" or ".webm" or ".mov")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(Math.Clamp(take, 1, 200))
            .Select(f => new { name = f.Name, path = f.FullName, sizeMb = Math.Round(f.Length / 1024d / 1024, 1), modifiedUtc = f.LastWriteTimeUtc })
            .ToList();
        return Json(new { ok = true, items });
    }

    public sealed record DeleteOutputRequest(string Path);

    /// <summary>Deletes a single generated clip (path-guarded to the output dir).</summary>
    [HttpPost]
    public IActionResult DeleteOutput([FromBody] DeleteOutputRequest? req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { ok = false, error = "A path is required." });

        var root = System.IO.Path.GetFullPath(_ltx.OutputDirectory).TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(req.Path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full))
            return BadRequest(new { ok = false, error = "Not an output-dir file." });

        try { System.IO.File.Delete(full); return Json(new { ok = true }); }
        catch (Exception ex) { return Json(new { ok = false, error = ex.Message }); }
    }


    public sealed record CancelRunRequest(string Id);

    /// <summary>Cancels a single in-flight graph run by id.</summary>
    [HttpPost]
    public IActionResult CancelRun([FromBody] CancelRunRequest? req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Id))
            return BadRequest(new { ok = false, error = "A run id is required." });
        var found = _runs.Cancel(req.Id);
        if (found) _logger.LogInformation("Studio admin cancelled run {RunId}", req.Id);
        return Json(new { ok = found, error = found ? null : "Run not found (already finished?)." });
    }

    /// <summary>Cancels every in-flight graph run.</summary>
    [HttpPost]
    public IActionResult CancelAllRuns()
    {
        var n = _runs.CancelAll();
        if (n > 0) _logger.LogInformation("Studio admin cancelled {Count} run(s)", n);
        return Json(new { ok = true, cancelled = n });
    }

    public sealed record SetModelRequest(string Id);

    /// <summary>Switches the active video model and brings its backend up (freeing co-resident ones).</summary>
    [HttpPost]
    public async Task<IActionResult> SetModel([FromBody] SetModelRequest? req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Id) || !_activeModel.Set(req.Id))
            return BadRequest(new { ok = false, error = $"Unknown model id '{req?.Id}'." });

        var sw = await _backends.ActivateAsync(req.Id, ct);
        _logger.LogInformation("Studio admin switched active model to {Id} (backend {Backend}, wasUp={WasUp})", req.Id, sw.Backend, sw.WasAlreadyUp);
        return Json(new
        {
            ok = true,
            activeId = _activeModel.ActiveId,
            backend = sw.Backend,
            backendPort = sw.Port,
            backendWasUp = sw.WasAlreadyUp,
            stopped = sw.StoppedCoResident,
            warning = sw.Ok ? null : sw.Error,
        });
    }

    /// <summary>Stops and restarts the local LTX server. Returns the script output.</summary>
    [HttpPost]
    public async Task<IActionResult> RestartLtx(CancellationToken ct)
    {
        var r = await _ltxControl.RestartAsync(ct);
        return Json(new { ok = r.Ok, output = r.Output });
    }

    /// <summary>Starts the local Stable Audio server (detached; the model loads in the background).</summary>
    [HttpPost]
    public IActionResult StartAudio()
    {
        var ok = _audioControl.StartDetached();
        return Json(new { ok, error = ok ? null : "Audio start script not found." });
    }

    /// <summary>Stops the local Stable Audio server (frees its VRAM). Returns the script output.</summary>
    [HttpPost]
    public async Task<IActionResult> StopAudio(CancellationToken ct)
    {
        var r = await _audioControl.StopAsync(ct);
        return Json(new { ok = r.Ok, output = r.Output });
    }

    /// <summary>Starts the local prompt-enhancer server (detached; the model loads in the background).</summary>
    [HttpPost]
    public IActionResult StartPrompt()
    {
        var ok = _promptControl.StartDetached();
        return Json(new { ok, error = ok ? null : "Prompt start script not found." });
    }

    /// <summary>Stops the local prompt-enhancer server (frees its VRAM). Returns the script output.</summary>
    [HttpPost]
    public async Task<IActionResult> StopPrompt(CancellationToken ct)
    {
        var r = await _promptControl.StopAsync(ct);
        return Json(new { ok = r.Ok, output = r.Output });
    }

    /// <summary>Deletes staged/intermediate files (uploads + temp H.264) without touching real outputs.</summary>
    [HttpPost]
    public IActionResult CleanStaging()
    {
        long files = 0, bytes = 0;
        foreach (var dir in StagingDirs())
            CleanDir(dir.Path, dir.Pattern, ref files, ref bytes);
        _logger.LogInformation("Studio admin cleaned {Files} staging files ({Bytes} bytes).", files, bytes);
        return Json(new { ok = true, files, bytes, mb = Math.Round(bytes / 1024d / 1024, 1) });
    }

    // --- helpers (self-contained; mirrors the v1 dashboard's logic but owned here) ---

    /// <summary>Staging/intermediate locations safe to wipe (never the output dir).</summary>
    private IEnumerable<(string Path, string Pattern)> StagingDirs() => new[]
    {
        (_ltx.InputDirectory, "*"),
        (_maxine.InputDirectory, "*"),
        (_speed.InputDirectory, "*"),
        (Path.GetTempPath(), "h264_*.mp4"),
    };

    private static void CleanDir(string path, string pattern, ref long files, ref long bytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        foreach (var f in Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly))
        {
            try { var len = new FileInfo(f).Length; System.IO.File.Delete(f); files++; bytes += len; }
            catch { /* in use / locked — skip */ }
        }
    }

    private object StagingInfo()
    {
        long files = 0, bytes = 0;
        foreach (var dir in StagingDirs())
        {
            if (string.IsNullOrWhiteSpace(dir.Path) || !Directory.Exists(dir.Path)) continue;
            foreach (var f in Directory.EnumerateFiles(dir.Path, dir.Pattern, SearchOption.TopDirectoryOnly))
                try { bytes += new FileInfo(f).Length; files++; } catch { /* ignore */ }
        }
        return new { files, bytes, mb = Math.Round(bytes / 1024d / 1024, 1) };
    }

    private static async Task<object> GetGpuAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "--query-gpu=name,memory.used,memory.total,utilization.gpu,temperature.gpu", "--format=csv,noheader,nounits" })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            string outText;
            try
            {
                outText = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return new { available = false, error = "nvidia-smi timed out" };
            }

            var line = outText.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (line is null) return new { available = false, error = "no GPU reported" };

            var p = line.Split(',').Select(s => s.Trim()).ToArray();
            int? ParseInt(int i) => i < p.Length && int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

            return new
            {
                available = true,
                name = p.Length > 0 ? p[0] : null,
                vramUsedMb = ParseInt(1),
                vramTotalMb = ParseInt(2),
                utilizationPct = ParseInt(3),
                temperatureC = ParseInt(4),
                error = (string?)null,
            };
        }
        catch (Exception ex)
        {
            return new { available = false, error = ex.Message };
        }
    }

    /// <summary>Per-process VRAM from nvidia-smi (so you can see what's holding the GPU). Empty on failure.</summary>
    private static async Task<object> GetGpuProcsAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            foreach (var a in new[] { "--query-compute-apps=pid,process_name,used_memory", "--format=csv,noheader,nounits" })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            string outText;
            try
            {
                outText = await proc.StandardOutput.ReadToEndAsync(cts.Token);
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return Array.Empty<object>();
            }

            var rows = new List<(string Pid, string Name, int? UsedMb)>();
            foreach (var raw in outText.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0) continue;
                var p = line.Split(',').Select(s => s.Trim()).ToArray();
                if (p.Length < 3) continue;
                int? usedMb = int.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
                rows.Add((p[0], System.IO.Path.GetFileName(p[1]), usedMb));
            }
            return rows.OrderByDescending(r => r.UsedMb ?? 0)
                       .Select(r => new { pid = r.Pid, name = r.Name, usedMb = r.UsedMb })
                       .ToList();
        }
        catch
        {
            return Array.Empty<object>();
        }
    }

    private static object DiskInfo(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return new { path, exists = Directory.Exists(path) };
            var d = new DriveInfo(root);
            return new { path, exists = Directory.Exists(path), drive = root, freeGb = Math.Round(d.AvailableFreeSpace / 1024d / 1024 / 1024, 1), totalGb = Math.Round(d.TotalSize / 1024d / 1024 / 1024, 1) };
        }
        catch
        {
            return new { path, exists = Directory.Exists(path) };
        }
    }
}
