using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace ClaudeCore.Services;

public sealed record SpeedResult(string FileName, string SavedPath, double Speed, string Command);

/// <summary>
/// Re-times a video to play faster by shelling out to ffmpeg's <c>setpts</c>
/// filter (and <c>atempo</c> for audio when kept). Two entry points:
///
///  - <see cref="RetimeAsync"/> — used after Maxine upscaling. The upscaled file
///    has had its audio restored (see <see cref="RestoreAudioAsync"/>), so it
///    re-times video and audio together and writes the result next to the input.
///  - <see cref="StageInputAsync"/> + <see cref="RetimeUploadAsync"/> — the
///    standalone /Speed page: stages an uploaded clip, re-times it (keeping and
///    re-timing audio when present), and writes to the configured output dir.
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

    public string OutputDirectory => _o.OutputDirectory;

    /// <summary>Staging dir for uploaded videos (Speed page).</summary>
    public string InputDirectory => _o.InputDirectory;

    /// <summary>Configured ffmpeg path (for the admin badge).</summary>
    public string FfmpegPath => _o.FfmpegPath;

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

    /// <summary>Saves an uploaded video to the staging input dir and returns its path.</summary>
    public async Task<string> StageInputAsync(IFormFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_o.InputDirectory);
        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".mp4";
        var path = Path.Combine(_o.InputDirectory, $"speed_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}");
        await using var fs = File.Create(path);
        await file.CopyToAsync(fs, ct);
        return path;
    }

    /// <summary>
    /// Re-times <paramref name="inputPath"/> to play at <paramref name="speed"/>×,
    /// writing a "_xN.mp4" alongside it. Keeps and re-times audio when the input has
    /// a track (upscaled clips get their audio restored before this runs).
    /// </summary>
    public Task<SpeedResult> RetimeAsync(string inputPath, double speed, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(inputPath)!;
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var outPath = Path.Combine(dir, $"{stem}_{Tag(speed)}.mp4");
        return RunRetimeAsync(inputPath, speed, outPath, keepAudio: true, ct);
    }

    /// <summary>
    /// Re-times a staged upload to the configured output dir, keeping and
    /// re-timing audio when the source has an audio track. For the /Speed page.
    /// </summary>
    public Task<SpeedResult> RetimeUploadAsync(string inputPath, double speed, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_o.OutputDirectory);
        var outName = $"sped_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}_{Tag(speed)}.mp4";
        var outPath = Path.Combine(_o.OutputDirectory, outName);
        return RunRetimeAsync(inputPath, speed, outPath, keepAudio: true, ct);
    }

    public string GetOutputFilePath(string name) => Path.Combine(_o.OutputDirectory, Path.GetFileName(name));

    /// <summary>
    /// Maxine's OpenCV writer produces a video-only file, so the source audio is
    /// lost. Re-muxes the audio track from <paramref name="audioSourcePath"/> (the
    /// original upload) onto the upscaled <paramref name="videoPath"/> in place,
    /// copying the video stream untouched (no re-encode). No-op if the source has
    /// no audio or ffprobe is unavailable.
    /// </summary>
    public async Task RestoreAudioAsync(string videoPath, string audioSourcePath, CancellationToken ct = default)
    {
        if (!File.Exists(videoPath) || !File.Exists(audioSourcePath)) return;
        if (!await HasAudioStreamAsync(audioSourcePath, ct)) return;

        var temp = Path.Combine(Path.GetTempPath(), $"mux_{Guid.NewGuid():N}.mp4");
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", videoPath,        // 0: upscaled, video-only
            "-i", audioSourcePath,  // 1: original upload, for its audio
            "-map", "0:v:0", "-map", "1:a:0",
            "-c:v", "copy", "-c:a", "aac",
            "-shortest", "-movflags", "+faststart",
            temp,
        };
        _log.LogInformation("Restoring audio from {Src} onto upscaled {Video}", audioSourcePath, videoPath);
        var (exit, stderr) = await RunFfmpegAsync(args, ct);
        if (exit != 0 || !File.Exists(temp))
            throw new InvalidOperationException($"ffmpeg audio mux failed (exit {exit}).\n{stderr}".Trim());

        File.Copy(temp, videoPath, overwrite: true);
        try { File.Delete(temp); } catch { /* temp cleanup is best effort */ }
    }

    /// <summary>
    /// Maxine's VideoEffectsApp only accepts H.264 input ("Filters only target
    /// H264 videos, not HEVC"). If <paramref name="inputPath"/> isn't H.264,
    /// transcode a temp H.264 copy (video-only — Maxine drops audio anyway) and
    /// return its path; otherwise return the input unchanged.
    /// </summary>
    public async Task<string> EnsureH264Async(string inputPath, CancellationToken ct = default)
    {
        var codec = await GetVideoCodecAsync(inputPath, ct);
        if (codec is null || codec.Equals("h264", StringComparison.OrdinalIgnoreCase))
            return inputPath;

        var outPath = Path.Combine(Path.GetTempPath(), $"h264_{Guid.NewGuid():N}.mp4");
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", inputPath,
            "-map", "0:v:0",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart",
            "-an",
            outPath,
        };
        _log.LogInformation("Transcoding {Codec} -> H.264 for Maxine: {In} -> {Out}", codec, inputPath, outPath);
        var (exit, stderr) = await RunFfmpegAsync(args, ct);
        if (exit != 0 || !File.Exists(outPath))
            throw new InvalidOperationException($"ffmpeg transcode to H.264 failed (exit {exit}).\n{stderr}".Trim());
        return outPath;
    }

    /// <summary>
    /// Ensures a saved video is H.264 in place (LTX emits HEVC; H.264 plays in all
    /// browsers and feeds Maxine). No-op if already H.264. Unlike
    /// <see cref="EnsureH264Async"/>, this keeps and re-encodes audio, since it
    /// runs on the user-facing generated file.
    /// </summary>
    public async Task ConvertToH264InPlaceAsync(string path, CancellationToken ct = default)
    {
        var codec = await GetVideoCodecAsync(path, ct);
        if (codec is null || codec.Equals("h264", StringComparison.OrdinalIgnoreCase))
            return;

        var hasAudio = await HasAudioStreamAsync(path, ct);
        var temp = Path.Combine(Path.GetTempPath(), $"h264_{Guid.NewGuid():N}.mp4");
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", path,
            "-map", "0:v:0",
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
        };
        if (hasAudio) { args.AddRange(new[] { "-map", "0:a:0?", "-c:a", "aac" }); }
        else { args.Add("-an"); }
        args.AddRange(new[] { "-movflags", "+faststart", temp });

        _log.LogInformation("Converting {Codec} -> H.264 in place (audio={Audio}): {Path}", codec, hasAudio, path);
        var (exit, stderr) = await RunFfmpegAsync(args, ct);
        if (exit != 0 || !File.Exists(temp))
            throw new InvalidOperationException($"H.264 conversion failed (exit {exit}).\n{stderr}".Trim());

        File.Copy(temp, path, overwrite: true);
        try { File.Delete(temp); } catch { /* temp cleanup is best effort */ }
    }

    /// <summary>
    /// Extracts the final frame of <paramref name="videoPath"/> as a PNG into
    /// <paramref name="destDir"/> and returns its path. This is the primitive
    /// behind "Extend": the last frame of one clip becomes the conditioning image
    /// (frame 0) of the next LTX generation, so the segments join continuously.
    /// </summary>
    public async Task<string> ExtractLastFrameAsync(string videoPath, string destDir, CancellationToken ct = default)
    {
        if (!File.Exists(videoPath))
            throw new FileNotFoundException("Video to extend was not found.", videoPath);

        Directory.CreateDirectory(destDir);
        var outPath = Path.Combine(destDir, $"tail_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.png");

        // -sseof -1 seeks ~1s before the end; -update 1 overwrites the output on
        // every decoded frame, so the file is left holding the very last frame.
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-sseof", "-1", "-i", videoPath,
            "-update", "1", "-q:v", "2", "-an",
            outPath,
        };
        _log.LogInformation("Extracting last frame for extend: {In} -> {Out}", videoPath, outPath);
        var (exit, stderr) = await RunFfmpegAsync(args, ct);
        if (exit != 0 || !File.Exists(outPath))
            throw new InvalidOperationException($"ffmpeg last-frame extract failed (exit {exit}).\n{stderr}".Trim());
        return outPath;
    }

    /// <summary>
    /// Concatenates <paramref name="inputs"/> (in order) into a single H.264 MP4 at
    /// <paramref name="outPath"/> and returns that path. Re-encodes across the seams
    /// so clips from separate LTX generations join cleanly, and keeps audio when the
    /// inputs have it. All inputs must share resolution/fps (chained LTX segments do).
    /// </summary>
    public async Task<string> ConcatAsync(IReadOnlyList<string> inputs, string outPath, CancellationToken ct = default)
    {
        if (inputs.Count == 0)
            throw new ArgumentException("No segments to stitch.", nameof(inputs));
        foreach (var p in inputs)
            if (!File.Exists(p)) throw new FileNotFoundException("Segment to stitch was not found.", p);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        // The concat demuxer reads a list file; ffmpeg accepts forward slashes on
        // Windows, and single quotes in a path are escaped as '\'' per its syntax.
        var listPath = Path.Combine(Path.GetTempPath(), $"concat_{Guid.NewGuid():N}.txt");
        var sb = new StringBuilder();
        foreach (var p in inputs)
            sb.Append("file '").Append(p.Replace('\\', '/').Replace("'", "'\\''")).Append("'\n");
        await File.WriteAllTextAsync(listPath, sb.ToString(), ct);

        var withAudio = await HasAudioStreamAsync(inputs[0], ct);
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "concat", "-safe", "0", "-i", listPath,
            "-c:v", "libx264", "-pix_fmt", "yuv420p",
        };
        if (withAudio) { args.AddRange(new[] { "-c:a", "aac" }); }
        else { args.Add("-an"); }
        args.AddRange(new[] { "-movflags", "+faststart", outPath });

        _log.LogInformation("Stitching {Count} segments (audio={Audio}) -> {Out}", inputs.Count, withAudio, outPath);
        try
        {
            var (exit, stderr) = await RunFfmpegAsync(args, ct);
            if (exit != 0 || !File.Exists(outPath))
                throw new InvalidOperationException($"ffmpeg concat failed (exit {exit}).\n{stderr}".Trim());
        }
        finally
        {
            try { File.Delete(listPath); } catch { /* best effort */ }
        }
        return outPath;
    }

    // --- core ---------------------------------------------------------------

    private async Task<SpeedResult> RunRetimeAsync(string inputPath, double speed, string outPath, bool keepAudio, CancellationToken ct)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Video to re-time was not found.", inputPath);
        if (speed <= 0)
            throw new ArgumentOutOfRangeException(nameof(speed), "Speed must be greater than 0.");

        var pts = (1.0 / speed).ToString("0.######", CultureInfo.InvariantCulture);
        var withAudio = keepAudio && await HasAudioStreamAsync(inputPath, ct);

        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", inputPath,
            "-filter:v", $"setpts={pts}*PTS",
        };
        if (withAudio)
        {
            args.Add("-filter:a"); args.Add(BuildAtempoChain(speed));
            args.Add("-c:a"); args.Add("aac");
        }
        else
        {
            args.Add("-an");
        }
        args.AddRange(new[] { "-c:v", "libx264", "-pix_fmt", "yuv420p", "-movflags", "+faststart", outPath });

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
        _log.LogInformation("Re-time {Speed}x (audio={Audio}): {Cmd}", speed, withAudio, cmd);

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
        return new SpeedResult(Path.GetFileName(outPath), outPath, speed, cmd);
    }

    /// <summary>2 -> "x2", 1.5 -> "x1_5" (filename-safe).</summary>
    private static string Tag(double speed)
        => "x" + speed.ToString("0.##", CultureInfo.InvariantCulture).Replace('.', '_');

    /// <summary>
    /// atempo only accepts 0.5–2.0 per instance, so chain factors that multiply
    /// to <paramref name="speed"/> (e.g. 3 -> atempo=2.0,atempo=1.5; 4 -> atempo=2.0,atempo=2.0).
    /// </summary>
    private static string BuildAtempoChain(double speed)
    {
        var factors = new List<double>();
        var remaining = speed;
        while (remaining > 2.0) { factors.Add(2.0); remaining /= 2.0; }
        factors.Add(remaining);
        return string.Join(",", factors.Select(f => $"atempo={f.ToString("0.######", CultureInfo.InvariantCulture)}"));
    }

    /// <summary>Runs ffmpeg with the given args; returns its exit code and captured stderr.</summary>
    private async Task<(int Exit, string Stderr)> RunFfmpegAsync(List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _o.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.OutputDataReceived += (_, _) => { };

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
            throw new InvalidOperationException("ffmpeg timed out or was cancelled.");
        }
        return (proc.ExitCode, stderr.ToString());
    }

    private async Task<string?> GetVideoCodecAsync(string path, CancellationToken ct)
    {
        var probe = ResolveFfprobePath();
        if (probe is null) return null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = probe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", path })
                psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var stdout = (await ReadProbeOutputAsync(proc, ct)).Trim();
            return string.IsNullOrWhiteSpace(stdout) ? null : stdout;
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> HasAudioStreamAsync(string path, CancellationToken ct)
    {
        var probe = ResolveFfprobePath();
        if (probe is null) return false;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = probe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "-v", "error", "-select_streams", "a", "-show_entries", "stream=index", "-of", "csv=p=0", path })
                psi.ArgumentList.Add(a);

            using var proc = new Process { StartInfo = psi };
            proc.Start();
            var stdout = await ReadProbeOutputAsync(proc, ct);
            return !string.IsNullOrWhiteSpace(stdout);
        }
        catch
        {
            return false; // if we can't probe, fall back to video-only
        }
    }

    /// <summary>
    /// Reads a running ffprobe's stdout with a hard timeout, killing the process
    /// if it hangs (a wedged probe must never block a generation/upscale request).
    /// </summary>
    private static async Task<string> ReadProbeOutputAsync(Process proc, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            // ffprobe closes stdout on exit, so bounding the read bounds the whole call.
            var stdout = await proc.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            await proc.WaitForExitAsync(timeoutCts.Token);
            return stdout;
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>ffprobe lives beside ffmpeg in the same install.</summary>
    private string? ResolveFfprobePath()
    {
        var f = _o.FfmpegPath;
        if (f.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            return f[..^"ffmpeg.exe".Length] + "ffprobe.exe";
        if (f.Equals("ffmpeg", StringComparison.OrdinalIgnoreCase))
            return "ffprobe";
        var dir = Path.GetDirectoryName(f);
        if (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "ffprobe.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
