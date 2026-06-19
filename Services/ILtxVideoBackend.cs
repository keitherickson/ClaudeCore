using ClaudeCore.Models.Ltx;

namespace ClaudeCore.Services;

/// <summary>
/// A video-generation backend the Generate flow can be pointed at. Two
/// implementations exist: <see cref="LtxVideoClient"/> (the local LTX Desktop
/// REST server — BF16 2.3, full features incl. audio) and
/// <see cref="ComfyUiVideoBackend"/> (a ComfyUI server running the NVFP4 2.3
/// model for speed). The active one is chosen at runtime via
/// <see cref="ActiveModelStore"/> and the <c>VideoModels</c> registry; the
/// orchestration in <see cref="LtxVideoService"/> stays backend-agnostic.
/// </summary>
public interface ILtxVideoBackend
{
    /// <summary>
    /// Stable key matching a model entry's <c>Backend</c> in the VideoModels
    /// registry (e.g. "LtxDesktop", "ComfyUI"). Routes a selected model to its
    /// implementation.
    /// </summary>
    string Key { get; }

    /// <summary>
    /// Submits a generation and returns only when the video is finished, with a
    /// server-readable path to the result file (which the caller copies to the
    /// output dir). Synchronous from the caller's perspective.
    /// </summary>
    Task<GenerateVideoResponse> GenerateAsync(GenerateVideoRequest request, CancellationToken ct = default);

    /// <summary>Live progress for the page's poll loop (null/idle when nothing is running).</summary>
    Task<GenerationProgress?> GetProgressAsync(CancellationToken ct = default);

    /// <summary>Connectivity + model-status probe.</summary>
    Task<LtxHealth?> GetHealthAsync(CancellationToken ct = default);

    /// <summary>Raw model capability matrix JSON (resolution/fps/duration) for the form.</summary>
    Task<string> GetModelSpecsRawAsync(CancellationToken ct = default);
}
