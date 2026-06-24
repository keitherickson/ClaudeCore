using System.Text.Json;
using KeithVision.Models.SoundGen;
using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>
/// Generates a sound effect from a text prompt via the self-hosted Stable Audio
/// server and writes the WAV into the LTX input dir, so it can be handed to the LTX
/// server as the audio track for audio-to-video — the same destination and shape as
/// an uploaded audio file. (The LTX server sniffs wav, so no conversion is needed.)
/// </summary>
public sealed class SoundGenService
{
    private readonly LocalAudioClient _client;
    private readonly LocalAudioOptions _options;
    private readonly LtxVideoOptions _ltx;
    private readonly AudioServerControl _control;
    private readonly ILogger<SoundGenService> _logger;

    public SoundGenService(
        LocalAudioClient client,
        IOptions<LocalAudioOptions> options,
        IOptions<LtxVideoOptions> ltx,
        AudioServerControl control,
        ILogger<SoundGenService> logger)
    {
        _client = client;
        _options = options.Value;
        _ltx = ltx.Value;
        _control = control;
        _logger = logger;
    }

    /// <summary>
    /// Pings the local audio server's /health. ok=false (with a reason) when the
    /// server is unreachable or the model hasn't finished loading.
    /// </summary>
    public async Task<(bool Ok, string? Error)> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            // Bound the health probe so the Admin Status page can't block on a
            // wedged server (a stopped server refuses fast; this caps the slow case).
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var raw = await _client.GetHealthRawAsync(cts.Token);
            using var doc = JsonDocument.Parse(raw);
            var loaded = doc.RootElement.TryGetProperty("model_loaded", out var ml) && ml.GetBoolean();
            if (!loaded)
            {
                var err = doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString() : null;
                return (false, err ?? "Audio model is still loading or failed to load.");
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Local audio server isn't reachable on {_options.BaseUrl} — start tools/run-audio-server.ps1. ({ex.Message})");
        }
    }

    /// <summary>
    /// Generates audio for <paramref name="prompt"/> and stages it. A non-positive
    /// duration lets the server choose the length; larger values are capped.
    /// </summary>
    public async Task<StagedAudio> GenerateAsync(string prompt, double? seconds, CancellationToken ct = default)
    {
        await EnsureUpAsync(ct);
        double? s = seconds is > 0 ? Math.Min(seconds.Value, _options.MaxDurationSeconds) : null;
        var bytes = await _client.GenerateSoundAsync(prompt, s, ct);

        Directory.CreateDirectory(_ltx.InputDirectory);
        var fileName = $"sfxgen_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.wav";
        var path = Path.Combine(_ltx.InputDirectory, fileName);
        await File.WriteAllBytesAsync(path, bytes, ct);

        _logger.LogInformation("Generated sound for '{Prompt}' → {Path} ({Bytes} bytes)", prompt, path, bytes.Length);
        return new StagedAudio(fileName, path, prompt);
    }

    /// <summary>
    /// Starts the audio server if it isn't listening and waits (best-effort) for the model
    /// to report loaded, so the Generate Sound node "just works" without the user pre-starting
    /// it from the Admin page. Mirrors <see cref="PromptEnhanceService"/>'s on-demand start.
    /// </summary>
    private async Task EnsureUpAsync(CancellationToken ct)
    {
        if (_control.IsPortListening()) return;

        _logger.LogInformation("Audio server not running — starting it on demand.");
        if (!_control.StartDetached()) return;   // script missing; GenerateAsync will then surface the connection error

        // Wait for the port to bind, then for the model to report loaded (Stable Audio load takes ~30-60s).
        for (var i = 0; i < 60 && !_control.IsPortListening(); i++)
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        for (var i = 0; i < 90; i++)
        {
            var (ok, _) = await GetHealthAsync(ct);
            if (ok) return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>Resolves a bare file name to a path inside the staging dir (guards against path traversal).</summary>
    public string GetStagedFilePath(string name) => Path.Combine(_ltx.InputDirectory, Path.GetFileName(name));
}
