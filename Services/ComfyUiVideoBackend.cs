using System.Net.Http.Json;
using System.Text.Json;
using ClaudeCore.Models.Ltx;
using Microsoft.Extensions.Options;

namespace ClaudeCore.Services;

/// <summary>
/// <see cref="ILtxVideoBackend"/> over a ComfyUI server running the NVFP4 LTX-2.3
/// model (the "fast" path). Builds the validated text-to-video graph
/// (UNETLoader FP4 transformer + distilled LoRA + CheckpointLoaderSimple-for-VAE +
/// LTXAVTextEncoderLoader gemma), submits it to <c>/prompt</c>, polls
/// <c>/history</c> until done, then downloads the rendered clip via <c>/view</c>.
///
/// First cut: text-to-video only (no image/audio conditioning) — the BF16 default
/// backend covers image-to-video, audio, and extend.
/// </summary>
public sealed class ComfyUiVideoBackend : ILtxVideoBackend
{
    private readonly HttpClient _http;
    private readonly ComfyUiOptions _o;
    private readonly ILogger<ComfyUiVideoBackend> _log;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string ClientId = "claudecore";

    public ComfyUiVideoBackend(HttpClient http, IOptions<ComfyUiOptions> o, ILogger<ComfyUiVideoBackend> log)
    {
        _http = http;
        _o = o.Value;
        _log = log;
    }

    public string Key => "ComfyUI";

