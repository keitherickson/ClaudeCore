using Microsoft.Extensions.Options;

namespace ClaudeCore.Services;

/// <summary>Outcome of activating a model's backend.</summary>
/// <param name="Ok">Whether the activation request was valid and dispatched.</param>
/// <param name="Backend"><see cref="ILtxVideoBackend.Key"/> that now serves the model.</param>
/// <param name="Port">The backend's port — what the UI polls to watch it come online.</param>
/// <param name="WasAlreadyUp">True if the backend was already listening (no start needed).</param>
/// <param name="StoppedCoResident">Backends stopped to free this GPU's VRAM.</param>
/// <param name="Error">Set when <paramref name="Ok"/> is false.</param>
public sealed record BackendSwitchResult(
    bool Ok, string Backend, int Port, bool WasAlreadyUp,
    IReadOnlyList<string> StoppedCoResident, string? Error);

/// <summary>
/// Owns the lifecycle of the video backends behind the /Admin model switch so that
/// selecting a model brings its backend up and frees the GPU it needs. The behavior
/// is derived purely from each backend's configured <c>GpuIndex</c> — no 1-vs-2-GPU
/// mode flag:
///
/// <list type="bullet">
///   <item>Backends sharing the target's GPU are <b>co-resident</b> and get stopped
///   first to free VRAM (single-GPU box, or two video backends both pinned to the
///   5090 — only one fits at a time).</item>
///   <item>Backends on a <b>different</b> GPU are left running (two-GPU box — e.g. the
///   audio server on the 4090 keeps serving while video switches on the 5090).</item>
/// </list>
///
/// On a single GPU every index collapses to 0, so the co-resident rule naturally
/// serializes everything that needs that card. When the second GPU is added it is a
/// config change (move an index), not a code change. NVFP4/ComfyUI must stay on the
/// Blackwell (5090) card — the Ada (4090) has no native FP4 path.
/// </summary>
public sealed class VideoBackendCoordinator
{
    private readonly LtxServerControl _ltx;
    private readonly ComfyUiServerControl _comfy;
    private readonly VideoModelsOptions _models;
    private readonly ILogger<VideoBackendCoordinator> _log;

    public VideoBackendCoordinator(
        LtxServerControl ltx,
        ComfyUiServerControl comfy,
        IOptions<VideoModelsOptions> models,
        ILogger<VideoBackendCoordinator> log)
    {
        _ltx = ltx;
        _comfy = comfy;
        _models = models.Value;
        _log = log;
    }

    /// <summary>A controllable video backend, keyed to match <see cref="ILtxVideoBackend.Key"/>.</summary>
    private sealed record Backend(
        string Key, int GpuIndex, int Port,
        Func<bool> IsUp, Action StartIfDown, Func<CancellationToken, Task<RestartResult>> Stop);

    private IReadOnlyList<Backend> Backends() => new[]
    {
        new Backend("LtxDesktop", _ltx.GpuIndex, _ltx.Port,
            _ltx.IsPortListening, _ltx.RestartDetached, _ltx.StopAsync),
        new Backend("ComfyUI", _comfy.GpuIndex, _comfy.Port,
            _comfy.IsPortListening, () => _comfy.StartDetached(), _comfy.StopAsync),
    };

    /// <summary>
    /// Makes the backend for <paramref name="modelId"/> the live one: stops any
    /// co-resident backend (same GPU) to free VRAM, then starts the target if it
    /// isn't already up. Starting is detached/fire-and-forget — the backend binds its
    /// port over the next ~tens of seconds, so the caller polls status to see it
    /// online (weights still load lazily on the first generation either way).
    /// </summary>
    public async Task<BackendSwitchResult> ActivateAsync(string modelId, CancellationToken ct = default)
    {
        var entry = _models.Models.FirstOrDefault(m => m.Id == modelId);
        if (entry is null)
            return new BackendSwitchResult(false, "", 0, false, Array.Empty<string>(), $"Unknown model id '{modelId}'.");

        var backends = Backends();
        var target = backends.FirstOrDefault(b => b.Key == entry.Backend);
        if (target is null)
            return new BackendSwitchResult(false, entry.Backend, 0, false, Array.Empty<string>(),
                $"No process control for backend '{entry.Backend}'.");

        // 1. Free the GPU: stop every OTHER backend that shares the target's card.
        var stopped = new List<string>();
        foreach (var b in backends)
        {
            if (b.Key == target.Key || b.GpuIndex != target.GpuIndex) continue;
            if (!b.IsUp()) continue;
            _log.LogInformation("Model switch → {Target}: stopping co-resident {Other} on GPU {Gpu} to free VRAM",
                target.Key, b.Key, b.GpuIndex);
            await b.Stop(ct);
            stopped.Add(b.Key);
        }

        // 2. Bring the target up if it isn't already (detached; binds shortly).
        var wasUp = target.IsUp();
        if (!wasUp)
        {
            _log.LogInformation("Model switch → {Target}: starting backend on GPU {Gpu} (port {Port})",
                target.Key, target.GpuIndex, target.Port);
            target.StartIfDown();
        }

        return new BackendSwitchResult(true, target.Key, target.Port, wasUp, stopped, null);
    }
}
