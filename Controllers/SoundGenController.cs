using KeithVision.Services;
using Microsoft.AspNetCore.Mvc;

namespace KeithVision.Controllers;

/// <summary>
/// Generates a sound effect from a text prompt via the self-hosted Stable Audio
/// server and stages it into the LTX input dir, where the Generate page uses it as
/// the audio track for audio-to-video. Localhost-only app, same trust model as the
/// rest of the site.
/// </summary>
public class SoundGenController : Controller
{
    private readonly SoundGenService _service;
    private readonly ILogger<SoundGenController> _logger;

    public SoundGenController(SoundGenService service, ILogger<SoundGenController> logger)
    {
        _service = service;
        _logger = logger;
    }

    /// <summary>Whether the local audio server is up and the model is loaded. Polled by the page on load.</summary>
    [HttpGet]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        var (ok, error) = await _service.GetHealthAsync(ct);
        return Json(new { ok, error });
    }

    [HttpPost]
    public async Task<IActionResult> Generate([FromForm] string prompt, [FromForm] double duration, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return BadRequest(new { ok = false, error = "Describe the sound you want." });
        try
        {
            var staged = await _service.GenerateAsync(prompt, duration, ct);
            return Json(new { ok = true, fileName = staged.FileName, path = staged.Path, name = staged.Name });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sound generation failed for '{Prompt}'", prompt);
            return StatusCode(502, new { ok = false, error = ex.Message });
        }
    }

    /// <summary>Streams a generated clip from the staging dir so the page can preview it.</summary>
    [HttpGet]
    public IActionResult Preview(string name)
    {
        var path = _service.GetStagedFilePath(name);
        if (!System.IO.File.Exists(path))
            return NotFound();
        return PhysicalFile(path, "audio/wav", enableRangeProcessing: true);
    }
}
