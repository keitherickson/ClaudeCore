using ClaudeCore.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCore.Controllers;

public class UpscaleController : Controller
{
    private static readonly string[] AllowedEffects = { "SuperRes", "Upscale", "ArtifactReduction" };

    private readonly MaxineUpscaleService _service;
    private readonly ILogger<UpscaleController> _logger;

    public UpscaleController(MaxineUpscaleService service, ILogger<UpscaleController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet]
    public IActionResult Health()
    {
        var ready = _service.IsReady(out var problem);
        return Json(new { ok = ready, error = problem });
    }

    [HttpPost]
    [RequestSizeLimit(2_147_483_648)] // 2 GB cap for uploaded source videos
    [RequestFormLimits(MultipartBodyLengthLimit = 2_147_483_648)]
    public async Task<IActionResult> Run(
        [FromForm] string effect,
        [FromForm] int resolution,
        [FromForm] int mode,
        [FromForm] float strength,
        IFormFile? video,
        CancellationToken ct)
    {
        if (video is not { Length: > 0 })
            return BadRequest(new { ok = false, error = "Please choose a video to upscale." });
        if (!AllowedEffects.Contains(effect))
            effect = "SuperRes";
        if (resolution <= 0)
            resolution = 2160;

        try
        {
            var input = await _service.StageInputAsync(video, ct);
            var result = await _service.UpscaleAsync(input, effect, resolution, mode, strength, ct);
            return Json(new { ok = true, fileName = result.FileName, savedPath = result.SavedPath, effect });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Maxine upscale failed");
            return StatusCode(500, new { ok = false, error = ex.Message });
        }
    }

    [HttpGet]
    public IActionResult Download(string name)
    {
        var path = _service.GetOutputFilePath(name);
        if (!System.IO.File.Exists(path))
            return NotFound();
        return PhysicalFile(path, "video/mp4", enableRangeProcessing: true);
    }
}
