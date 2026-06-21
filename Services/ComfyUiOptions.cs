namespace KeithVision.Services;

/// <summary>
/// Configuration for the ComfyUI NVFP4 backend ("ComfyUI" config section). Mirrors
/// the validated text-to-video graph (UNETLoader FP4 transformer + distilled LoRA +
/// CheckpointLoaderSimple-for-VAE + LTXAVTextEncoderLoader gemma). The model files
/// live under the ComfyUI install's models/ tree.
/// </summary>
public sealed class ComfyUiOptions
{
    public const string SectionName = "ComfyUI";

    /// <summary>ComfyUI server base URL.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8188";

    /// <summary>
    /// Physical GPU index (CUDA device ordinal) ComfyUI is pinned to, passed to
    /// run-comfyui.ps1 as -Gpu and exported as CUDA_VISIBLE_DEVICES. MUST resolve to
    /// the Blackwell (5090) card — NVFP4 has no native path on the Ada (4090). When
    /// this equals another video backend's GpuIndex the model switch treats them as
    /// co-resident and frees one before starting the other (see VideoBackendCoordinator).
    /// </summary>
    public int GpuIndex { get; set; } = 0;

    /// <summary>PowerShell launcher that starts ComfyUI (model switch → NVFP4).</summary>
    public string StartScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\run-comfyui.ps1";

    /// <summary>PowerShell script that stops ComfyUI (frees its VRAM when leaving NVFP4).</summary>
    public string StopScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\stop-comfyui.ps1";

    /// <summary>NVFP4 transformer + VAE checkpoint (in models/checkpoints).</summary>
    public string Checkpoint { get; set; } = "ltx-2.3-22b-dev-nvfp4.safetensors";

    /// <summary>Distilled LoRA applied for 8-step generation (in models/loras).</summary>
    public string DistilledLora { get; set; } = "ltx-2.3-22b-distilled-lora-384-1.1.safetensors";

    /// <summary>fp8 Gemma text encoder for the 2.3 AV path (in models/text_encoders).</summary>
    public string TextEncoder { get; set; } = "gemma_3_12B_it_fp8_scaled.safetensors";

    /// <summary>Sampling steps (distilled LoRA → few steps).</summary>
    public int Steps { get; set; } = 8;

    /// <summary>Sampler name (ComfyUI KSamplerSelect).</summary>
    public string Sampler { get; set; } = "euler";

    /// <summary>Max minutes to wait for a single generation.</summary>
    public int GenerationTimeoutMinutes { get; set; } = 15;
}
