namespace ClaudeCore.Services;

/// <summary>
/// On startup, reconciles the running backends with the persisted active model so the
/// choice survives reboots. The logon launcher (start-keithvision.ps1) always brings
/// up the BF16 LTX server (the default); if the persisted selection is actually NVFP4,
/// this starts ComfyUI and frees the co-resident LTX server — and vice versa. Runs once,
/// best-effort, in the background so it never blocks app start or crashes boot.
/// </summary>
public sealed class VideoBackendReconciler : BackgroundService
{
    private readonly ActiveModelStore _active;
    private readonly VideoBackendCoordinator _coordinator;
    private readonly ILogger<VideoBackendReconciler> _log;

    public VideoBackendReconciler(
        ActiveModelStore active, VideoBackendCoordinator coordinator, ILogger<VideoBackendReconciler> log)
    {
        _active = active;
        _coordinator = coordinator;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Give the logon launcher a moment to bind the default backend's port first,
            // so a no-op (active == default) doesn't race it into a needless restart.
            await Task.Delay(TimeSpan.FromSeconds(8), stoppingToken);

            var id = _active.ActiveId;
            var result = await _coordinator.ActivateAsync(id, stoppingToken);
            if (!result.Ok)
                _log.LogWarning("Startup reconcile of model '{Id}' failed: {Error}", id, result.Error);
            else
                _log.LogInformation("Startup reconcile: model '{Id}' → backend {Backend} (wasUp={WasUp}, stopped=[{Stopped}])",
                    id, result.Backend, result.WasAlreadyUp, string.Join(", ", result.StoppedCoResident));
        }
        catch (OperationCanceledException) { /* app shutting down */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Startup backend reconcile threw; leaving backends as-is.");
        }
    }
}
