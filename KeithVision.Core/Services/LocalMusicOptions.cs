namespace KeithVision.Services;

/// <summary>
/// Configuration for the self-hosted MusicGen server (tools/music_server.py,
/// launched by tools/run-music-server.ps1). KeithVision calls it over HTTP on
/// localhost — no API key and no per-call cost, since it runs on the local GPU.
/// Mirrors <see cref="LocalAudioOptions"/> (the Stable Audio sound-effects server).
/// </summary>
public sealed class LocalMusicOptions
{
    public const string SectionName = "LocalMusic";

    /// <summary>Base URL of the local music server. Must match the launcher's -Port (8772).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8772";

    /// <summary>
    /// Physical GPU index (CUDA device ordinal) the music server is pinned to,
    /// passed to the launcher as -Gpu and exported as CUDA_VISIBLE_DEVICES.
    /// Defaults to 1 so the music model loads on a different GPU than the LTX
    /// video model (which defaults to 0).
    /// </summary>
    public int GpuIndex { get; set; } = 1;

    /// <summary>Upper bound on requested duration (the server clamps too; MusicGen is trained on 30s).</summary>
    public double MaxDurationSeconds { get; set; } = 30;

    /// <summary>Minutes to allow a single generation (longer clips take longer to decode).</summary>
    public int TimeoutMinutes { get; set; } = 5;

    /// <summary>PowerShell launcher that starts the music server (Admin "Start" button).</summary>
    public string StartScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\run-music-server.ps1";

    /// <summary>PowerShell script that stops the music server (Admin "Stop" button).</summary>
    public string StopScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\stop-music-server.ps1";
}
