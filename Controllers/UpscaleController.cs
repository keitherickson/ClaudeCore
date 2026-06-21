using KeithVision.Services;
using Microsoft.AspNetCore.Mvc;

namespace KeithVision.Controllers;

public class UpscaleController : Controller
{
    private static readonly string[] AllowedEffects = { "SuperRes", "Upscale", "ArtifactReduction" };

    private readonly MaxineUpscaleService _service;
    private readonly ComfyUiUpscaleService _ai;
    private readonly VideoSpeedService _speed;
    private readonly ILogger<UpscaleController> _logger;

    public UpscaleController(MaxineUpscaleService service, ComfyUiUpscaleService ai, VideoSpeedService speed, ILogger<UpscaleController> logger)
    {
        _service = service;
        _ai = ai;
        _speed = speed;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index() => View();

    [HttpGet]
    public IActionResult Health()
    {
        var maxineReady = _service.IsReady(out var maxineProblem);
        var aiReady = _ai.IsReady(out var aiProblem);
        return Json(new
        {
            ok = maxineReady,                  // back-compat: existing page reads ok = Maxine
            error = maxineProblem,
            maxine = new { ready = maxineReady, error = maxineProblem },
            ai = new { ready = aiReady, error = aiProblem },
        });
    }

    /// <summary>
    /// AI upscale via ComfyUI to an arbitrary target height (aspect preserved). The
    /// alternative engine to Maxine — no integer-ratio restriction.
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(2_147_483_648)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2_147_483_648)]
    public async Task<IActionResult> RunAi(
        [FromForm] int targetHeight,
        [FromForm] bool speedUp,
        [FromForm] double speedFactor,
        IFormFile? video,
        CancellationToken ct)
    {
        if (video is not { Length: > 0 })
            return BadRequest(new { ok = false, error = "Please choose a video to upscale." });
        targetHeight = Math.Clamp(targetHeight <= 0 ? 1080 : targetHeight, 240, 4320);

        try
        {
            var name = await _ai.StageInputAsync(video, ct);
            var dims = VideoProbe.TryGetDimensions(Path.Combine(_ai.InputDirectory, name));

            // Preserve aspect: width from source ratio (fallback 16:9 if unreadable). Even dims.
            int targetW = dims is { } d
                ? (int)Math.Round((double)d.Width / d.Height * targetHeight)
                : targetHeight * 16 / 9;
            int Even(int v) => v % 2 == 0 ? v : v + 1;
            targetW = Even(Math.Max(2, targetW));
            int targetH = Even(targetHeight);

            var result = await _ai.UpscaleAsync(name, targetW, targetH, ct);

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

            return Json(new { ok = true, fileName, savedPath, engine = "ai", width = result.Width, height = result.Height, speed = appliedSpeed });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ComfyUI AI upscale failed");
            return StatusCode(500, new { ok = false, error = ex.Message });
        }
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

            // Maxine's writer produces a video-only file; restore the original
            // upload's audio onto it (staged still has audio — input may be the
            // audio-stripped H.264 temp).
            await _speed.RestoreAudioAsync(result.SavedPath, staged, ct);

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
