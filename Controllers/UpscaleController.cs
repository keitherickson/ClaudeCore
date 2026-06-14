using ClaudeCore.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCore.Controllers;

public class UpscaleController : Controller
{
    private static readonly string[] AllowedEffects = { "SuperRes", "Upscale", "ArtifactReduction" };

    private readonly MaxineUpscaleService _service;
    private readonly VideoSpeedService _speed;
    private readonly ILogger<UpscaleController> _logger;

    public UpscaleController(MaxineUpscaleService service, VideoSpeedService speed, ILogger<UpscaleController> logger)
    {
        _service = service;
        _speed = speed;
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
        [FromForm] int factor,
        [FromForm] int mode,
        [FromForm] float strength,
        [FromForm] bool speedUp,
        [FromForm] double speedFactor,
        IFormFile? video,
        CancellationToken ct)
    {
        if (video is not { Length: > 0 })
            return BadRequest(new { ok = false, error = "Please choose a video to upscale." });
        if (!AllowedEffects.Contains(effect))
            effect = "SuperRes";
        if (factor is not (2 or 3 or 4))
            factor = 2;

        string? h264Temp = null;
        try
        {
            var staged = await _service.StageInputAsync(video, ct);

            // Maxine's VideoEffectsApp only accepts H.264 input; transcode HEVC/etc. first.
            var input = await _speed.EnsureH264Async(staged, ct);
            // A different path means a temp transcode in %TEMP% we own and must delete.
            if (!string.Equals(input, staged, StringComparison.OrdinalIgnoreCase)) h264Temp = input;

            // SuperRes/Upscale require an output height that is an exact integer
            // multiple of the source, so compute it from the probed source height
            // (an absolute height the user picked could be a non-integer multiple
            // and the SDK rejects it: "resolution not supported"). ArtifactReduction
            // doesn't upscale, so it keeps the source height.
            int resolution;
            if (effect == "ArtifactReduction")
            {
                resolution = VideoProbe.TryGetHeight(input) ?? 1080;
            }
            else
            {
                var srcHeight = VideoProbe.TryGetHeight(input)
                    ?? throw new InvalidOperationException("Could not read the source video's height to compute the output resolution.");
                resolution = srcHeight * factor;
            }

            var result = await _service.UpscaleAsync(input, effect, resolution, mode, strength, ct);

            // Optionally re-time the upscaled clip to play faster.
            var fileName = result.FileName;
            var savedPath = result.SavedPath;
            double? appliedSpeed = null;
            if (speedUp && speedFactor > 1.0)
            {
                var sped = await _speed.RetimeAsync(result.SavedPath, speedFactor, ct);
                fileName = sped.FileName;
                savedPath = sped.SavedPath;
                appliedSpeed = sped.Speed;
            }

            return Json(new { ok = true, fileName, savedPath, effect, factor, height = resolution, speed = appliedSpeed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Maxine upscale failed");
            return StatusCode(500, new { ok = false, error = ex.Message });
        }
        finally
        {
            if (h264Temp != null)
                try { System.IO.File.Delete(h264Temp); } catch { /* best effort */ }
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
