namespace KeithVision.Services;

/// <summary>
/// Configuration for the self-hosted Stable Audio Open server (tools/audio_server.py,
/// launched by tools/run-audio-server.ps1). KeithVision calls it over HTTP on
/// localhost — no API key and no per-call cost, since it runs on the local GPU.
/// </summary>
public sealed class LocalAudioOptions
{
    public const string SectionName = "LocalAudio";

    /// <summary>Base URL of the local audio server. Must match the launcher's -Port (8770).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8770";

    /// <summary>
    /// Physical GPU index (CUDA device ordinal) the audio server is pinned to,
    /// passed to the launcher as -Gpu and exported as CUDA_VISIBLE_DEVICES.
    /// Defaults to 1 so the audio model loads on a different GPU than the LTX
    /// video model (which defaults to 0).
    /// </summary>
    public int GpuIndex { get; set; } = 1;

    /// <summary>Upper bound on requested duration (the server clamps too).</summary>
    public double MaxDurationSeconds { get; set; } = 30;

    /// <summary>Minutes to allow a single generation (diffusion can take a while).</summary>
    public int TimeoutMinutes { get; set; } = 5;

    /// <summary>PowerShell launcher that starts the audio server (Admin "Start" button).</summary>
    public string StartScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\run-audio-server.ps1";

    /// <summary>PowerShell script that stops the audio server (Admin "Stop" button).</summary>
    public string StopScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\stop-audio-server.ps1";
}
