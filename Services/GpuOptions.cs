namespace KeithVision.Services;

/// <summary>Configuration for the live GPU footer readout shown on every page.</summary>
public sealed class GpuOptions
{
    public const string SectionName = "Gpu";

    /// <summary>How often (milliseconds) the footer polls /Admin/Gpu for live GPU stats.</summary>
    public int PollIntervalMs { get; set; } = 1000;
}
