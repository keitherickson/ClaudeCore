using System.Net.Http.Json;
using System.Text.Json;

namespace KeithVision.Services;

/// <summary>
/// Typed HttpClient over the local prompt-enhancer server (tools/prompt_server.py).
/// POSTs an idea to /enhance and gets back a single rewritten text-to-video prompt.
/// </summary>
public sealed class LocalLlmClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public LocalLlmClient(HttpClient http) => _http = http;

    /// <summary>Raw /health JSON (model/device/loaded) for a readiness check.</summary>
    public async Task<string> GetHealthRawAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/health", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Rewrites <paramref name="text"/> into a vivid prompt; returns the enhanced text.
    /// A non-empty <paramref name="model"/> asks the server to swap to that model id first.
    /// </summary>
    public async Task<string> EnhanceAsync(string text, string? style, string? model, int maxTokens, CancellationToken ct = default)
    {
        var body = new EnhanceRequest(text, style, maxTokens, model);
        using var resp = await _http.PostAsJsonAsync("/enhance", body, JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Prompt server returned {(int)resp.StatusCode}: {err}");
        }
        var doc = await resp.Content.ReadFromJsonAsync<EnhanceResponse>(JsonOpts, ct);
        return doc?.Prompt ?? text;
    }

    private sealed record EnhanceRequest(string Text, string? Style, int MaxTokens, string? Model);
    private sealed record EnhanceResponse(string Prompt);
}
