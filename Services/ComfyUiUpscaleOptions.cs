namespace KeithVision.Services;

/// <summary>
/// Configuration for AI video upscaling via ComfyUI ("ComfyUiUpscale" section) — the
/// alternative to NVIDIA Maxine that upscales to an ARBITRARY target resolution (Maxine
/// is locked to integer ratios). Runs on the same ComfyUI server as the video models;
/// the graph upscales each frame with an ESRGAN model then resizes to the exact target.
/// </summary>
public sealed class ComfyUiUpscaleOptions
{
    public const string SectionName = "ComfyUiUpscale";

    /// <summary>ComfyUI base URL — the shared server (also runs NVFP4 + Wan).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    /// <summary>ComfyUI's input directory, where LoadVideo reads staged source clips.</summary>
    public string InputDirectory { get; set; } = @"C:\ComfyUI\ComfyUI\input";

    /// <summary>ESRGAN upscale model (in ComfyUI/models/upscale_models).</summary>
    public string Model { get; set; } = "RealESRGAN_x4plus.pth";

    /// <summary>Resampling used to hit the exact target after the ESRGAN pass.</summary>
    public string UpscaleMethod { get; set; } = "lanczos";

    /// <summary>Where finished upscaled videos are written (shared with Maxine).</summary>
    public string OutputDirectory { get; set; } = @"C:\Users\keith\Videos\LTX-Generated\upscaled";

    /// <summary>Max minutes for one upscale job (AI frame-by-frame upscaling is slow).</summary>
    public int TimeoutMinutes { get; set; } = 45;
}
