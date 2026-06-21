using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>Outcome of a restart attempt: whether it succeeded plus the script's console output.</summary>
public sealed record RestartResult(bool Ok, string Output);

/// <summary>
/// Controls the local LTX-2.3 inference server process (the python server on
/// port 8765). Used by the admin page to report whether the port is listening
/// and to stop+restart the server via tools/restart-ltx-server.ps1.
/// </summary>
public sealed class LtxServerControl
{
    private readonly LtxVideoOptions _options;
    private readonly ILogger<LtxServerControl> _logger;

    public LtxServerControl(IOptions<LtxVideoOptions> options, ILogger<LtxServerControl> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Port parsed from Ltx:BaseUrl (defaults to 8765 if it can't be read).</summary>
    public int Port => Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var u) && u.Port > 0 ? u.Port : 8765;

    /// <summary>GPU index the LTX server is pinned to (Ltx:GpuIndex).</summary>
    public int GpuIndex => _options.GpuIndex;

    /// <summary>True if anything is currently listening on the LTX port (cheap, no HTTP call).</summary>
    public bool IsPortListening()
    {
        var port = Port;
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        foreach (var ep in listeners)
        {
            if (ep.Port == port &&
                (IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(IPAddress.Any)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Fire-and-forget restart: launches the restart script detached and returns
    /// immediately, without capturing output or tracking the process. Used by the
    /// Generate page's Cancel to free the GPU now (the script kills the port's
    /// process first, then relaunches). Crucially it does NOT wait or kill a
    /// process tree, so the freshly relaunched server is never torn down.
    /// </summary>
    public void RestartDetached()
    {
        var script = _options.RestartScriptPath;
        if (!File.Exists(script))
        {
            _logger.LogWarning("Restart script not found at {Script}; cannot hard-stop.", script);
            return;
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

        _logger.LogInformation("Hard-stop: launching detached LTX restart on port {Port} (GPU {Gpu})", Port, _options.GpuIndex);
        Process.Start(psi); // detached on purpose — the relaunched server must outlive this call
    }

    /// <summary>
    /// Runs the stop script (kills the LTX python + the port owner) WITHOUT
    /// relaunching. Used by the model switch to free this GPU's VRAM when moving off
    /// the BF16 model. Returns the script's output.
    /// </summary>
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

    /// <summary>
    /// Runs the restart script (stop the process on the port, relaunch it, wait
    /// for it to start listening). Blocks until the script finishes or a hard
    /// 90s cap elapses. Returns the script's combined output either way.
    /// </summary>
    public async Task<RestartResult> RestartAsync(CancellationToken ct = default)
    {
        var script = _options.RestartScriptPath;
        if (!File.Exists(script))
            return new RestartResult(false, $"Restart script not found at {script}");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script, "-Port", Port.ToString(), "-Gpu", _options.GpuIndex.ToString()
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogInformation("Restarting LTX server via {Script} on port {Port} (GPU {Gpu})", script, Port, _options.GpuIndex);

        using var proc = new Process { StartInfo = psi };
        var stdout = new System.Text.StringBuilder();
        var stderr = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(TimeSpan.FromSeconds(90));
        try
        {
            await proc.WaitForExitAsync(cap.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new RestartResult(false, "Restart timed out after 90s.\n" + stdout + stderr);
        }

        var output = (stdout.ToString() + stderr.ToString()).Trim();
        var ok = proc.ExitCode == 0;
        if (!ok) _logger.LogWarning("LTX restart script exited with code {Code}: {Output}", proc.ExitCode, output);
        return new RestartResult(ok, output);
    }
}
