using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ClaudeCore.Services;
using Microsoft.AspNetCore.Mvc;

namespace ClaudeCore.Controllers;

/// <summary>
/// Local administration dashboard: health of the LTX server, the Maxine upscaler,
/// ffmpeg, the GPU, and the web app itself, plus controls to restart the LTX
/// server and clean up staging files.
/// Localhost-only app (Kestrel binds 127.0.0.1), so no separate auth here — same
/// trust model as the rest of the site.
/// </summary>
public class AdminController : Controller
{
    private readonly LtxVideoService _ltx;
    private readonly LtxServerControl _ltxControl;
    private readonly MaxineUpscaleService _maxine;
    private readonly VideoSpeedService _speed;
    private readonly SystemStatsService _stats;
    private readonly ILogger<AdminController> _logger;

    public AdminController(
        LtxVideoService ltx,
        LtxServerControl ltxControl,
        MaxineUpscaleService maxine,
        VideoSpeedService speed,
        SystemStatsService stats,
        ILogger<AdminController> logger)
    {
        _ltx = ltx;
        _ltxControl = ltxControl;
        _maxine = maxine;
        _speed = speed;
        _stats = stats;
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

        // --- ffmpeg (on-demand exe, used for re-time + H.264 normalization) ---
        var ffmpegReady = _speed.IsReady(out var ffmpegProblem);

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
            ffmpeg = new { ready = ffmpegReady, error = ffmpegProblem, path = _speed.FfmpegPath },
            app,
            output = DiskInfo(_ltx.OutputDirectory),
            staging = StagingInfo(),
        });
    }

    /// <summary>Lightweight live GPU + CPU snapshot — polled by the footer on every page.</summary>
    [HttpGet]
    public async Task<IActionResult> SystemStats(CancellationToken ct)
        => Json(new { gpu = await GetGpuAsync(ct), cpu = _stats.Cpu, memory = _stats.Memory });

    /// <summary>Stops and restarts the local LTX server. Returns the script output.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RestartLtx(CancellationToken ct)
    {
        var result = await _ltxControl.RestartAsync(ct);
        return Json(new { ok = result.Ok, output = result.Output });
    }

    /// <summary>Deletes staged/intermediate files (uploads + temp H.264) without touching real outputs.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult CleanStaging()
    {
        long files = 0, bytes = 0;
        foreach (var dir in StagingDirs())
            CleanDir(dir.Path, dir.Pattern, ref files, ref bytes);

        _logger.LogInformation("Cleaned {Files} staging files ({Bytes} bytes).", files, bytes);
        return Json(new { ok = true, files, bytes, mb = Math.Round(bytes / 1024d / 1024, 1) });
    }

    // --- helpers ------------------------------------------------------------

    /// <summary>The staging/intermediate locations that are safe to wipe (never the output dirs).</summary>
    private IEnumerable<(string Path, string Pattern)> StagingDirs() => new[]
    {
        (_ltx.InputDirectory, "*"),
        (_maxine.InputDirectory, "*"),
        (_speed.InputDirectory, "*"),
        (Path.GetTempPath(), "h264_*.mp4"), // transcode temps from VideoSpeedService
    };

    private static void CleanDir(string path, string pattern, ref long files, ref long bytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        foreach (var f in Directory.EnumerateFiles(path, pattern, SearchOption.TopDirectoryOnly))
        {
            try
            {
                var len = new FileInfo(f).Length;
                System.IO.File.Delete(f);
                files++;
                bytes += len;
            }
            catch { /* in use / locked — skip */ }
        }
    }

    private object StagingInfo()
    {
        long files = 0, bytes = 0;
        foreach (var dir in StagingDirs())
        {
            if (string.IsNullOrWhiteSpace(dir.Path) || !Directory.Exists(dir.Path)) continue;
            foreach (var f in Directory.EnumerateFiles(dir.Path, dir.Pattern, SearchOption.TopDirectoryOnly))
            {
                try { bytes += new FileInfo(f).Length; files++; } catch { /* ignore */ }
            }
        }
        return new { files, bytes, mb = Math.Round(bytes / 1024d / 1024, 1) };
    }

    /// <summary>Live GPU stats from nvidia-smi (works even when the LTX server is down). Null fields if unavailable.</summary>
    private static async Task<object> GetGpuAsync(CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[]
            {
                "--query-gpu=name,memory.used,memory.total,utilization.gpu,temperature.gpu",
                "--format=csv,noheader,nounits",
            }) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            var outText = await proc.StandardOutput.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await proc.WaitForExitAsync(cts.Token);

            var line = outText.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
            if (line is null) return new { available = false, error = "no GPU reported" };

            var p = line.Split(',').Select(s => s.Trim()).ToArray();
            int? ParseInt(int i) => i < p.Length && int.TryParse(p[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

            return new
            {
                available = true,
                name = p.Length > 0 ? p[0] : null,
                vramUsedMb = ParseInt(1),
                vramTotalMb = ParseInt(2),
                utilizationPct = ParseInt(3),
                temperatureC = ParseInt(4),
                error = (string?)null,
            };
        }
        catch (Exception ex)
        {
            return new { available = false, error = ex.Message };
        }
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
