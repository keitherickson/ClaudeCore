using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>One selectable voice effect for the /Voice page.</summary>
public sealed record VoicePreset(string Id, string Label, string Description);

/// <summary>The processed clip: the file name and its full path in the output dir.</summary>
public sealed record VoiceResult(string FileName, string SavedPath);

/// <summary>
/// Applies ffmpeg audio-filter "voice changer" effects to a recorded or uploaded
/// clip. Pure local ffmpeg — no model server — so it reuses <see cref="VideoSpeedOptions"/>
/// for the ffmpeg path, the staging input dir, the output dir and the job timeout.
///
/// Effects are either a fixed filter chain (robot, telephone, echo…) or a pitch
/// shift, or both. Pitch is done with the classic asetrate→atempo trick: relabel
/// the sample rate to shift pitch+speed, then atempo back to the original duration.
/// atempo only accepts 0.5–2.0, so the pitch range is clamped to ±12 semitones
/// (one octave), where the correcting 1/ratio stays in range.
/// </summary>
public sealed class VoiceChangerService
{
    // The pitch trick relabels samples at this rate; input is resampled to it first.
    private const int WorkRate = 44100;

    private readonly VideoSpeedOptions _o;
    private readonly ILogger<VoiceChangerService> _log;

    public VoiceChangerService(IOptions<VideoSpeedOptions> o, ILogger<VoiceChangerService> log)
    {
        _o = o.Value;
        _log = log;
    }

    public string OutputDirectory => _o.OutputDirectory;
    public string InputDirectory => _o.InputDirectory;

    /// <summary>A fixed extra filter and/or a pitch offset (semitones) that defines a preset.</summary>
    private sealed record Effect(string Id, string Label, string Description, double Semitones, string? Extra);

    // The curated effect list. "pitch" is the only one whose semitones come from the
    // request (the slider); all others are fixed.
    private static readonly Effect[] Effects =
    {
        new("none",      "Normalize",   "Clean up levels, no pitch change",          0,   "dynaudnorm"),
        new("chipmunk",  "Chipmunk",    "Bright, sped-up high voice",                7,   null),
        new("helium",    "Helium",      "Extreme high, one octave up",               12,  null),
        new("deep",      "Deep",        "Lower, fuller voice",                       -5,  null),
        new("monster",   "Monster",     "Very low with a short growl echo",          -9,  "aecho=0.8:0.88:60:0.4"),
        new("robot",     "Robot",       "Monotone phase-vocoder robotization",       0,   "afftfilt=real='hypot(re,im)*sin(0)':imag='hypot(re,im)*cos(0)':win_size=512:overlap=0.75"),
        new("telephone", "Telephone",   "Tinny 300–3400 Hz phone-line band",         0,   "highpass=f=300,lowpass=f=3400"),
        new("echo",      "Echo",        "Single slap-back echo",                     0,   "aecho=0.8:0.9:1000:0.3"),
        new("cave",      "Cave",        "Layered reverb-like echoes",                0,   "aecho=0.8:0.9:500|600|800:0.4|0.3|0.2"),
        new("alien",     "Alien",       "Warbling, slightly pitched-up",             3,   "vibrato=f=7:d=0.6"),
        new("drunk",     "Drunk",       "Wobbly pitch and tremolo",                  0,   "vibrato=f=3:d=0.85,tremolo=f=5:d=0.6"),
        new("pitch",     "Custom pitch", "Pitch shift by the slider amount",         0,   null),
    };

    /// <summary>The presets for the page's effect buttons.</summary>
    public IReadOnlyList<VoicePreset> Presets =>
        Effects.Select(e => new VoicePreset(e.Id, e.Label, e.Description)).ToList();

    /// <summary>Saves an uploaded/recorded clip to the staging input dir and returns its path.</summary>
    public async Task<string> StageInputAsync(IFormFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_o.InputDirectory);
        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".webm"; // browser MediaRecorder default
        var path = Path.Combine(_o.InputDirectory, $"voicein_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}");
        await using var fs = File.Create(path);
        await file.CopyToAsync(fs, ct);
        return path;
    }

    /// <summary>True if the path is a real file inside the output dir (guards the serve/download endpoint).</summary>
    public bool IsOutputFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(_o.OutputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
        }
        catch { return false; }
    }

    /// <summary>True if the path is a real file inside the staging input dir.</summary>
    public bool IsInputFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(_o.InputDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
        }
        catch { return false; }
    }

    /// <summary>
    /// Applies the named effect to <paramref name="inputPath"/>, writing a 44.1 kHz WAV to
    /// the output dir and returning it. <paramref name="pitchSemitones"/> is only used by the
    /// "pitch" preset; for the others the preset's own pitch (if any) applies.
    /// </summary>
    public async Task<VoiceResult> ProcessAsync(string inputPath, string effectId, double pitchSemitones, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Audio to process was not found.", inputPath);

        var effect = Effects.FirstOrDefault(e => e.Id.Equals(effectId, StringComparison.OrdinalIgnoreCase))
                     ?? throw new ArgumentException($"Unknown effect '{effectId}'.", nameof(effectId));

        var semitones = effect.Id == "pitch" ? pitchSemitones : effect.Semitones;
        var filter = BuildFilter(semitones, effect.Extra);

        Directory.CreateDirectory(_o.OutputDirectory);
        var outPath = Path.Combine(_o.OutputDirectory, $"voice_{effect.Id}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.wav");

        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", inputPath,
            "-filter:a", filter,
            "-ac", "1",                 // voice is mono; keeps files small and the effects predictable
            "-c:a", "pcm_s16le",
            outPath,
        };

        _log.LogInformation("Voice effect '{Effect}' (pitch={Semi}st): {In} -> {Out}", effect.Id, semitones, inputPath, outPath);
        var (exit, stderr) = await RunFfmpegAsync(args, ct);
        if (exit != 0 || !File.Exists(outPath))
            throw new InvalidOperationException($"ffmpeg voice effect failed (exit {exit}).\n{stderr}".Trim());

        return new VoiceResult(Path.GetFileName(outPath), outPath);
    }

    /// <summary>Builds the -filter:a chain from a pitch offset and an optional extra filter.</summary>
    private static string BuildFilter(double semitones, string? extra)
    {
        var parts = new List<string>();

        // Clamp to ±1 octave so the correcting atempo stays inside its 0.5–2.0 window.
        semitones = Math.Clamp(semitones, -12, 12);
        if (Math.Abs(semitones) > 0.01)
        {
            var ratio = Math.Pow(2, semitones / 12.0);
            string F(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);
            // Relabel sample rate (pitch+speed up), resample to standard rate, then atempo back to original length.
            parts.Add($"asetrate={WorkRate}*{F(ratio)}");
            parts.Add($"aresample={WorkRate}");
            parts.Add($"atempo={F(1.0 / ratio)}");
        }

        if (!string.IsNullOrWhiteSpace(extra)) parts.Add(extra);
        return parts.Count == 0 ? "anull" : string.Join(",", parts);
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
            throw new InvalidOperationException("Voice effect timed out or was cancelled.");
        }
        return (proc.ExitCode, stderr.ToString());
    }
}
