using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace KeithUI.Controllers;

/// <summary>
/// The v2 node-graph "studio" UI. The canvas (LiteGraph.js) composes the existing
/// KeithVision operations as nodes; <see cref="Run"/> will execute the wired graph
/// by calling the reused KeithVision.Core services.
/// </summary>
public class StudioController : Controller
{
    private readonly ILogger<StudioController> _logger;

    public StudioController(ILogger<StudioController> logger) => _logger = logger;

    [HttpGet]
    public IActionResult Index() => View();

    /// <summary>
    /// Receives the serialized LiteGraph graph. v1: validates + summarizes it. The
    /// real executor (topological walk → call Core services → hand the output file
    /// along each edge → stream progress) is the next step.
    /// </summary>
    [HttpPost]
    public IActionResult Run([FromBody] JsonElement graph)
    {
        var nodeCount = graph.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array
            ? nodes.GetArrayLength() : 0;
        _logger.LogInformation("Studio graph received: {Count} nodes", nodeCount);
        return Json(new { ok = true, nodeCount, message = $"Graph received ({nodeCount} nodes). Executor wiring is next." });
    }

    [HttpGet]
    public IActionResult Error() => Content("KeithUI error.");
}
