using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace KeithVision.Services;

/// <summary>
/// Typed HttpClient over rvc-python's built-in API server. Endpoints used:
///   GET  /models                      -> { "models": ["name", ...] }
///   POST /models/{name}               -> load that target voice (server-side state)
///   POST /params { "params": {...} }  -> set f0up_key / f0method / index_rate / ...
///   POST /convert_file  (multipart "file": wav) -> converted WAV bytes
/// The server has no /health, so GET /models doubles as the readiness probe.
/// </summary>
public sealed class LocalRvcClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public LocalRvcClient(HttpClient http) => _http = http;

    /// <summary>Lists the available target-voice model names (also serves as a readiness check).</summary>
    public async Task<IReadOnlyList<string>> GetModelsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/models", ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
            return arr.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
        return Array.Empty<string>();
    }

    /// <summary>Loads a target-voice model on the server (subsequent conversions use it).</summary>
    public async Task LoadModelAsync(string name, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsync($"/models/{Uri.EscapeDataString(name)}", content: null, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"RVC load model '{name}' failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>Sets conversion parameters (transpose in semitones via f0up_key, f0 method, etc.).</summary>
    public async Task SetParamsAsync(int f0UpKey, string f0Method, CancellationToken ct = default)
    {
        var body = new { @params = new { f0up_key = f0UpKey, f0method = f0Method } };
        using var resp = await _http.PostAsJsonAsync("/params", body, JsonOpts, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"RVC set params failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>Converts a WAV file (bytes) with the currently loaded model; returns the converted WAV bytes.</summary>
    public async Task<byte[]> ConvertFileAsync(byte[] wavBytes, CancellationToken ct = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(wavBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(file, "file", "input.wav");

        using var resp = await _http.PostAsync("/convert_file", form, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"RVC convert failed ({(int)resp.StatusCode}): {await resp.Content.ReadAsStringAsync(ct)}");
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }
}
