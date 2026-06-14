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

    /// <summary>Where sped-up videos from the standalone /Speed page are written.</summary>
    public string OutputDirectory { get; set; } = @"C:\Users\keith\Videos\LTX-Generated\sped-up";

    /// <summary>Where uploaded source videos for the /Speed page are staged.</summary>
    public string InputDirectory { get; set; } = @"C:\Users\keith\Videos\LTX-Generated\_speed_inputs";

    /// <summary>Max minutes to wait for a single re-time job.</summary>
    public int TimeoutMinutes { get; set; } = 30;
}
