using System.Text.Json;
using KeithVision.Models.SoundGen;
using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>
/// Generates instrumental music from a text prompt via the self-hosted MusicGen
/// server and writes the WAV into the LTX input dir, so it can be handed to the LTX
/// server as the audio track for audio-to-video — the same destination and shape as
/// a generated sound effect. Mirrors <see cref="SoundGenService"/>, including the
/// on-demand server start so the "Generate Music" node just works.
/// </summary>
public sealed class MusicGenService
{
    private readonly LocalMusicClient _client;
    private readonly LocalMusicOptions _options;
    private readonly LtxVideoOptions _ltx;
    private readonly MusicServerControl _control;
    private readonly ILogger<MusicGenService> _logger;

    public MusicGenService(
        LocalMusicClient client,
        IOptions<LocalMusicOptions> options,
        IOptions<LtxVideoOptions> ltx,
        MusicServerControl control,
        ILogger<MusicGenService> logger)
    {
        _client = client;
        _options = options.Value;
        _ltx = ltx.Value;
        _control = control;
        _logger = logger;
    }

    /// <summary>
    /// Pings the local music server's /health. ok=false (with a reason) when the
    /// server is unreachable or the model hasn't finished loading.
    /// </summary>
    public async Task<(bool Ok, string? Error)> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            // Bound the health probe so the Admin Status page can't block on a wedged server.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var raw = await _client.GetHealthRawAsync(cts.Token);
            using var doc = JsonDocument.Parse(raw);
            var loaded = doc.RootElement.TryGetProperty("model_loaded", out var ml) && ml.GetBoolean();
            if (!loaded)
            {
                var err = doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString() : null;
                return (false, err ?? "Music model is still loading or failed to load.");
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Local music server isn't reachable on {_options.BaseUrl} — start tools/run-music-server.ps1. ({ex.Message})");
        }
    }

    /// <summary>
    /// Generates music for <paramref name="prompt"/> and stages it. A non-positive
    /// duration lets the server choose the length; larger values are capped.
    /// </summary>
    public async Task<StagedAudio> GenerateAsync(string prompt, double? seconds, CancellationToken ct = default)
    {
        await EnsureUpAsync(ct);
        double? s = seconds is > 0 ? Math.Min(seconds.Value, _options.MaxDurationSeconds) : null;
        var bytes = await _client.GenerateMusicAsync(prompt, s, ct);

        Directory.CreateDirectory(_ltx.InputDirectory);
        var fileName = $"musicgen_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.wav";
        var path = Path.Combine(_ltx.InputDirectory, fileName);
        await File.WriteAllBytesAsync(path, bytes, ct);

        _logger.LogInformation("Generated music for '{Prompt}' → {Path} ({Bytes} bytes)", prompt, path, bytes.Length);
        return new StagedAudio(fileName, path, prompt);
    }

    /// <summary>
    /// Starts the music server if it isn't listening and waits (best-effort) for the model
    /// to report loaded, so the Generate Music node "just works" without pre-starting it
    /// from the Admin page. Mirrors <see cref="SoundGenService"/>'s on-demand start.
    /// </summary>
    private async Task EnsureUpAsync(CancellationToken ct)
    {
        if (_control.IsPortListening()) return;

        _logger.LogInformation("Music server not running — starting it on demand.");
        if (!_control.StartDetached()) return;   // script missing; GenerateAsync will then surface the connection error

        // Wait for the port to bind, then for the model to report loaded (MusicGen load + first download can take a while).
        for (var i = 0; i < 60 && !_control.IsPortListening(); i++)
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        for (var i = 0; i < 120; i++)
        {
            var (ok, _) = await GetHealthAsync(ct);
            if (ok) return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>Resolves a bare file name to a path inside the staging dir (guards against path traversal).</summary>
    public string GetStagedFilePath(string name) => Path.Combine(_ltx.InputDirectory, Path.GetFileName(name));
}
