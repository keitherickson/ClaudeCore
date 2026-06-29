namespace KeithVision.Services;

/// <summary>
/// Configuration for the self-hosted RVC (Retrieval-based Voice Conversion) server.
/// We run rvc-python's BUILT-IN API server (`python -m rvc_python api`), launched by
/// tools/run-rvc-server.ps1, and call it over HTTP on localhost — no API key, runs on
/// the local GPU. Mirrors <see cref="LocalMusicOptions"/>. Target-voice models
/// (.pth/.index) live under <see cref="ModelsDir"/>; the server exposes them via /models.
/// </summary>
public sealed class LocalRvcOptions
{
    public const string SectionName = "LocalRvc";

    /// <summary>Base URL of the local RVC server. Must match the launcher's -Port (8773).</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8773";

    /// <summary>Physical GPU index (CUDA ordinal) the server is pinned to (exported as CUDA_VISIBLE_DEVICES).</summary>
    public int GpuIndex { get; set; } = 1;

    /// <summary>Default pitch transpose (semitones) when the request doesn't specify one. ±12 for cross-gender.</summary>
    public int DefaultTranspose { get; set; }

    /// <summary>F0 (pitch) extraction method: rmvpe (best), crepe, harvest, pm.</summary>
    public string F0Method { get; set; } = "rmvpe";

    /// <summary>Minutes to allow a single conversion.</summary>
    public int TimeoutMinutes { get; set; } = 5;

    /// <summary>Directory of RVC target-voice models (.pth + optional .index), passed to the launcher as -ModelsDir.</summary>
    public string ModelsDir { get; set; } = @"C:\ClaudeCore\rvc-models";

    /// <summary>PowerShell launcher that starts the RVC server (Admin "Start" button).</summary>
    public string StartScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\run-rvc-server.ps1";

    /// <summary>PowerShell script that stops the RVC server (Admin "Stop" button).</summary>
    public string StopScriptPath { get; set; } = @"C:\ClaudeCore\ClaudeCore\tools\stop-rvc-server.ps1";
}
