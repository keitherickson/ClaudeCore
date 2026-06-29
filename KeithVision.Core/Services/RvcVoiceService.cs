using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>
/// AI voice conversion via the self-hosted rvc-python server: replaces the speaker's
/// timbre with a target voice (a .pth model), so you sound like a different person —
/// unlike the ffmpeg effects in <see cref="VoiceChangerService"/>, which only reshape
/// your own voice. Converts the recorded/uploaded clip to WAV with ffmpeg (reusing the
/// VideoSpeed ffmpeg path), then drives the RVC server: load target model → set pitch →
/// /convert_file. Writes the result into the same output dir as the ffmpeg effects so the
/// existing /Voice/Audio endpoint serves it. On-demand server start mirrors
/// <see cref="MusicGenService"/>.
/// </summary>
public sealed class RvcVoiceService
{
    // The RVC server holds the loaded model + params as shared state, so conversions must
    // not interleave (one request's model/pitch would bleed into another). Serialize them.
    private static readonly SemaphoreSlim _gate = new(1, 1);

    private readonly LocalRvcClient _client;
    private readonly LocalRvcOptions _options;
    private readonly VideoSpeedOptions _speed;   // ffmpeg path, output dir, timeout
    private readonly RvcServerControl _control;
    private readonly ILogger<RvcVoiceService> _logger;

    public RvcVoiceService(
        LocalRvcClient client,
        IOptions<LocalRvcOptions> options,
        IOptions<VideoSpeedOptions> speed,
        RvcServerControl control,
        ILogger<RvcVoiceService> logger)
    {
        _client = client;
        _options = options.Value;
        _speed = speed.Value;
        _control = control;
        _logger = logger;
    }

    public string OutputDirectory => _speed.OutputDirectory;

    /// <summary>Pings the RVC server (GET /models doubles as health). ok=false (with reason) if unreachable.</summary>
    public async Task<(bool Ok, string? Error)> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            await _client.GetModelsAsync(cts.Token);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Local RVC server isn't reachable on {_options.BaseUrl} — start tools/run-rvc-server.ps1. ({ex.Message})");
        }
    }

    /// <summary>Starts the server if needed and returns the available target-voice names.</summary>
    public async Task<IReadOnlyList<string>> ListVoicesAsync(CancellationToken ct = default)
    {
        await EnsureUpAsync(ct);
        return await _client.GetModelsAsync(ct);
    }

    /// <summary>
    /// Converts <paramref name="inputPath"/> to the <paramref name="voice"/> target, shifting
    /// pitch by <paramref name="transpose"/> semitones (use ±12 for cross-gender). Writes a WAV
    /// to the output dir and returns it.
    /// </summary>
    public async Task<VoiceResult> ConvertAsync(string inputPath, string? voice, int transpose, CancellationToken ct = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("Audio to convert was not found.", inputPath);

        await EnsureUpAsync(ct);

        // RVC's /convert_file wants WAV; recordings arrive as webm/ogg. Transcode to mono 44.1 kHz.
        var wav = await ToWavAsync(inputPath, ct);
        try
        {
            var bytes = await File.ReadAllBytesAsync(wav, ct);
            Directory.CreateDirectory(_speed.OutputDirectory);
            var outPath = Path.Combine(_speed.OutputDirectory, $"rvc_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.wav");

            await _gate.WaitAsync(ct);
            try
            {
                if (!string.IsNullOrWhiteSpace(voice)) await _client.LoadModelAsync(voice!, ct);
                await _client.SetParamsAsync(transpose, _options.F0Method, ct);
                var converted = await _client.ConvertFileAsync(bytes, ct);
                await File.WriteAllBytesAsync(outPath, converted, ct);
            }
            finally
            {
                _gate.Release();
            }

            _logger.LogInformation("RVC convert '{In}' -> voice '{Voice}' ({Semi}st) -> {Out}", inputPath, voice, transpose, outPath);
            return new VoiceResult(Path.GetFileName(outPath), outPath);
        }
        finally
        {
            try { File.Delete(wav); } catch { /* temp cleanup is best effort */ }
        }
    }

    /// <summary>Transcodes any input to a mono 44.1 kHz WAV in the temp dir; returns its path.</summary>
    private async Task<string> ToWavAsync(string inputPath, CancellationToken ct)
    {
        var outPath = Path.Combine(Path.GetTempPath(), $"rvcin_{Guid.NewGuid():N}.wav");
        var args = new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", inputPath, "-ac", "1", "-ar", "44100", "-c:a", "pcm_s16le", outPath,
        };
        var psi = new ProcessStartInfo
        {
            FileName = _speed.FfmpegPath,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        proc.OutputDataReceived += (_, _) => { };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(_speed.TimeoutMinutes));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException("Audio→WAV transcode timed out or was cancelled.");
        }
        if (proc.ExitCode != 0 || !File.Exists(outPath))
            throw new InvalidOperationException($"ffmpeg WAV transcode failed (exit {proc.ExitCode}).\n{stderr}".Trim());
        return outPath;
    }

    /// <summary>
    /// Starts the RVC server if it isn't listening and waits (best-effort) for it to answer
    /// /models. Mirrors <see cref="MusicGenService"/>'s on-demand start.
    /// </summary>
    private async Task EnsureUpAsync(CancellationToken ct)
    {
        if (_control.IsPortListening()) return;

        _logger.LogInformation("RVC server not running — starting it on demand.");
        if (!_control.StartDetached()) return;   // script missing; the next call surfaces the connection error

        for (var i = 0; i < 60 && !_control.IsPortListening(); i++)
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        for (var i = 0; i < 90; i++)
        {
            var (ok, _) = await GetHealthAsync(ct);
            if (ok) return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
