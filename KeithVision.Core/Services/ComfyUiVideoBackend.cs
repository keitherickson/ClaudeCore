using KeithVision.Models.Ltx;
using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>
/// <see cref="ILtxVideoBackend"/> over a ComfyUI server running the NVFP4 LTX-2.3
/// model (the "fast" path). Builds the validated text-to-video graph
/// (UNETLoader FP4 transformer + distilled LoRA + CheckpointLoaderSimple-for-VAE +
/// LTXAVTextEncoderLoader gemma). Submit/poll/download live in
/// <see cref="ComfyUiBackendBase"/>.
///
/// Text-to-video only (no image/audio conditioning) — the BF16 default backend
/// covers image-to-video, audio, and extend; the Wan backend covers quality i2v.
/// </summary>
public sealed class ComfyUiVideoBackend : ComfyUiBackendBase
{
    private readonly ComfyUiOptions _o;

    public ComfyUiVideoBackend(HttpClient http, IOptions<ComfyUiOptions> o, ILogger<ComfyUiVideoBackend> log)
        : base(http, log)
    {
        _o = o.Value;
    }

    public override string Key => "ComfyUI";
    protected override int GenerationTimeoutMinutes => _o.GenerationTimeoutMinutes;
    protected override string ModelName => _o.Checkpoint;

    public override Task<string> GetModelSpecsRawAsync(CancellationToken ct = default)
        // ComfyUI has no specs endpoint; serve the resolution/fps/duration matrix the form needs.
        => Task.FromResult(
            "{\"local_models\":[{\"id\":\"fast\",\"resolutions\":[\"540p\",\"720p\",\"1080p\"]," +
            "\"fps\":[24,25],\"durations\":[5,6,8,10],\"aspectRatios\":[\"16:9\",\"9:16\"]}],\"api_models\":[]}");

    protected override Task<Dictionary<string, object>> BuildGraphAsync(GenerateVideoRequest request, CancellationToken ct)
    {
        var (width, height) = Dims(request.Resolution, request.AspectRatio);
        var length = FrameCount(request.Duration, request.Fps);
        return Task.FromResult(BuildGraph(request, width, height, length));
    }

    /// <summary>Builds the API-format graph (mirrors tools/run_val_nvfp4_23b.py).</summary>
    private Dictionary<string, object> BuildGraph(GenerateVideoRequest r, int width, int height, int length)
    {
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
}
