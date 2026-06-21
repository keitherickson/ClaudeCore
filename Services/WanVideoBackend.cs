using ClaudeCore.Models.Ltx;
using Microsoft.Extensions.Options;

namespace ClaudeCore.Services;

/// <summary>
/// <see cref="ILtxVideoBackend"/> for Wan 2.2 14B <b>image-to-video</b> on the shared
/// ComfyUI server (the "quality" path). Higher fidelity / more stable motion than the
/// LTX paths, at ~70-100s warm. Reconstructed from ComfyUI's bundled
/// <c>video_wan2_2_14B_i2v.json</c> template (validated headlessly in
/// tools/run_val_wan22_i2v.py): two fp8 experts (high/low-noise) each with a lightx2v
/// 4-step LoRA, ModelSamplingSD3 shift, split sampling, WanImageToVideo conditioning.
///
/// Requires a starting image — Wan here is i2v only; text-to-video stays on the LTX
/// backends. No native audio (pair with MMAudio downstream if needed).
/// </summary>
public sealed class WanVideoBackend : ComfyUiBackendBase
{
    private readonly WanOptions _o;

    public WanVideoBackend(HttpClient http, IOptions<WanOptions> o, ILogger<WanVideoBackend> log)
        : base(http, log)
    {
        _o = o.Value;
    }

    public override string Key => "Wan";
    protected override int GenerationTimeoutMinutes => _o.GenerationTimeoutMinutes;
    protected override string ModelName => _o.HighNoiseModel;

    public override Task<string> GetModelSpecsRawAsync(CancellationToken ct = default)
        => Task.FromResult(
            "{\"local_models\":[{\"id\":\"wan\",\"resolutions\":[\"540p\",\"720p\"]," +
            "\"fps\":[16],\"durations\":[5],\"aspectRatios\":[\"16:9\",\"9:16\"]}],\"api_models\":[]}");

    protected override async Task<Dictionary<string, object>> BuildGraphAsync(GenerateVideoRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.ImagePath) || !File.Exists(r.ImagePath))
            throw new LtxServerException(400,
                "Wan 2.2 is image-to-video — add a starting image, or switch to an LTX model for text-to-video.");

        var imageName = await UploadImageAsync(r.ImagePath, ct);
        var (width, height) = Dims(r.Resolution, r.AspectRatio);
        var length = FrameCount(r.Duration, _o.Fps);
        return BuildGraph(r, width, height, length, imageName);
    }

    /// <summary>Builds the API-format Wan 2.2 i2v graph (mirrors tools/run_val_wan22_i2v.py).</summary>
    private Dictionary<string, object> BuildGraph(GenerateVideoRequest r, int width, int height, int length, string imageName)
    {
        var seed = Random.Shared.NextInt64(1, 2_000_000_000);
        double fps = _o.Fps <= 0 ? 16.0 : _o.Fps;

        return new Dictionary<string, object>
        {
            ["1"] = Node("CLIPLoader", new { clip_name = _o.TextEncoder, type = "wan", device = "default" }),
            ["2"] = Node("CLIPTextEncode", new { text = r.Prompt, clip = Link("1", 0) }),
            ["3"] = Node("CLIPTextEncode", new { text = r.NegativePrompt ?? "", clip = Link("1", 0) }),
            ["4"] = Node("VAELoader", new { vae_name = _o.Vae }),
            ["5"] = Node("LoadImage", new { image = imageName }),

            // High-noise expert + its lightx2v LoRA + sampling shift.
            ["6"] = Node("UNETLoader", new { unet_name = _o.HighNoiseModel, weight_dtype = "default" }),
            ["7"] = Node("LoraLoaderModelOnly", new { model = Link("6", 0), lora_name = _o.HighNoiseLora, strength_model = 1.0 }),
            ["8"] = Node("ModelSamplingSD3", new { model = Link("7", 0), shift = _o.Shift }),

            // Low-noise expert + its lightx2v LoRA + sampling shift.
            ["9"] = Node("UNETLoader", new { unet_name = _o.LowNoiseModel, weight_dtype = "default" }),
            ["10"] = Node("LoraLoaderModelOnly", new { model = Link("9", 0), lora_name = _o.LowNoiseLora, strength_model = 1.0 }),
            ["11"] = Node("ModelSamplingSD3", new { model = Link("10", 0), shift = _o.Shift }),

            // i2v conditioning: emits (positive, negative, latent) with the start frame baked in.
            ["12"] = Node("WanImageToVideo", new
            {
                positive = Link("2", 0),
                negative = Link("3", 0),
                vae = Link("4", 0),
                width,
                height,
                length,
                batch_size = 1,
                start_image = Link("5", 0),
            }),

            // High-noise expert runs the first half, injecting noise and leaving leftover.
            ["13"] = Node("KSamplerAdvanced", new
            {
                add_noise = "enable",
                noise_seed = seed,
                steps = _o.Steps,
                cfg = _o.Cfg,
                sampler_name = _o.Sampler,
                scheduler = "simple",
                start_at_step = 0,
                end_at_step = _o.SplitStep,
                return_with_leftover_noise = "enable",
                model = Link("8", 0),
                positive = Link("12", 0),
                negative = Link("12", 1),
                latent_image = Link("12", 2),
            }),

            // Low-noise expert finishes the schedule.
            ["14"] = Node("KSamplerAdvanced", new
            {
                add_noise = "disable",
                noise_seed = 0,
                steps = _o.Steps,
                cfg = _o.Cfg,
                sampler_name = _o.Sampler,
                scheduler = "simple",
                start_at_step = _o.SplitStep,
                end_at_step = 10000,
                return_with_leftover_noise = "disable",
                model = Link("11", 0),
                positive = Link("12", 0),
                negative = Link("12", 1),
                latent_image = Link("13", 0),
            }),

            ["15"] = Node("VAEDecode", new { samples = Link("14", 0), vae = Link("4", 0) }),
            ["16"] = Node("SaveWEBM", new { images = Link("15", 0), filename_prefix = "wan22", codec = "vp9", fps, crf = 32.0 }),
        };
    }

    /// <summary>Maps resolution + aspect to Wan-appropriate, /16-aligned width/height.</summary>
    private static (int w, int h) Dims(string? resolution, string? aspect)
    {
        var (lw, lh) = (resolution ?? "720p") switch
        {
            "540p" => (832, 480),
            _ => (1280, 720),   // 720p (and cap 1080p here — 14B i2v at 1080p is very heavy)
        };
        return aspect == "9:16" ? (lh, lw) : (lw, lh);
    }

    /// <summary>Wan latent length must be 4n+1 (temporal stride 4); round duration*fps to it.</summary>
    private static int FrameCount(int duration, int fps)
    {
        var frames = Math.Max(1, (duration <= 0 ? 5 : duration) * (fps <= 0 ? 16 : fps));
        var n = (int)Math.Round((frames - 1) / 4.0);
        return Math.Max(5, 4 * n + 1);
    }
}
