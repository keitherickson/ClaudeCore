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
    private readonly ILogger<StudioController> _logger;

    public StudioController(GraphExecutor executor, LtxVideoService ltx, ILogger<StudioController> logger)
    {
        _executor = executor;
        _ltx = ltx;
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

    [HttpGet]
    public IActionResult Error() => Content("KeithUI error.");
}
