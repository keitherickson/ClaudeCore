using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>
/// Frees the prompt-enhancer LLM's VRAM right before a video generation when the two
/// would otherwise fight for the same card. This is what makes the "run on 4090"
/// profile viable: the 22B BF16 model can't share 24 GB with a resident 7B LLM, so we
/// hand the card back to video and let the prompt server reload lazily on its next call.
///
/// Like <see cref="VideoBackendCoordinator"/>, the behavior is derived purely from the
/// configured GPU indices — no mode flag. When LTX (BF16) and the prompt LLM are pinned
/// to different cards (the default 5090/4090 split) this is a no-op, so the normal setup
/// is completely unaffected.
/// </summary>
public sealed class PromptVramCoordinator
{
    /// <summary><see cref="ILtxVideoBackend.Key"/> of the BF16 backend — the only one
    /// that runs on <see cref="LtxVideoOptions.GpuIndex"/>. The ComfyUI backends (NVFP4,
    /// Wan) have their own card, so generating with them never contends with the LLM.</summary>
    private const string Bf16BackendKey = "LtxDesktop";

    private readonly LtxVideoOptions _ltx;
    private readonly LocalLlmOptions _llm;
    private readonly PromptServerControl _promptControl;
    private readonly LocalLlmClient _promptClient;
    private readonly ILogger<PromptVramCoordinator> _log;

    public PromptVramCoordinator(
        IOptions<LtxVideoOptions> ltx,
        IOptions<LocalLlmOptions> llm,
        PromptServerControl promptControl,
        LocalLlmClient promptClient,
        ILogger<PromptVramCoordinator> log)
    {
        _ltx = ltx.Value;
        _llm = llm.Value;
        _promptControl = promptControl;
        _promptClient = promptClient;
        _log = log;
    }

    /// <summary>True when the BF16 video model and the prompt LLM are pinned to the same GPU.</summary>
    public bool PromptSharesVideoGpu => _ltx.GpuIndex == _llm.GpuIndex;

    /// <summary>
    /// If the BF16 backend is about to generate on the same GPU the prompt LLM occupies,
    /// ask the prompt server to drop its model from VRAM first. Best-effort: a missing or
    /// unreachable prompt server is fine (nothing to free), and any failure is logged but
    /// never blocks the generation.
    /// </summary>
    public async Task FreeForVideoAsync(string activeBackendKey, CancellationToken ct = default)
    {
        if (!string.Equals(activeBackendKey, Bf16BackendKey, StringComparison.Ordinal)) return;
        if (!PromptSharesVideoGpu) return;
        if (!_promptControl.IsPortListening()) return;

        try
        {
            _log.LogInformation(
                "BF16 video shares GPU {Gpu} with the prompt LLM — freeing prompt VRAM before generating.",
                _ltx.GpuIndex);
            await _promptClient.UnloadAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Failed to free prompt VRAM before video generation; continuing anyway.");
        }
    }
}
