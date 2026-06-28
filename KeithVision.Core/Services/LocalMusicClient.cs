using KeithVision.Models.SoundGen;

namespace KeithVision.Services;

/// <summary>
/// Typed HttpClient over the local MusicGen server (tools/music_server.py). Shares the
/// request shape ({text, seconds}) and WAV-bytes response with the Stable Audio server,
/// so it reuses <see cref="LocalAudioRequest"/>.
/// </summary>
public sealed class LocalMusicClient
{
    private readonly HttpClient _http;

    public LocalMusicClient(HttpClient http) => _http = http;

    /// <summary>Raw /health JSON (model/device/sample_rate) for a readiness check.</summary>
    public async Task<string> GetHealthRawAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/health", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Generates instrumental music from text and returns the raw WAV bytes.</summary>
    public async Task<byte[]> GenerateMusicAsync(string text, double? seconds, CancellationToken ct = default)
    {
        var body = new LocalAudioRequest { Text = text, Seconds = seconds };
        using var resp = await _http.PostAsJsonAsync("/generate", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Music server returned {(int)resp.StatusCode}: {err}");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}
