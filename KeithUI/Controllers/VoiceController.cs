using KeithVision.Services;
using Microsoft.AspNetCore.Mvc;

namespace KeithUI.Controllers;

/// <summary>
/// The "Voice" page: record from the mic (or upload a clip), convert it to a target
/// voice with the local RVC server, then play back, download, or send the result into
/// the studio's input staging dir as a Load Sound source.
/// </summary>
public class VoiceController : Controller
{
    private readonly VoiceStagingService _staging;   // stages clips + guards the input/output trees
    private readonly RvcVoiceService _rvc;    // AI voice conversion (rvc-python server)
    private readonly LtxVideoService _ltx;   // for staging the result into the studio input dir
    private readonly ILogger<VoiceController> _logger;

    public VoiceController(VoiceStagingService staging, RvcVoiceService rvc, LtxVideoService ltx, ILogger<VoiceController> logger)
    {
        _staging = staging;
        _rvc = rvc;
        _ltx = ltx;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index() => View();

    /// <summary>Stages a recorded/uploaded clip and returns its path + name.</summary>
    [HttpPost]
    [RequestSizeLimit(104_857_600)] // 100 MB
    public async Task<IActionResult> Upload(IFormFile? audio, CancellationToken ct)
    {
        if (audio is not { Length: > 0 })
            return BadRequest(new { ok = false, error = "No audio." });
        var path = await _staging.StageInputAsync(audio, ct);
        return Json(new { ok = true, path, name = Path.GetFileName(path) });
    }

    /// <summary>Lists the available RVC target voices (starts the server on demand).</summary>
    [HttpGet]
    public async Task<IActionResult> Voices(CancellationToken ct)
    {
        try
        {
            var voices = await _rvc.ListVoicesAsync(ct);
            return Json(new { ok = true, voices });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Listing RVC voices failed");
            return Json(new { ok = false, error = ex.Message, voices = Array.Empty<string>() });
        }
    }

    /// <summary>Converts a staged clip to an RVC target voice; returns the produced file + playback URL.</summary>
    [HttpPost]
    public async Task<IActionResult> Convert([FromBody] ConvertRequest? req, CancellationToken ct)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Path))
            return BadRequest(new { ok = false, error = "A staged clip is required." });
        if (!_staging.IsInputFile(req.Path))
            return BadRequest(new { ok = false, error = "The clip is not a staged input file." });

        try
        {
            var result = await _rvc.ConvertAsync(req.Path, req.Voice, req.Transpose, ct);
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
            _logger.LogWarning(ex, "RVC convert to '{Voice}' failed", req.Voice);
            return BadRequest(new { ok = false, error = ex.Message });
        }
    }

    public sealed record ConvertRequest(string Path, string? Voice, int Transpose);

    /// <summary>Serves a produced clip for playback/download (guarded to the output tree).</summary>
    [HttpGet]
    public IActionResult Audio(string path)
    {
        if (!_staging.IsOutputFile(path))
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
        if (!_staging.IsOutputFile(req.Path))
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
