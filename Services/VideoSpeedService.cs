using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace ClaudeCore.Services;

public sealed record SpeedResult(string FileName, string SavedPath, double Speed, string Command);

/// <summary>
/// Re-times a video to play faster (or slower) by shelling out to ffmpeg's
/// <c>setpts</c> filter. Used as an optional step after Maxine upscaling.
///
/// The Maxine upscaler writes video-only files (its OpenCV writer drops audio),
/// so this re-times video only (<c>-an</c>) and re-encodes to browser-friendly
/// H.264. The output is written next to the input file, so the existing
/// /Upscale/Download endpoint (which serves the upscaled output folder) can
/// serve it without any new route.
/// </summary>
public sealed class VideoSpeedService
{
    private readonly VideoSpeedOptions _o;
    private readonly ILogger<VideoSpeedService> _log;

    public VideoSpeedService(IOptions<VideoSpeedOptions> o, ILogger<VideoSpeedService> log)
    {
        _o = o.Value;
        _log = log;
    }

    /// <summary>Reports whether ffmpeg looks runnable, for a status badge.</summary>
    public bool IsReady(out string? problem)
    {
        // A bare "ffmpeg" means "rely on PATH" — we can't cheaply verify that, so
        // treat it as ready. An absolute path we can check for real.
        if (_o.FfmpegPath.IndexOfAny(new[] { '\\', '/' }) >= 0 && !File.Exists(_o.FfmpegPath))
        {
            problem = $"ffmpeg not found at {_o.FfmpegPath} — install it (winget install Gyan.FFmpeg) or fix VideoSpeed:FfmpegPath.";
            return false;
        }
        problem = null;
        return true;
    }

    /// <summary>
    /// Produces a copy of <paramref name="inputPath"/> that plays at
    /// <paramref name="speed"/>× (e.g. 2.0 = twice as fast). The new file is
    /// written alongside the input with an "_xN" suffix.
    /// </summary>
    public async Task<SpeedResult> RetimeAsync(string inputPath, double speed, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Video to re-time was not found.", inputPath);
        if (speed <= 0)
            throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be greater than 0.");

        var dir = Path.GetDirectoryName(inputPath)!;
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        // 2 -> "x2", 1.5 -> "x1_5" (filename-safe).
        var tag = "x" + speed.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', '_');
        var outName = $"{stem}_{tag}.mp4";
        var outPath = Path.Combine(dir, outName);

        var pts = (1.0 / speed).ToString("0.######", CultureInfo.InvariantCulture);
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", inputPath,
            "-filter:v", $"setpts={pts}*PTS",
            "-an",                       // upscaled source is video-only
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            "-movflags", "+faststart",
            outPath,
        };

        var psi = new ProcessStartInfo
        {
            FileName = _o.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var cmd = $"\"{_o.FfmpegPath}\" {string.Join(' ', args)}";
        _log.LogInformation("Re-time {Speed}x: {Cmd}", speed, cmd);

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.OutputDataReceived += (_, _) => { /* ffmpeg writes progress to stderr */ };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(_o.TimeoutMinutes));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException("Speed-up timed out or was cancelled.");
        }

        if (proc.ExitCode != 0 || !File.Exists(outPath))
            throw new InvalidOperationException($"ffmpeg re-time failed (exit {proc.ExitCode}).\n{stderr}".Trim());

        _log.LogInformation("Re-timed video saved to {Out}", outPath);
        return new SpeedResult(outName, outPath, speed, cmd);
    }
}
