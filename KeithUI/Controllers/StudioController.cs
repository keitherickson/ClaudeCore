using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using KeithUI.Services;
using KeithVision.Services;
using Microsoft.AspNetCore.Mvc;

namespace KeithUI.Controllers;

/// <summary>
/// The v2 node-graph "studio" UI. The canvas (LiteGraph.js) composes the existing
/// KeithVision operations as nodes; <see cref="Run"/> executes the wired graph via
/// the reused KeithVision.Core services.
/// </summary>
public class StudioController : Controller
{
    private readonly GraphExecutor _executor;
    private readonly LtxVideoService _ltx;   // for the output-dir guard on Preview
    private readonly SystemStatsService _stats;   // footer GPU/CPU/RAM readout
    private readonly LayoutStore _layouts;   // named, saved graphs for the dropdown
    private readonly RunRegistry _runs;      // in-flight runs (cancellable from the admin page)
    private readonly ILogger<StudioController> _logger;

    public StudioController(GraphExecutor executor, LtxVideoService ltx, SystemStatsService stats, LayoutStore layouts, RunRegistry runs, ILogger<StudioController> logger)
    {
        _executor = executor;
        _ltx = ltx;
        _stats = stats;
        _layouts = layouts;
        _runs = runs;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index() => View();

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Executes the serialized graph and STREAMS per-node events back as newline-
    /// delimited JSON (node-start / node-progress / node-done / node-error / log / done),
    /// so the canvas can light up as each node runs.
    /// </summary>
    [HttpPost]
    public async Task Run([FromBody] JsonElement graph, CancellationToken ct)
    {
        Response.ContentType = "application/x-ndjson";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        // Register the run so the admin page can cancel it; drive the executor with the
        // linked token. The run id is emitted first so the client knows what to cancel.
        var (runId, token) = _runs.Register(ct);

        async Task Emit(object ev)
        {
            // Stream on the request token, not the (cancellable) run token, so a cancel
            // still lets the final "done"/"node-error" event flush to the client.
            await Response.WriteAsync(JsonSerializer.Serialize(ev, JsonOpts) + "\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        try
        {
            await Emit(new { type = "run", id = runId });
            await _executor.RunAsync(graph, Emit, token, runId);
        }
        finally
        {
            _runs.Unregister(runId);
        }
    }

    /// <summary>
    /// Releases a run that is paused between loop iterations (the "Trim &amp; Continue" node with
    /// pause-each-step on). Supplies the prompt for the next iteration, or stops the loop early so
    /// it stitches what's already produced. Returns ok=false if the run isn't currently paused.
    /// </summary>
    [HttpPost]
    public IActionResult Continue([FromBody] ContinueRunRequest? req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Id)) return BadRequest(new { ok = false, error = "missing run id" });
        var released = _runs.Continue(req.Id, new RunRegistry.ContinueSignal(req.Prompt, req.Stop));
        return Json(new { ok = released });
    }

    public sealed record ContinueRunRequest(string Id, string? Prompt, bool Stop);

    /// <summary>Streams a produced clip for the Save/Preview node (guarded to the output tree).</summary>
    [HttpGet]
    public IActionResult Preview(string path)
    {
        var root = Path.GetFullPath(_ltx.OutputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full))
            return NotFound();
        return PhysicalFile(full, "video/mp4", enableRangeProcessing: true);
    }

    /// <summary>Stages an uploaded image (for the Load Image node) and returns its path.</summary>
    [HttpPost]
    [RequestSizeLimit(104_857_600)] // 100 MB
    public async Task<IActionResult> Upload(IFormFile? image, CancellationToken ct)
    {
        if (image is not { Length: > 0 })
            return BadRequest(new { ok = false, error = "No image." });
        var path = await _ltx.StageImageAsync(image, ct);
        return Json(new { ok = true, path, name = Path.GetFileName(path) });
    }

    /// <summary>Stages an uploaded audio clip (for the Load Sound node) and returns its path.</summary>
    [HttpPost]
    [RequestSizeLimit(104_857_600)] // 100 MB
    public async Task<IActionResult> UploadAudio(IFormFile? audio, CancellationToken ct)
    {
        if (audio is not { Length: > 0 })
            return BadRequest(new { ok = false, error = "No audio." });
        var path = await _ltx.StageAudioAsync(audio, ct);
        return Json(new { ok = true, path, name = Path.GetFileName(path) });
    }

