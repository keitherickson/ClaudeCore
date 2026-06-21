using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using KeithVision.Models.Ltx;

namespace KeithVision.Services;

/// <summary>
/// Shared plumbing for any <see cref="ILtxVideoBackend"/> that drives a ComfyUI
/// server: submit an API-format graph to <c>/prompt</c>, poll <c>/history</c> to
/// completion, and download the rendered clip via <c>/view</c> (saved with an
/// <c>.mp4</c> name so the caller's H.264 normalization turns the VP9/WebM into a
/// browser-friendly MP4). Subclasses supply only the model-specific bits: the
/// graph, the timeout, a display name, and (for i2v) any input upload.
/// </summary>
public abstract class ComfyUiBackendBase : ILtxVideoBackend
{
    protected readonly HttpClient Http;
    private readonly ILogger _log;
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    protected const string ClientId = "claudecore";

    protected ComfyUiBackendBase(HttpClient http, ILogger log)
    {
        Http = http;
        _log = log;
    }

    public abstract string Key { get; }
    public abstract Task<string> GetModelSpecsRawAsync(CancellationToken ct = default);

    /// <summary>Minutes to allow a single generation (polled to completion).</summary>
    protected abstract int GenerationTimeoutMinutes { get; }

    /// <summary>Human label reported in the health snapshot.</summary>
    protected abstract string ModelName { get; }

    /// <summary>Builds the API-format graph for this request (may upload inputs to ComfyUI first).</summary>
    protected abstract Task<Dictionary<string, object>> BuildGraphAsync(GenerateVideoRequest request, CancellationToken ct);

