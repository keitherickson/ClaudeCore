namespace KeithVision.Services;

/// <summary>Default selections for the Generate Video form ("VideoDefaults" config section).</summary>
public sealed class VideoDefaultsOptions
{
    public const string SectionName = "VideoDefaults";

    public string Resolution { get; set; } = "540p";
    public int Duration { get; set; } = 20;
    public int Fps { get; set; } = 24;
    public string AspectRatio { get; set; } = "16:9";
    public string CameraMotion { get; set; } = "focus_shift";
    public bool Audio { get; set; } = true;
}
