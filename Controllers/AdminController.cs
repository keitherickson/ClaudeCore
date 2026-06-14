using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ClaudeCore.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCore.Controllers;

/// <summary>
/// Local administration dashboard: health of the LTX server, the Maxine upscaler,
/// and the web app itself, plus a button to restart the LTX server.
/// Localhost-only app (Kestrel binds 127.0.0.1), so no separate auth here — same
/// trust model as the rest of the site.
/// </summary>
public class AdminController : Controller
{
    private readonly LtxVideoService _ltx;
    private readonly LtxServerControl _ltxControl;
    private readonly MaxineUpscaleService _maxine;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        LtxVideoService ltx,
        LtxServerControl ltxControl,
        MaxineUpscaleService maxine,
        ILogger<AdminController> logger)
    {
        _ltx = ltx;
        _ltxControl = ltxControl;
        _maxine = maxine;
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index() => View();

    /// <summary>Aggregated health snapshot for the dashboard (polled on demand by the page).</summary>
    [HttpGet]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        // --- LTX server ---
        var portListening = _ltxControl.IsPortListening();
        object ltx;
        try
        {
            var raw = await _ltx.GetHealthRawAsync(ct);
            using var doc = JsonDocument.Parse(raw);
            ltx = new
            {
                reachable = true,
                portListening,
                port = _ltxControl.Port,
                health = doc.RootElement.Clone(),
                error = (string?)null,
            };
        }
        catch (Exception ex)
        {
            ltx = new
            {
                reachable = false,
                portListening,
                port = _ltxControl.Port,
                health = (object?)null,
                error = ex.Message,
            };
        }

        // --- Maxine upscaler (on-demand exe, not a server) ---
        var maxineReady = _maxine.IsReady(out var maxineProblem);

        // --- Web app + output disk ---
        var proc = Process.GetCurrentProcess();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var app = new
        {
            version,
            machine = Environment.MachineName,
            startedUtc = proc.StartTime.ToUniversalTime(),
            uptimeSeconds = (long)(DateTime.Now - proc.StartTime).TotalSeconds,
        };

        return Json(new
        {
            ok = true,
            timeUtc = DateTime.UtcNow,
            ltx,
            maxine = new { ready = maxineReady, error = maxineProblem },
            app,
            output = DiskInfo(_ltx.OutputDirectory),
        });
    }

    /// <summary>Stops and restarts the local LTX server. Returns the script output.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RestartLtx(CancellationToken ct)
    {
        var result = await _ltxControl.RestartAsync(ct);
        return Json(new { ok = result.Ok, output = result.Output });
    }

    private static object DiskInfo(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return new { path, exists = Directory.Exists(path) };
            var d = new DriveInfo(root);
            return new
            {
                path,
                exists = Directory.Exists(path),
                drive = root,
                freeGb = Math.Round(d.AvailableFreeSpace / 1024d / 1024 / 1024, 1),
                totalGb = Math.Round(d.TotalSize / 1024d / 1024 / 1024, 1),
            };
        }
        catch
        {
            return new { path, exists = Directory.Exists(path) };
        }
    }
}
