namespace KeithVision.Services;

/// <summary>
/// Configuration for upscaling via the NVIDIA Maxine Video Effects SDK.
/// KeithVision shells out to the SDK's VideoEffectsApp.exe per job.
///
/// The model + runtime DLLs come from the Maxine "Video Effects SDK"
/// redistributable installer (separate, EULA-gated download). After installing
/// it, set <see cref="ModelDir"/> and <see cref="SdkBinDir"/> to match.
/// </summary>
public sealed class MaxineUpscaleOptions
{
    public const string SectionName = "Maxine";

    /// <summary>Path to VideoEffectsApp.exe (prebuilt sample shipped in the SDK repo).</summary>
    public string ExecutablePath { get; set; } = @"C:\ClaudeCore\maxine-vfx\samples\VideoEffectsApp\VideoEffectsApp.exe";

    /// <summary>Directory containing the SDK AI models (installed by the redistributable). Required for SuperRes/ArtifactReduction.</summary>
    public string ModelDir { get; set; } = @"C:\Program Files\NVIDIA Corporation\NVIDIA Video Effects\models";

    /// <summary>Directory with the SDK runtime DLLs (NVVideoEffects.dll, etc.); added to PATH for the process.</summary>
    public string SdkBinDir { get; set; } = @"C:\Program Files\NVIDIA Corporation\NVIDIA Video Effects";

    /// <summary>Directory with the bundled OpenCV DLL the sample needs on PATH (from the SDK repo).</summary>
    public string OpenCvBinDir { get; set; } = @"C:\ClaudeCore\maxine-vfx\samples\external\opencv\bin";

    /// <summary>Where finished upscaled videos are written.</summary>
    public string OutputDirectory { get; set; } = @"C:\Users\keith\Videos\LTX-Generated\upscaled";

    /// <summary>Where uploaded source videos are staged for the exe to read.</summary>
    public string InputDirectory { get; set; } = @"C:\Users\keith\Videos\LTX-Generated\_upscale_inputs";

    /// <summary>Output video FourCC codec (avc1 = H.264, browser-friendly). Empty = app default.</summary>
    public string Codec { get; set; } = "avc1";

    /// <summary>
    /// GPU model name (substring, e.g. "RTX 5090") to pin upscaling to — resolved to its
    /// CUDA index by name (slot-order-proof) and exported as CUDA_VISIBLE_DEVICES for the
    /// VideoEffectsApp child process. Empty = let the SDK pick its default device.
    /// </summary>
    public string GpuName { get; set; } = "RTX 5090";

    /// <summary>Max minutes to wait for a single upscale job.</summary>
    public int TimeoutMinutes { get; set; } = 60;
}
