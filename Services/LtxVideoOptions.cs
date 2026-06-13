namespace ClaudeCore.Services;

/// <summary>
/// Configuration for talking to the locally self-hosted LTX-2 inference server
/// (the same engine LTX Desktop ships, launched by tools/run-ltx-server.ps1).
/// </summary>
public sealed class LtxVideoOptions
{
    public const string SectionName = "Ltx";

    /// <summary>Base URL of the local LTX-2 server. Must match LTX_PORT in the launcher.</summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:8765";

    /// <summary>Folder on the local drive where finished videos are copied.</summary>
    public string OutputDirectory { get; set; } = @"C:\Users\keith\Videos\LTX-Generated";

    /// <summary>Folder where uploaded conditioning images are staged for the server to read.</summary>
    public string InputDirectory { get; set; } = @"C:\Users\keith\Videos\LTX-Generated\_inputs";

    /// <summary>Max minutes to wait for a single generation (video gen can take minutes).</summary>
    public int GenerationTimeoutMinutes { get; set; } = 30;
}
