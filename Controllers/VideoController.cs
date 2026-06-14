using ClaudeCore.Models.Ltx;
using ClaudeCore.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCore.Controllers;

public class VideoController : Controller
{
    private readonly LtxVideoService _service;
    private readonly MaxineUpscaleService _upscaler;
    private readonly VideoSpeedService _speed;
    private readonly LastImageStore _lastImage;
    private readonly ILogger<VideoController> _logger;

    public VideoController(LtxVideoService service, MaxineUpscaleService upscaler, VideoSpeedService speed, LastImageStore lastImage, ILogger<VideoController> logger)
    {
        _service = service;
        _upscaler = upscaler;
        _speed = speed;
        _lastImage = lastImage;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        ViewData["LastImage"] = _lastImage.Get(); // remembered starting image, reused automatically
        return View();
    }

    /// <summary>Forget the remembered starting image.</summary>
    [HttpPost]
    public IActionResult ClearImage()
    {
        _lastImage.Set(null);
        return Json(new { ok = true });
    }

    /// <summary>Connectivity + model-status probe for the page header.</summary>
    [HttpGet]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        try
        {
            var health = await _service.GetHealthAsync(ct);
            return Json(new { ok = true, health });
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Proxy for the model capability matrix (resolution/fps/duration limits).</summary>
    [HttpGet]
    public async Task<IActionResult> Specs(CancellationToken ct)
    {
        try
        {
            var json = await _service.GetModelSpecsRawAsync(ct);
            return Content(json, "application/json");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch model specs");
            return Content("{\"local_models\":[],\"api_models\":[]}", "application/json");
        }
    }

    /// <summary>Proxy for the server's live progress, polled by the page during a generation.</summary>
    [HttpGet]
    public async Task<IActionResult> Progress(CancellationToken ct)
    {
        try
        {
            var progress = await _service.GetProgressAsync(ct);
            return Json(progress);
        }
        catch (Exception ex)
        {
            return Json(new { status = "error", phase = ex.Message, progress = 0 });
        }
    }

    [HttpPost]
    [RequestSizeLimit(104_857_600)] // 100 MB cap for uploaded conditioning images
    public async Task<IActionResult> Generate(
        [FromForm] string prompt,
        [FromForm] string? resolution,
        [FromForm] int duration,
        [FromForm] int fps,
        [FromForm] string? aspectRatio,
        [FromForm] string? cameraMotion,
        [FromForm] string? negativePrompt,
        [FromForm] bool audio,
        [FromForm] bool upscale,
        [FromForm] int upscaleFactor,
        [FromForm] int upscaleMode,
        [FromForm] bool speedUp,
        [FromForm] double speedFactor,
        IFormFile? image,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return BadRequest(new { ok = false, error = "Prompt is required." });

        try
        {
            string? imagePath = null;
            if (image is { Length: > 0 })
            {
                imagePath = await _service.StageImageAsync(image, ct);
            }
            else
            {
                // No new upload: automatically reuse the last remembered image.
                var last = _lastImage.Get();
                if (_service.IsStagedInputImage(last)) imagePath = last;
            }

            var request = new GenerateVideoRequest
            {
                Prompt = prompt,
                Resolution = string.IsNullOrWhiteSpace(resolution) ? "1080p" : resolution,
                Duration = duration <= 0 ? 5 : duration,
                Fps = fps <= 0 ? 24 : fps,
                AspectRatio = string.IsNullOrWhiteSpace(aspectRatio) ? "16:9" : aspectRatio,
                CameraMotion = string.IsNullOrWhiteSpace(cameraMotion) ? "none" : cameraMotion,
                NegativePrompt = negativePrompt ?? "",
                Audio = audio,
                ImagePath = imagePath,
            };

            // 1. Generate + save the original.
            var result = await _service.GenerateAsync(request, ct);

            // LTX emits HEVC; convert to H.264 so it plays in all browsers and feeds Maxine.
            try { await _speed.ConvertToH264InPlaceAsync(result.SavedPath, ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "H.264 conversion of generated video failed; serving original."); }

            // Generation completed => remember (or clear) the starting image server-side.
            _lastImage.Set(imagePath);

            // 2. Optionally upscale the saved original and save an upscaled copy.
            object? upscaled = null;
            string? upscaleError = null;
            if (upscale)
            {
                try
                {
                    if (!_upscaler.IsReady(out var problem))
                        throw new InvalidOperationException(problem ?? "Upscaler is not ready.");

                    // Maxine's VideoEffectsApp only accepts H.264 input; transcode HEVC/etc. first.
                    var upscaleSource = await _speed.EnsureH264Async(result.SavedPath, ct);

                    var srcHeight = VideoProbe.TryGetHeight(upscaleSource)
                        ?? throw new InvalidOperationException("Could not read the generated video's height.");

                    var factor = upscaleFactor is 2 or 3 or 4 ? upscaleFactor : 2;
                    var target = srcHeight * factor; // exact 2x/3x/4x => valid SuperRes engine

                    var up = await _upscaler.UpscaleAsync(upscaleSource, "SuperRes", target, upscaleMode, 0f, ct);

                    // Optionally re-time the upscaled clip to play faster.
                    var upFileName = up.FileName;
                    var upSavedPath = up.SavedPath;
                    double? appliedSpeed = null;
                    if (speedUp && speedFactor > 1.0)
                    {
                        var sped = await _speed.RetimeAsync(up.SavedPath, speedFactor, ct);
                        upFileName = sped.FileName;
                        upSavedPath = sped.SavedPath;
                        appliedSpeed = sped.Speed;
                    }

                    upscaled = new { fileName = upFileName, savedPath = upSavedPath, factor, height = target, speed = appliedSpeed };
                }
                catch (Exception ex)
                {
                    upscaleError = ex.Message;
                    _logger.LogError(ex, "Integrated upscale failed");
                }
            }

            return Json(new
            {
                ok = true,
                fileName = result.FileName,
                savedPath = result.SavedPath,
                mode = imagePath is null ? "text-to-video" : "image-to-video",
                inputImagePath = imagePath,
                upscaled,
                upscaleError,
            });
        }
        catch (LtxServerException ex)
        {
            _logger.LogError(ex, "LTX generation failed");
            return StatusCode(502, new { ok = false, error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Generation error");
            return StatusCode(500, new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Streams a finished video from the output folder for inline preview/download.</summary>
    [HttpGet]
    public IActionResult Download(string name)
    {
        var path = _service.GetOutputFilePath(name);
        if (!System.IO.File.Exists(path))
            return NotFound();

        return PhysicalFile(path, "video/mp4", enableRangeProcessing: true);
    }
}
