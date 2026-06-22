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
    private readonly ILogger<StudioController> _logger;

    public StudioController(GraphExecutor executor, LtxVideoService ltx, SystemStatsService stats, ILogger<StudioController> logger)
    {
        _executor = executor;
        _ltx = ltx;
        _stats = stats;
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

        async Task Emit(object ev)
        {
            await Response.WriteAsync(JsonSerializer.Serialize(ev, JsonOpts) + "\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        await _executor.RunAsync(graph, Emit, ct);
    }

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
