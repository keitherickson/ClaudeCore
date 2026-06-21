namespace KeithVision.Services;

/// <summary>
/// Configuration for the Wan 2.2 14B image-to-video backend ("Wan" config section).
/// Runs on the SAME ComfyUI server as the NVFP4 backend (shares the BaseUrl/port), so
/// the model switch treats them as one process; only the graph differs. Mirrors the
/// validated lightx2v fast path from tools/run_val_wan22_i2v.py (dual high/low-noise
/// fp8 experts + 4-step lightx2v LoRAs, ModelSamplingSD3 shift, euler/simple).
/// </summary>
public sealed class WanOptions
{
    public const string SectionName = "Wan";

    /// <summary>ComfyUI base URL — the same server the NVFP4 backend uses.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    /// <summary>High-noise expert (models/diffusion_models). Runs the first sampling phase.</summary>
    public string HighNoiseModel { get; set; } = "wan2.2_i2v_high_noise_14B_fp8_scaled.safetensors";

    /// <summary>Low-noise expert (models/diffusion_models). Finishes the schedule.</summary>
    public string LowNoiseModel { get; set; } = "wan2.2_i2v_low_noise_14B_fp8_scaled.safetensors";

    /// <summary>lightx2v 4-step LoRA for the high-noise expert (models/loras).</summary>
    public string HighNoiseLora { get; set; } = "wan2.2_i2v_lightx2v_4steps_lora_v1_high_noise.safetensors";

    /// <summary>lightx2v 4-step LoRA for the low-noise expert (models/loras).</summary>
    public string LowNoiseLora { get; set; } = "wan2.2_i2v_lightx2v_4steps_lora_v1_low_noise.safetensors";

    /// <summary>umt5 text encoder (models/text_encoders).</summary>
    public string TextEncoder { get; set; } = "umt5_xxl_fp8_e4m3fn_scaled.safetensors";

    /// <summary>Wan 2.1 VAE (models/vae) — the 14B i2v path uses the 2.1 VAE.</summary>
    public string Vae { get; set; } = "wan_2.1_vae.safetensors";

    /// <summary>Total sampling steps (lightx2v distill → 4).</summary>
    public int Steps { get; set; } = 4;

    /// <summary>Step at which the high-noise expert hands off to the low-noise expert.</summary>
    public int SplitStep { get; set; } = 2;

    /// <summary>CFG (lightx2v is cfg-distilled → 1.0).</summary>
    public double Cfg { get; set; } = 1.0;

    /// <summary>ModelSamplingSD3 shift.</summary>
    public double Shift { get; set; } = 5.0;

    /// <summary>Output frame rate (Wan i2v is trained at 16 fps).</summary>
    public int Fps { get; set; } = 16;

    /// <summary>Sampler name.</summary>
    public string Sampler { get; set; } = "euler";

    /// <summary>Max minutes to wait for a single generation.</summary>
    public int GenerationTimeoutMinutes { get; set; } = 15;
}
