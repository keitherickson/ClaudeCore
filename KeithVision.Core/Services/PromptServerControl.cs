using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>
/// Controls the local prompt-enhancer server process (tools/run-prompt-server.ps1 on
/// port 8771). The Admin page reports whether the port is listening and can start/stop
/// it; <see cref="PromptEnhanceService"/> also auto-starts it on first use. Mirrors
/// <see cref="AudioServerControl"/> and reuses <see cref="RestartResult"/> for stop.
/// </summary>
public sealed class PromptServerControl
{
    private readonly LocalLlmOptions _options;
    private readonly ILogger<PromptServerControl> _logger;

    public PromptServerControl(IOptions<LocalLlmOptions> options, ILogger<PromptServerControl> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Port parsed from LocalLlm:BaseUrl (defaults to 8771).</summary>
    public int Port => Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var u) && u.Port > 0 ? u.Port : 8771;

    /// <summary>True if anything is listening on the prompt-server port (cheap, no HTTP call).</summary>
    public bool IsPortListening()
    {
        var port = Port;
        foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (ep.Port == port && (IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(IPAddress.Any)))
                return true;
        return false;
    }

    /// <summary>
    /// Launches the prompt server detached and returns immediately — it loads the model
    /// in the background, so the caller polls /health to see it come online. Detached on
    /// purpose, so the server outlives this request.
    /// </summary>
    public bool StartDetached()
    {
        var script = _options.StartScriptPath;
        if (!File.Exists(script))
        {
            _logger.LogWarning("Prompt start script not found at {Script}", script);
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script, "-Port", Port.ToString(), "-Gpu", _options.GpuIndex.ToString()
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogInformation("Starting prompt server (detached) on port {Port} (GPU {Gpu})", Port, _options.GpuIndex);
        Process.Start(psi);
        return true;
    }

    /// <summary>Runs the stop script (kills prompt_server.py + the port owner); returns its output.</summary>
    public async Task<RestartResult> StopAsync(CancellationToken ct = default)
    {
        var script = _options.StopScriptPath;
        if (!File.Exists(script))
            return new RestartResult(false, $"Stop script not found at {script}");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script, "-Port", Port.ToString()
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        var sb = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await proc.WaitForExitAsync(cap.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new RestartResult(false, "Stop timed out after 30s.\n" + sb);
        }

        return new RestartResult(proc.ExitCode == 0, sb.ToString().Trim());
    }
}
