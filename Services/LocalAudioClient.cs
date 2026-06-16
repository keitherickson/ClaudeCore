using System.Net.Http.Json;
using System.Text.Json;
using ClaudeCore.Models.SoundGen;

namespace ClaudeCore.Services;

/// <summary>
/// Typed HttpClient over the local Stable Audio server (tools/audio_server.py).
/// The server returns the generated WAV bytes directly.
/// </summary>
public sealed class LocalAudioClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public LocalAudioClient(HttpClient http) => _http = http;

    /// <summary>Raw /health JSON (model/device/sample_rate) for a readiness check.</summary>
    public async Task<string> GetHealthRawAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/health", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Generates a sound effect from text and returns the raw WAV bytes.</summary>
    public async Task<byte[]> GenerateSoundAsync(string text, double? seconds, CancellationToken ct = default)
    {
        var body = new LocalAudioRequest { Text = text, Seconds = seconds };
        using var resp = await _http.PostAsJsonAsync("/generate", body, JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Audio server returned {(int)resp.StatusCode}: {err}");
        }
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}