    /// <summary>Stages an uploaded video clip (for the Load Video node) and returns its path.</summary>
    [HttpPost]
    [RequestSizeLimit(2_147_483_648)]                              // 2 GB — source clips can be large
    [RequestFormLimits(MultipartBodyLengthLimit = 2_147_483_648)]  // raise the default 128 MB multipart cap too
    public async Task<IActionResult> UploadVideo(IFormFile? video, CancellationToken ct)
    {
        if (video is not { Length: > 0 })
            return BadRequest(new { ok = false, error = "No video." });
        var path = await _ltx.StageVideoAsync(video, ct);
        return Json(new { ok = true, path, name = Path.GetFileName(path) });
    }

    /// <summary>Serves a staged video for the Load Video node's poster-frame thumbnail (guarded to the input tree).</summary>
    [HttpGet]
    public IActionResult InputVideo(string path)
    {
        var root = Path.GetFullPath(_ltx.InputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full))
            return NotFound();
        var ext = Path.GetExtension(full).ToLowerInvariant();
        var mime = ext == ".webm" ? "video/webm" : ext == ".mov" ? "video/quicktime" : "video/mp4";
        return PhysicalFile(full, mime, enableRangeProcessing: true);
    }

    /// <summary>Serves a staged image for the node thumbnail (guarded to the input tree).</summary>
    [HttpGet]
    public IActionResult Image(string path)
    {
        var root = Path.GetFullPath(_ltx.InputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full))
            return NotFound();
        var ext = Path.GetExtension(full).ToLowerInvariant();
        var mime = ext == ".png" ? "image/png" : ext is ".jpg" or ".jpeg" ? "image/jpeg" : ext == ".webp" ? "image/webp" : "application/octet-stream";
        return PhysicalFile(full, mime);
    }

    /// <summary>Lists the saved layouts (name + save time) for the toolbar dropdown.</summary>
    [HttpGet]
    public IActionResult Layouts()
        => Json(_layouts.List().Select(l => new { name = l.Name, savedUtc = l.SavedUtc }));

    /// <summary>Saves (or overwrites) the posted graph under a name.</summary>
    [HttpPost]
    public async Task<IActionResult> SaveLayout([FromBody] SaveLayoutRequest? req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { ok = false, error = "A layout name is required." });
        if (req.Graph.ValueKind != JsonValueKind.Object)
            return BadRequest(new { ok = false, error = "No graph to save." });
        try
        {
            await _layouts.SaveAsync(req.Name, req.Graph, ct);
            return Json(new { ok = true, name = req.Name.Trim() });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Returns the saved graph for a layout name (for loading into the canvas).</summary>
    [HttpGet]
    public async Task<IActionResult> Layout(string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { ok = false, error = "A layout name is required." });
        var graph = await _layouts.LoadAsync(name, ct);
        if (graph is null)
            return NotFound(new { ok = false, error = $"Layout '{name}' not found." });
        return Json(new { ok = true, graph = graph.Value });
    }

    /// <summary>Deletes a named layout.</summary>
    [HttpPost]
    public IActionResult DeleteLayout([FromBody] DeleteLayoutRequest? req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { ok = false, error = "A layout name is required." });
        return Json(new { ok = _layouts.Delete(req.Name) });
    }

    public sealed record SaveLayoutRequest(string Name, JsonElement Graph);
    public sealed record DeleteLayoutRequest(string Name);

    /// <summary>Live GPU + CPU + RAM snapshot for the footer (mirrors KeithVision's /Admin/SystemStats).</summary>
    [HttpGet]
    public async Task<IActionResult> SystemStats(CancellationToken ct)
        => Json(new { gpu = await GetGpuAsync(ct), cpu = _stats.Cpu, memory = _stats.Memory });

    /// <summary>Live GPU stats from nvidia-smi (null fields if unavailable).</summary>
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
            foreach (var a in new[]
            {
                "--query-gpu=name,memory.used,memory.total,utilization.gpu,temperature.gpu",
                "--format=csv,noheader,nounits",
            }) psi.ArgumentList.Add(a);

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

    [HttpGet]
    public IActionResult Error() => Content("KeithUI error.");
}
