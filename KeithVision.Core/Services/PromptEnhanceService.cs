using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>
/// Rewrites a short idea into a vivid text-to-video prompt via the self-hosted
/// prompt-enhancer server. Auto-starts the server on first use (like the AI-upscale
/// service auto-starts ComfyUI), so the Enhance Prompt node "just works" without the
/// user pre-starting it from the Admin page.
/// </summary>
public sealed class PromptEnhanceService
{
    private readonly LocalLlmClient _client;
    private readonly LocalLlmOptions _options;
    private readonly PromptServerControl _control;
    private readonly ILogger<PromptEnhanceService> _logger;

    public PromptEnhanceService(
        LocalLlmClient client,
        IOptions<LocalLlmOptions> options,
        PromptServerControl control,
        ILogger<PromptEnhanceService> logger)
    {
        _client = client;
        _options = options.Value;
        _control = control;
        _logger = logger;
    }

    /// <summary>
    /// Pings the prompt server's /health. ok=false (with a reason) when the server is
    /// unreachable or the model hasn't finished loading.
    /// </summary>
    public async Task<(bool Ok, string? Error)> GetHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));
            var raw = await _client.GetHealthRawAsync(cts.Token);
            using var doc = JsonDocument.Parse(raw);
            var loaded = doc.RootElement.TryGetProperty("model_loaded", out var ml) && ml.GetBoolean();
            // A deliberate /unload (VRAM yielded to a co-resident video model) is healthy:
            // the server reloads the model on the next EnhanceAsync, which only needs the
            // port up (see EnsureUpAsync), not a pre-loaded model.
            var unloaded = doc.RootElement.TryGetProperty("unloaded", out var ul) && ul.GetBoolean();
            if (!loaded && !unloaded)
            {
                var err = doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString() : null;
                return (false, err ?? "Prompt model is still loading or failed to load.");
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Local prompt server isn't reachable on {_options.BaseUrl} — start tools/run-prompt-server.ps1. ({ex.Message})");
        }
    }

    /// <summary>
    /// Returns an enhanced version of <paramref name="text"/>. A non-empty
    /// <paramref name="model"/> asks the server to use that model id (swapping if needed).
    /// Auto-starts the server if it's down and waits for the model to load. Never returns
    /// empty — falls back to the original text on any failure so a generation can proceed.
    /// </summary>
    public async Task<string> EnhanceAsync(string text, string? style, string? model, CancellationToken ct = default)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length == 0) return text;

        await EnsureUpAsync(ct);
        try
        {
            var enhanced = (await _client.EnhanceAsync(text, style, model, _options.MaxTokens, ct) ?? string.Empty).Trim();
            return enhanced.Length == 0 ? text : enhanced;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prompt enhancement failed; falling back to the original text.");
            return text;
        }
    }

    /// <summary>Starts the server if it isn't listening and waits (best-effort) for the model to load.</summary>
    private async Task EnsureUpAsync(CancellationToken ct)
    {
        if (_control.IsPortListening()) return;

        _logger.LogInformation("Prompt server not running — starting it on demand.");
        if (!_control.StartDetached()) return;   // script missing; EnhanceAsync will then fall back

        // Wait for the port to bind, then for the model to report loaded (LLM load can take a while).
        for (var i = 0; i < 60 && !_control.IsPortListening(); i++)
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        for (var i = 0; i < 90; i++)
        {
            var (ok, _) = await GetHealthAsync(ct);
            if (ok) return;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
}
