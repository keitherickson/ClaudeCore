namespace ClaudeCore.Services;

/// <summary>
/// Configuration for the optional "play faster" step that re-times a clip with
/// ffmpeg after upscaling. ffmpeg is installed separately (winget Gyan.FFmpeg);
/// point <see cref="FfmpegPath"/> at its exe (or just "ffmpeg" if it's on PATH).
/// </summary>
public sealed class VideoSpeedOptions
{
    public const string SectionName = "VideoSpeed";

    /// <summary>Path to ffmpeg.exe. The winget Gyan.FFmpeg install lands under %LOCALAPPDATA%\Microsoft\WinGet\Packages.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Max minutes to wait for a single re-time job.</summary>
    public int TimeoutMinutes { get; set; } = 30;
}
