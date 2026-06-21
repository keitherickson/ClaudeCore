namespace KeithVision.Services;

/// <summary>
/// Registry of selectable video models for the /Admin model switch ("VideoModels"
/// config section). Each entry names a backend (matching an
/// <see cref="ILtxVideoBackend.Key"/>) so the active model routes to the right
/// implementation. The Generate flow uses whichever model
/// <see cref="ActiveModelStore"/> currently points at.
/// </summary>
public sealed class VideoModelsOptions
{
    public const string SectionName = "VideoModels";

    /// <summary>Model <see cref="VideoModelEntry.Id"/> selected when none is persisted yet.</summary>
    public string Default { get; set; } = "bf16-2.3";

    /// <summary>
    /// The selectable models. Defined here in code because each entry's
    /// <see cref="VideoModelEntry.Backend"/> is coupled to an
    /// <see cref="ILtxVideoBackend.Key"/>. Do NOT also list these under a
    /// "VideoModels:Models" config array — .NET config binding *appends* to this
    /// default list rather than replacing it, which would duplicate every entry.
    /// </summary>
    public List<VideoModelEntry> Models { get; set; } = new()
    {
        new VideoModelEntry
        {
            Id = "bf16-2.3",
            Label = "LTX-2.3 (BF16)",
            Backend = "LtxDesktop",
            Description = "Full quality + features (audio, extend). Default.",
        },
        new VideoModelEntry
        {
            Id = "nvfp4-2.3",
            Label = "LTX-2.3 (NVFP4, fast)",
            Backend = "ComfyUI",
            Description = "FP4 on the 5090 — faster, text-to-video (no audio/extend yet).",
        },
        new VideoModelEntry
        {
            Id = "wan2.2",
            Label = "Wan 2.2 (quality, i2v)",
            Backend = "Wan",
            Description = "Best stability/quality, image-to-video — needs a starting image. ~70-100s. Shares the ComfyUI server.",
        },
    };
}

public sealed class VideoModelEntry
{
    /// <summary>Stable id persisted as the active selection.</summary>
    public string Id { get; set; } = "";

    /// <summary>Human label shown in the switch.</summary>
    public string Label { get; set; } = "";

    /// <summary>Which <see cref="ILtxVideoBackend.Key"/> handles this model.</summary>
    public string Backend { get; set; } = "";

    /// <summary>Short note shown under the label.</summary>
    public string Description { get; set; } = "";
}
