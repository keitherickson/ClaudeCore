using ClaudeCore.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCore.Controllers;

/// <summary>
/// Standalone "speed up a video" page: upload a clip, pick a speed, get a
/// re-timed copy back (audio kept + re-timed when present). Wraps VideoSpeedService.
/// </summary>
public class SpeedController : Controller
{
    private readonly VideoSpeedService _speed;
    private readonly ILogger<SpeedController> _logger;

    public SpeedController(VideoSpeedService speed, ILogger<SpeedController> logger)
    {
        _speed = speed;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet]
    public IActionResult Health()
    {
        var ready = _speed.IsReady(out var problem);
        return Json(new { ok = ready, error = problem });
    }

    [HttpPost]
    [RequestSizeLimit(2_147_483_648)] // 2 GB cap for uploaded source videos
    [RequestFormLimits(MultipartBodyLengthLimit = 2_147_483_648)]
    public async Task<IActionResult> Run(
        [FromForm] double speedFactor,
        IFormFile? video,
        CancellationToken ct)
    {
        if (video is not { Length: > 0 })
            return BadRequest(new { ok = false, error = "Please choose a video to speed up." });
        if (speedFactor <= 1.0)
            return BadRequest(new { ok = false, error = "Choose a speed greater than 1×." });

        try
        {
            if (!_speed.IsReady(out var problem))
                throw new InvalidOperationException(problem ?? "ffmpeg is not available.");

            var input = await _speed.StageInputAsync(video, ct);
            var result = await _speed.RetimeUploadAsync(input, speedFactor, ct);
            return Json(new { ok = true, fileName = result.FileName, savedPath = result.SavedPath, speed = result.Speed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Speed-up failed");
            return StatusCode(500, new { ok = false, error = ex.Message });
        }
    }

    [HttpGet]
    public IActionResult Download(string name)
    {
        var path = _speed.GetOutputFilePath(name);
        if (!System.IO.File.Exists(path))
            return NotFound();
        return PhysicalFile(path, "video/mp4", enableRangeProcessing: true);
    }
}