    public async Task<GenerateVideoResponse> GenerateAsync(GenerateVideoRequest request, CancellationToken ct = default)
    {
        var graph = await BuildGraphAsync(request, ct);

        using var submit = await Http.PostAsJsonAsync("/prompt",
            new { prompt = graph, client_id = ClientId }, Json, ct);
        if (!submit.IsSuccessStatusCode)
        {
            var body = await submit.Content.ReadAsStringAsync(ct);
            throw new LtxServerException((int)submit.StatusCode, $"ComfyUI rejected the graph: {body}");
        }
        var submitJson = await submit.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var promptId = submitJson.TryGetProperty("prompt_id", out var pid) ? pid.GetString() : null;
        if (string.IsNullOrEmpty(promptId))
            throw new LtxServerException(500, "ComfyUI did not return a prompt_id.");

        _log.LogInformation("ComfyUI[{Key}] generation {Id} submitted", Key, promptId);

        var deadline = DateTime.UtcNow.AddMinutes(GenerationTimeoutMinutes);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new LtxServerException(504, "ComfyUI generation timed out.");

            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            using var hist = await Http.GetAsync($"/history/{promptId}", ct);
            if (!hist.IsSuccessStatusCode) continue;
            var histJson = await hist.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (!histJson.TryGetProperty(promptId, out var entry)) continue;

            var statusStr = entry.TryGetProperty("status", out var st)
                && st.TryGetProperty("status_str", out var ss) ? ss.GetString() : null;
            if (statusStr == "error")
                throw new LtxServerException(500, $"ComfyUI execution error: {ExtractError(entry)}");

            var outFile = FindOutput(entry);
            if (outFile is null)
            {
                if (statusStr == "success") throw new LtxServerException(500, "ComfyUI finished with no output file.");
                continue;
            }

            var localPath = await DownloadAsync(outFile.Value, ct);
            return new GenerateVideoResponse { Status = "complete", VideoPath = localPath };
        }
    }

    public async Task<GenerationProgress?> GetProgressAsync(CancellationToken ct = default)
    {
        // Stateless: ComfyUI's queue tells us whether something is running. The page
        // overlays its own client-side step estimate (the engine has no per-step hook).
        try
        {
            using var resp = await Http.GetAsync("/prompt", ct);
            if (!resp.IsSuccessStatusCode) return new GenerationProgress { Status = "idle" };
            var j = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            var remaining = j.TryGetProperty("exec_info", out var ei)
                && ei.TryGetProperty("queue_remaining", out var qr) ? qr.GetInt32() : 0;
            return new GenerationProgress
            {
                Status = remaining > 0 ? "running" : "idle",
                Phase = remaining > 0 ? "generating" : "",
                Progress = remaining > 0 ? 50 : 0,
            };
        }
        catch
        {
            return new GenerationProgress { Status = "idle" };
        }
    }

    public async Task<LtxHealth?> GetHealthAsync(CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync("/system_stats", ct);
        resp.EnsureSuccessStatusCode();
        return new LtxHealth { Status = "ok", ModelsLoaded = true, ActiveModel = ModelName };
    }

    // --- helpers shared by all ComfyUI graphs ------------------------------

    protected static Dictionary<string, object> Node(string classType, object inputs)
        => new() { ["class_type"] = classType, ["inputs"] = inputs };

    protected static object[] Link(string node, int slot) => new object[] { node, slot };

    /// <summary>
    /// Uploads a local image to ComfyUI's input store via <c>/upload/image</c> and
    /// returns the name a LoadImage node should reference (subfolder-qualified). Used
    /// by image-to-video graphs so the conditioning frame doesn't rely on a shared
    /// filesystem path.
    /// </summary>
    protected async Task<string> UploadImageAsync(string localPath, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(localPath, ct);
        using var form = new MultipartFormDataContent();
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "image", Path.GetFileName(localPath));
        form.Add(new StringContent("true"), "overwrite");

        using var resp = await Http.PostAsync("/upload/image", form, ct);
        resp.EnsureSuccessStatusCode();
        var j = await resp.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var name = j.TryGetProperty("name", out var n) ? n.GetString() : Path.GetFileName(localPath);
        var sub = j.TryGetProperty("subfolder", out var s) ? s.GetString() ?? "" : "";
        return string.IsNullOrEmpty(sub) ? name! : $"{sub}/{name}";
    }

    protected readonly record struct OutputRef(string Filename, string Subfolder, string Type);

    protected static OutputRef? FindOutput(JsonElement entry)
    {
        if (!entry.TryGetProperty("outputs", out var outputs)) return null;
        foreach (var node in outputs.EnumerateObject())
        {
            foreach (var arrName in new[] { "images", "gifs", "videos" })
            {
                if (node.Value.TryGetProperty(arrName, out var arr) && arr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in arr.EnumerateArray())
                    {
                        var fn = item.TryGetProperty("filename", out var f) ? f.GetString() : null;
                        if (string.IsNullOrEmpty(fn)) continue;
                        return new OutputRef(
                            fn,
                            item.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "",
                            item.TryGetProperty("type", out var t) ? t.GetString() ?? "output" : "output");
                    }
                }
            }
        }
        return null;
    }

    private async Task<string> DownloadAsync(OutputRef o, CancellationToken ct)
    {
        var url = $"/view?filename={Uri.EscapeDataString(o.Filename)}&subfolder={Uri.EscapeDataString(o.Subfolder)}&type={Uri.EscapeDataString(o.Type)}";
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        var dest = Path.Combine(Path.GetTempPath(), $"comfyui_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(dest, bytes, ct);
        return dest;
    }

    protected static string ExtractError(JsonElement entry)
    {
        try
        {
            if (entry.TryGetProperty("status", out var st) && st.TryGetProperty("messages", out var msgs))
                foreach (var m in msgs.EnumerateArray())
                    if (m.ValueKind == JsonValueKind.Array && m.GetArrayLength() >= 2
                        && m[0].GetString() == "execution_error")
                        return m[1].TryGetProperty("exception_message", out var em) ? em.GetString() ?? "" : "";
        }
        catch { /* fall through */ }
        return "unknown error";
    }
}
