namespace KeithVision.Services;

/// <summary>
/// Configuration for the self-hosted prompt-enhancer LLM server (tools/prompt_server.py,
/// launched by tools/run-prompt-server.ps1). KeithVision/KeithUI call it over HTTP on
/// localhost — no API key and no per-call cost, since it runs on the local GPU (the 4090,
/// alongside the audio server, leaving the 5090 for video).
/// </summary>
public sealed class LocalLlmOptions
{
    public const string SectionName = "LocalLlm";

    /// <summary>Base URL of the local prompt server. Must match the launcher's -Port (8771).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8771";

    /// <summary>
    /// Physical GPU index (CUDA ordinal) the prompt server is pinned to, passed to the
    /// launcher as -Gpu. Defaults to 1 so the LLM shares the 4090 with the audio model.
    /// Also the co-residency key for <see cref="PromptVramCoordinator"/>: the prompt LLM
    /// only yields VRAM before a BF16 generation when this equals <see cref="LtxVideoOptions.GpuIndex"/>.
    /// The "run on 4090" profile moves this to 0 (the 5090, beside the game) so LTX gets
    /// the whole 4090 — then the two are on different cards and no yield is needed.
    /// </summary>
    public int GpuIndex { get; set; } = 1;

    /// <summary>
    /// GPU model substring (e.g. "RTX 4090") passed to run-prompt-server.ps1 as -GpuName,
    /// which it prefers over -Gpu and resolves to a CUDA index by name (slot-order-proof).
    /// MUST agree with <see cref="GpuIndex"/> — the index drives the co-residency logic
    /// while the name does the physical pin. Empty falls back to the numeric index.
    /// Ignored when <see cref="Device"/> is "cpu".
    /// </summary>
    public string GpuName { get; set; } = "RTX 4090";

    /// <summary>
    /// Compute device for the prompt LLM: "auto" (GPU if visible, else CPU — default),
    /// "cuda", or "cpu". The game-on-4090 profile sets "cpu" so the enhancer runs in
    /// system RAM and takes no GPU VRAM. Passed to run-prompt-server.ps1 as -Device; when
    /// "cpu" the prompt LLM is never co-resident with the video model (see
    /// <see cref="PromptVramCoordinator"/>).
    /// </summary>
    public string Device { get; set; } = "auto";

    /// <summary>Max new tokens to generate per enhancement (enhanced prompts stay short).</summary>
    public int MaxTokens { get; set; } = 220;

    /// <summary>Minutes to allow a single enhancement (first call can include model load).</summary>
    public int TimeoutMinutes { get; set; } = 3;

    /// <summary>PowerShell launcher that starts the prompt server (Admin "Start" / auto-start).</summary>
    public string StartScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\run-prompt-server.ps1";

    /// <summary>PowerShell script that stops the prompt server (Admin "Stop" button).</summary>
    public string StopScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\stop-prompt-server.ps1";
}
