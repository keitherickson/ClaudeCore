using System.Net.Http.Json;
using System.Text.Json;
using ClaudeCore.Models.Ltx;

namespace ClaudeCore.Services;

/// <summary>Typed HttpClient over the local LTX-2 inference server's REST API.</summary>
public sealed class LtxVideoClient
{
    private readonly HttpClient _http;

    // Explicit [JsonPropertyName] attributes on the DTOs handle naming, so the
    // default web options (case-insensitive) are all we need here.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public LtxVideoClient(HttpClient http) => _http = http;

    public async Task<LtxHealth?> GetHealthAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/health", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<LtxHealth>(JsonOpts, ct);
    }

    /// <summary>Returns the raw /health JSON (gpu_info, models_status, etc.) for the admin dashboard.</summary>
    public async Task<string> GetHealthRawAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/health", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Requests cancellation of the in-progress generation. Returns the raw status JSON (cancelling | no_active_generation).</summary>
    public async Task<string> CancelAsync(CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync("/api/generate/cancel", content: null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    public async Task<GenerationProgress?> GetProgressAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/api/generation/progress", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<GenerationProgress>(JsonOpts, ct);
    }

    /// <summary>
    /// Returns the model capability matrix (resolution → fps → durations) as raw
    /// JSON, passed straight through to the browser so the form can adapt.
    /// </summary>
    public async Task<string> GetModelSpecsRawAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/api/generate/models-specs", ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Submits a generation. NOTE: this call is synchronous on the server — it
    /// returns only when the video is fully generated, so the configured
    /// HttpClient timeout must be generous (minutes).
    /// </summary>
    public async Task<GenerateVideoResponse> GenerateAsync(GenerateVideoRequest request, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync("/api/generate", request, JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new LtxServerException((int)resp.StatusCode, body);
        }

        var result = await resp.Content.ReadFromJsonAsync<GenerateVideoResponse>(JsonOpts, ct);
        return result ?? throw new LtxServerException(500, "Empty response from LTX server.");
    }
}

/// <summary>Raised when the LTX server returns a non-success status.</summary>
public sealed class LtxServerException : Exception
{
    public int StatusCode { get; }

    public LtxServerException(int statusCode, string message)
        : base($"LTX server returned {statusCode}: {message}") => StatusCode = statusCode;
}

/// <summary>Raised when a generation was cancelled by the user (the server returned status "cancelled").</summary>
public sealed class LtxGenerationCancelledException : Exception
{
    public LtxGenerationCancelledException() : base("Generation was cancelled.") { }
}
