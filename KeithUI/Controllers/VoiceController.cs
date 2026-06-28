using KeithVision.Services;
using Microsoft.AspNetCore.Mvc;

namespace KeithUI.Controllers;

/// <summary>
/// The "Voice Changer / Recorder" page: record from the mic (or upload a clip),
/// apply a local ffmpeg voice effect, then play back, download, or send the
/// result into the studio's input staging dir as a Load Sound source.
/// </summary>
public class VoiceController : Controller
{
    private readonly VoiceChangerService _voice;
    private readonly LtxVideoService _ltx;   // for staging the result into the studio input dir
    private readonly ILogger<VoiceController> _logger;

    public VoiceController(VoiceChangerService voice, LtxVideoService ltx, ILogger<VoiceController> logger)
    {
        _voice = voice;
        _ltx = ltx;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index() => View();

    /// <summary>The effect presets for the page's buttons.</summary>
    [HttpGet]
    public IActionResult Presets() => Json(_voice.Presets);

    /// <summary>Stages a recorded/uploaded clip and returns its path + name.</summary>
    [HttpPost]
    [RequestSizeLimit(104_857_600)] // 100 MB
    public async Task<IActionResult> Upload(IFormFile? audio, CancellationToken ct)
    {
        if (audio is not { Length: > 0 })
            return BadRequest(new { ok = false, error = "No audio." });
        var path = await _voice.StageInputAsync(audio, ct);
        return Json(new { ok = true, path, name = Path.GetFileName(path) });
    }

    /// <summary>Applies an effect to a staged clip; returns the produced file name + playback URL.</summary>
    [HttpPost]
    public async Task<IActionResult> Process([FromBody] ProcessRequest? req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Path) || string.IsNullOrWhiteSpace(req.Effect))
            return BadRequest(new { ok = false, error = "A staged clip and an effect are required." });
        if (!_voice.IsInputFile(req.Path))
            return BadRequest(new { ok = false, error = "The clip is not a staged input file." });

        try
        {
            var result = await _voice.ProcessAsync(req.Path, req.Effect, req.Pitch, ct);
            return Json(new
            {
                ok = true,
                path = result.SavedPath,
                name = result.FileName,
                url = Url.Action(nameof(Audio), new { path = result.SavedPath }),
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Voice effect '{Effect}' failed", req.Effect);
            return BadRequest(new { ok = false, error = ex.Message });
        }
    }

    public sealed record ProcessRequest(string Path, string Effect, double Pitch);

    /// <summary>Serves a produced clip for playback/download (guarded to the output tree).</summary>
    [HttpGet]
    public IActionResult Audio(string path)
    {
        if (!_voice.IsOutputFile(path))
            return NotFound();
        var full = Path.GetFullPath(path);
        return PhysicalFile(full, "audio/wav", Path.GetFileName(full), enableRangeProcessing: true);
    }

    /// <summary>
    /// Copies a produced clip into the studio's input staging dir so it shows up as a
    /// Load Sound source in the node graph. Returns the staged path + name.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> SendToStudio([FromBody] SendRequest? req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { ok = false, error = "A produced clip is required." });
        if (!_voice.IsOutputFile(req.Path))
            return BadRequest(new { ok = false, error = "The clip is not a produced output file." });

        Directory.CreateDirectory(_ltx.InputDirectory);
        var dest = Path.Combine(_ltx.InputDirectory, $"voice_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.wav");
        await using (var src = System.IO.File.OpenRead(req.Path))
        await using (var dst = System.IO.File.Create(dest))
            await src.CopyToAsync(dst, ct);

        _logger.LogInformation("Sent voice clip to studio input: {Dest}", dest);
        return Json(new { ok = true, path = dest, name = Path.GetFileName(dest) });
    }

    public sealed record SendRequest(string Path);
}
