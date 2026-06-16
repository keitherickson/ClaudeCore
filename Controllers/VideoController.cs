using ClaudeCore.Models.Ltx;
using ClaudeCore.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCore.Controllers;

public class VideoController : Controller
{
    private readonly LtxVideoService _service;
    private readonly MaxineUpscaleService _upscaler;
    private readonly VideoSpeedService _speed;
    private readonly LtxServerControl _ltxControl;
    private readonly LastImageStore _lastImage;
    private readonly ILogger<VideoController> _logger;

    public VideoController(LtxVideoService service, MaxineUpscaleService upscaler, VideoSpeedService speed, LtxServerControl ltxControl, LastImageStore lastImage, ILogger<VideoController> logger)
    {
        _service = service;
        _upscaler = upscaler;
        _speed = speed;
        _ltxControl = ltxControl;
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

    /// <summary>
    /// Hard-stops the current generation. The LTX server's cooperative cancel can't
    /// interrupt an in-progress video inference (no per-step hook), so to actually
    /// free the GPU we kill and relaunch the server process.
    /// </summary>
    [HttpPost]
    public IActionResult Cancel()
    {
        try
        {
            // Detached: frees the GPU now and relaunches the server, without this
            // request blocking on (or tree-killing) the relaunched process.
            _ltxControl.RestartDetached();
            return Json(new { ok = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cancel (server restart) failed");
            return Json(new { ok = false, error = ex.Message });
        }
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
    [RequestSizeLimit(314_572_800)] // 300 MB cap (conditioning image up to 100 MB + audio up to 100 MB)
    [RequestFormLimits(MultipartBodyLengthLimit = 314_572_800)]
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
        [FromForm] string? audioStagedPath,
        IFormFile? image,
        IFormFile? audioFile,
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
                if (_service.IsStagedInputFile(last)) imagePath = last;
            }

            // Optional own-audio track: switches the server to audio-to-video.
            // An uploaded file takes precedence; otherwise use a clip already staged
            // by the AI sound generator (validated to be inside the staging dir).
            string? audioPath = null;
            if (audioFile is { Length: > 0 })
                audioPath = await _service.StageAudioAsync(audioFile, ct);
            else if (_service.IsStagedInputFile(audioStagedPath))
                audioPath = audioStagedPath;

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
                AudioPath = audioPath,
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
                    // The saved file is already H.264 (converted in place above), so this is
                    // normally a no-op; if it does transcode, delete the %TEMP% copy after use.
                    var upscaleSource = await _speed.EnsureH264Async(result.SavedPath, ct);
                    var upscaleTemp = string.Equals(upscaleSource, result.SavedPath, StringComparison.OrdinalIgnoreCase)
                        ? null : upscaleSource;

                    var srcHeight = VideoProbe.TryGetHeight(upscaleSource)
                        ?? throw new InvalidOperationException("Could not read the generated video's height.");

                    var factor = upscaleFactor is 2 or 3 or 4 ? upscaleFactor : 2;
                    var target = srcHeight * factor; // exact 2x/3x/4x => valid SuperRes engine

                    var up = await _upscaler.UpscaleAsync(upscaleSource, "SuperRes", target, upscaleMode, 0f, ct);
                    if (upscaleTemp != null)
                        try { System.IO.File.Delete(upscaleTemp); } catch { /* best effort */ }

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
                mode = audioPath is not null ? "audio-to-video" : imagePath is null ? "text-to-video" : "image-to-video",
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
