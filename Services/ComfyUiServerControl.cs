using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>
/// Controls the ComfyUI NVFP4 server process (tools/run-comfyui.ps1 on port 8188,
/// the Blackwell-only "fast" video backend). The /Admin model switch brings it up
/// when NVFP4 is selected and stops it when leaving NVFP4 — it is intentionally NOT
/// an auto-start service (the default BF16 model uses the always-on LTX server).
/// Mirrors <see cref="AudioServerControl"/>; reuses <see cref="RestartResult"/>.
/// </summary>
public sealed class ComfyUiServerControl
{
    private readonly ComfyUiOptions _options;
    private readonly ILogger<ComfyUiServerControl> _logger;

    public ComfyUiServerControl(IOptions<ComfyUiOptions> options, ILogger<ComfyUiServerControl> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Port parsed from ComfyUI:BaseUrl (defaults to 8188).</summary>
    public int Port => Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var u) && u.Port > 0 ? u.Port : 8188;

    /// <summary>GPU index ComfyUI is pinned to (ComfyUI:GpuIndex; must be the 5090).</summary>
    public int GpuIndex => _options.GpuIndex;

    /// <summary>True if anything is listening on the ComfyUI port (cheap, no HTTP call).</summary>
    public bool IsPortListening()
    {
        var port = Port;
        foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (ep.Port == port && (IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(IPAddress.Any)))
                return true;
        return false;
    }

    /// <summary>
    /// Launches ComfyUI detached and returns immediately — it imports nodes and binds
    /// the port over ~30-90s (model weights then load lazily on the first generation),
    /// so the caller polls /Admin/Status to watch it come online.
    /// </summary>
    public bool StartDetached()
    {
        var script = _options.StartScriptPath;
        if (!File.Exists(script))
        {
            _logger.LogWarning("ComfyUI start script not found at {Script}", script);
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

        _logger.LogInformation("Starting ComfyUI (detached) on port {Port} (GPU {Gpu})", Port, _options.GpuIndex);
        Process.Start(psi);
        return true;
    }

    /// <summary>Runs the stop script (kills ComfyUI's main.py + the port owner); returns its output.</summary>
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

        var output = sb.ToString().Trim();
        return new RestartResult(proc.ExitCode == 0, output);
    }
}
