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

    /// <summary>Executes the serialized LiteGraph graph and returns the final clip + a step log.</summary>
    [HttpPost]
    public async Task<IActionResult> Run([FromBody] JsonElement graph, CancellationToken ct)
    {
        var r = await _executor.RunAsync(graph, ct);
        var url = r.FinalVideo is not null ? Url.Action("Preview", new { path = r.FinalVideo }) : null;
        return Json(new
        {
            ok = r.Ok,
            videoUrl = url,
            fileName = r.FinalVideo is null ? null : Path.GetFileName(r.FinalVideo),
            log = r.Log,
            error = r.Error,
        });
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

    [HttpGet]
    public IActionResult Error() => Content("KeithUI error.");
}
