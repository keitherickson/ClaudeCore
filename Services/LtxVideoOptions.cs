namespace KeithVision.Services;

/// <summary>
/// Configuration for talking to the locally self-hosted LTX-2 inference server
/// (the same engine LTX Desktop ships, launched by tools/run-ltx-server.ps1).
/// </summary>
public sealed class LtxVideoOptions
{
    public const string SectionName = "Ltx";

    /// <summary>Base URL of the local LTX-2 server. Must match LTX_PORT in the launcher.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8765";

    /// <summary>
    /// Physical GPU index (CUDA device ordinal) the LTX video server is pinned to,
    /// passed to the launcher as -Gpu and exported as CUDA_VISIBLE_DEVICES. Keeping
    /// video and audio on separate GPUs lets both models stay resident at once.
    /// </summary>
    public int GpuIndex { get; set; } = 0;

    /// <summary>Folder on the local drive where finished videos are copied.</summary>
    public string OutputDirectory { get; set; } = @"C:\Users\keith\Videos\LTX-Generated";

    /// <summary>Folder where uploaded conditioning images are staged for the server to read.</summary>
    public string InputDirectory { get; set; } = @"C:\Users\keith\Videos\LTX-Generated\_inputs";

    /// <summary>Max minutes to wait for a single generation (video gen can take minutes).</summary>
    public int GenerationTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// PowerShell script the admin page runs to stop+restart the LTX server.
    /// Lives in the source tree's tools/ folder (same place start-keithvision.ps1
    /// references ltx_launch.py from), not the published app folder.
    /// </summary>
    public string RestartScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\restart-ltx-server.ps1";

    /// <summary>
    /// PowerShell script that stops the LTX server WITHOUT relaunching it. Used by the
    /// /Admin model switch to free this GPU's VRAM when moving off the BF16 model so a
    /// co-resident backend (NVFP4/ComfyUI on the same card) can take over.
    /// </summary>
    public string StopScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\stop-ltx-server.ps1";

    /// <summary>
    /// After a cancel-triggered restart, how long (ms) the Generate page keeps
    /// showing "server restarting…" once /health first answers, before flipping
    /// to "server online" — /health responds before the server is fully ready.
    /// </summary>
    public int RestartReadyDelayMs { get; set; } = 10000;
}
