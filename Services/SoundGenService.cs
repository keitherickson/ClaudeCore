using System.Text.Json;
using ClaudeCore.Models.SoundGen;
using Microsoft.Extensions.Options;

namespace ClaudeCore.Services;

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
    private readonly ILogger<SoundGenService> _logger;

    public SoundGenService(
        LocalAudioClient client,
        IOptions<LocalAudioOptions> options,
        IOptions<LtxVideoOptions> ltx,
        ILogger<SoundGenService> logger)
    {
        _client = client;
        _options = options.Value;
        _ltx = ltx.Value;
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
        double? s = seconds is > 0 ? Math.Min(seconds.Value, _options.MaxDurationSeconds) : null;
        var bytes = await _client.GenerateSoundAsync(prompt, s, ct);

        Directory.CreateDirectory(_ltx.InputDirectory);
        var fileName = $"sfxgen_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.wav";
        var path = Path.Combine(_ltx.InputDirectory, fileName);
        await File.WriteAllBytesAsync(path, bytes, ct);

        _logger.LogInformation("Generated sound for '{Prompt}' → {Path} ({Bytes} bytes)", prompt, path, bytes.Length);
        return new StagedAudio(fileName, path, prompt);
    }

    /// <summary>Resolves a bare file name to a path inside the staging dir (guards against path traversal).</summary>
    public string GetStagedFilePath(string name) => Path.Combine(_ltx.InputDirectory, Path.GetFileName(name));
}
