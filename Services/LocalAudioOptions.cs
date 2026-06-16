namespace ClaudeCore.Services;

/// <summary>
/// Configuration for the self-hosted Stable Audio Open server (tools/audio_server.py,
/// launched by tools/run-audio-server.ps1). ClaudeCore calls it over HTTP on
/// localhost — no API key and no per-call cost, since it runs on the local GPU.
/// </summary>
public sealed class LocalAudioOptions
{
    public const string SectionName = "LocalAudio";

    /// <summary>Base URL of the local audio server. Must match the launcher's -Port (8770).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8770";

    /// <summary>Upper bound on requested duration (the server clamps too).</summary>
    public double MaxDurationSeconds { get; set; } = 30;

    /// <summary>Minutes to allow a single generation (diffusion can take a while).</summary>
    public int TimeoutMinutes { get; set; } = 5;
}