    public async Task<GenerateVideoResponse> GenerateAsync(GenerateVideoRequest request, CancellationToken ct = default)
    {
        var (width, height) = Dims(request.Resolution, request.AspectRatio);
        var length = FrameCount(request.Duration, request.Fps);
        var graph = BuildGraph(request, width, height, length);

        // Submit.
        using var submit = await _http.PostAsJsonAsync("/prompt",
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

        _log.LogInformation("ComfyUI generation {Id}: {W}x{H}, {Frames} frames, {Steps} steps",
            promptId, width, height, length, _o.Steps);

        // Poll /history until this prompt appears (it's added on completion).
        var deadline = DateTime.UtcNow.AddMinutes(_o.GenerationTimeoutMinutes);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new LtxServerException(504, "ComfyUI generation timed out.");

            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            using var hist = await _http.GetAsync($"/history/{promptId}", ct);
            if (!hist.IsSuccessStatusCode) continue;
            var histJson = await hist.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (!histJson.TryGetProperty(promptId, out var entry)) continue;

            var statusStr = entry.TryGetProperty("status", out var st)
                && st.TryGetProperty("status_str", out var ss) ? ss.GetString() : null;
            if (statusStr == "error")
                throw new LtxServerException(500, $"ComfyUI execution error: {ExtractError(entry)}");

            // Find the SaveWEBM output (filename/subfolder/type).
            var outFile = FindOutput(entry);
            if (outFile is null)
            {
                // status present but no output yet — keep waiting unless it errored.
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
            using var resp = await _http.GetAsync("/prompt", ct);
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
        using var resp = await _http.GetAsync("/system_stats", ct);
        resp.EnsureSuccessStatusCode();
        return new LtxHealth { Status = "ok", ModelsLoaded = true, ActiveModel = _o.Checkpoint };
    }

    public Task<string> GetModelSpecsRawAsync(CancellationToken ct = default)
        // ComfyUI has no specs endpoint; serve the resolution/fps/duration matrix the form needs.
        => Task.FromResult(
            "{\"local_models\":[{\"id\":\"fast\",\"resolutions\":[\"540p\",\"720p\",\"1080p\"]," +
            "\"fps\":[24,25],\"durations\":[5,6,8,10],\"aspectRatios\":[\"16:9\",\"9:16\"]}],\"api_models\":[]}");

    // --- graph -------------------------------------------------------------

    /// <summary>Builds the API-format graph (mirrors tools/run_val_nvfp4_23b.py).</summary>
    private Dictionary<string, object> BuildGraph(GenerateVideoRequest r, int width, int height, int length)
    {
        static object[] Link(string node, int slot) => new object[] { node, slot };
        var seed = Random.Shared.NextInt64(1, 2_000_000_000);
        var fps = r.Fps <= 0 ? 24.0 : r.Fps;

        return new Dictionary<string, object>
        {
            ["1"] = Node("UNETLoader", new { unet_name = _o.Checkpoint, weight_dtype = "default" }),
            ["15"] = Node("LoraLoaderModelOnly", new { model = Link("1", 0), lora_name = _o.DistilledLora, strength_model = 1.0 }),
            ["14"] = Node("CheckpointLoaderSimple", new { ckpt_name = _o.Checkpoint }), // VAE only (slot 2)
            ["2"] = Node("LTXAVTextEncoderLoader", new { text_encoder = _o.TextEncoder, ckpt_name = _o.Checkpoint, device = "default" }),
            ["3"] = Node("CLIPTextEncode", new { text = r.Prompt, clip = Link("2", 0) }),
            ["4"] = Node("CLIPTextEncode", new { text = r.NegativePrompt ?? "", clip = Link("2", 0) }),
            ["5"] = Node("LTXVConditioning", new { positive = Link("3", 0), negative = Link("4", 0), frame_rate = fps }),
            ["6"] = Node("EmptyLTXVLatentVideo", new { width, height, length, batch_size = 1 }),
            ["7"] = Node("RandomNoise", new { noise_seed = seed }),
            ["8"] = Node("KSamplerSelect", new { sampler_name = _o.Sampler }),
            ["9"] = Node("LTXVScheduler", new { steps = _o.Steps, max_shift = 2.05, base_shift = 0.95, stretch = true, terminal = 0.1, latent = Link("6", 0) }),
            ["10"] = Node("CFGGuider", new { model = Link("15", 0), positive = Link("5", 0), negative = Link("5", 1), cfg = 1.0 }),
            ["11"] = Node("SamplerCustomAdvanced", new { noise = Link("7", 0), guider = Link("10", 0), sampler = Link("8", 0), sigmas = Link("9", 0), latent_image = Link("6", 0) }),
            ["12"] = Node("LTXVTiledVAEDecode", new { vae = Link("14", 2), latents = Link("11", 0), horizontal_tiles = 1, vertical_tiles = 1, overlap = 1, last_frame_fix = false }),
            ["13"] = Node("SaveWEBM", new { images = Link("12", 0), filename_prefix = "claudecore", codec = "vp9", fps, crf = 32.0 }),
        };
    }

    private static Dictionary<string, object> Node(string classType, object inputs)
        => new() { ["class_type"] = classType, ["inputs"] = inputs };

    /// <summary>Maps resolution + aspect to /32-aligned width/height.</summary>
    private static (int w, int h) Dims(string? resolution, string? aspect)
    {
        var (lw, lh) = (resolution ?? "720p") switch
        {
            "540p" => (960, 544),
            "1080p" => (1920, 1088),
            _ => (1280, 704),
        };
        return aspect == "9:16" ? (lh, lw) : (lw, lh);
    }

    /// <summary>LTX latent length must be 8n+1; round duration*fps to the nearest valid count.</summary>
    private static int FrameCount(int duration, int fps)
    {
        var frames = Math.Max(1, (duration <= 0 ? 5 : duration) * (fps <= 0 ? 24 : fps));
        var n = (int)Math.Round((frames - 1) / 8.0);
        return Math.Max(9, 8 * n + 1);
    }

    // --- output retrieval --------------------------------------------------

    private readonly record struct OutputRef(string Filename, string Subfolder, string Type);

    private static OutputRef? FindOutput(JsonElement entry)
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

    /// <summary>
    /// Downloads the rendered clip to a temp file. ComfyUI emits VP9/WebM; we save it
    /// with a .mp4 name (ffmpeg sniffs content, not extension) so the caller's H.264
    /// normalization turns it into a real, browser-friendly MP4.
    /// </summary>
    private async Task<string> DownloadAsync(OutputRef o, CancellationToken ct)
    {
        var url = $"/view?filename={Uri.EscapeDataString(o.Filename)}&subfolder={Uri.EscapeDataString(o.Subfolder)}&type={Uri.EscapeDataString(o.Type)}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        var dest = Path.Combine(Path.GetTempPath(), $"comfyui_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(dest, bytes, ct);
        return dest;
    }

    private static string ExtractError(JsonElement entry)
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
