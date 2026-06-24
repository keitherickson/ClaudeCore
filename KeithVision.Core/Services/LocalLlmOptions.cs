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
    /// launcher as -Gpu (the launcher prefers -GpuName "RTX 4090" and only falls back to
    /// this). Defaults to 1 so the LLM shares the 4090 with the audio model.
    /// </summary>
    public int GpuIndex { get; set; } = 1;

    /// <summary>Max new tokens to generate per enhancement (enhanced prompts stay short).</summary>
    public int MaxTokens { get; set; } = 220;

    /// <summary>Minutes to allow a single enhancement (first call can include model load).</summary>
    public int TimeoutMinutes { get; set; } = 3;

    /// <summary>PowerShell launcher that starts the prompt server (Admin "Start" / auto-start).</summary>
    public string StartScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\run-prompt-server.ps1";

    /// <summary>PowerShell script that stops the prompt server (Admin "Stop" button).</summary>
    public string StopScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\stop-prompt-server.ps1";
}
