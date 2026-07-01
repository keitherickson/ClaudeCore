using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>
/// Controls the local RVC server process (tools/run-rvc-server.ps1, which runs
/// `python -m rvc_python api` on port 8773). Reports port status and starts/stops on
/// demand for the Admin page. Not an auto-start service. Mirrors <see cref="MusicServerControl"/>.
/// </summary>
public sealed class RvcServerControl
{
    private readonly LocalRvcOptions _options;
    private readonly ILogger<RvcServerControl> _logger;

    public RvcServerControl(IOptions<LocalRvcOptions> options, ILogger<RvcServerControl> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Port parsed from LocalRvc:BaseUrl (defaults to 8773).</summary>
    public int Port => Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var u) && u.Port > 0 ? u.Port : 8773;

    /// <summary>True if anything is listening on the RVC port (cheap, no HTTP call).</summary>
    public bool IsPortListening()
    {
        var port = Port;
        foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (ep.Port == port && (IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(IPAddress.Any)))
                return true;
        return false;
    }

    /// <summary>
    /// Launches the RVC server detached and returns immediately — base models (hubert/rmvpe)
    /// load lazily on the first conversion, so the caller polls /models to see it come online.
    /// </summary>
    public bool StartDetached()
    {
        var script = _options.StartScriptPath;
        if (!File.Exists(script))
        {
            _logger.LogWarning("RVC start script not found at {Script}", script);
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script,
                "-Port", Port.ToString(),
                "-Gpu", _options.GpuIndex.ToString(),
                "-ModelsDir", _options.ModelsDir,
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogInformation("Starting RVC server (detached) on port {Port} (GPU {Gpu}, models {Dir})", Port, _options.GpuIndex, _options.ModelsDir);
        Process.Start(psi);
        return true;
    }

    /// <summary>Runs the stop script (kills the rvc_python api process + the port owner); returns its output.</summary>
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

    /// <summary>
    /// Installs a target voice from a URL by running tools/download-rvc-model.ps1, which places
    /// the .pth/.index into the models dir in the layout the server expects. Returns the script's
    /// output. The URL/name are passed as separate process args (no shell), so they can't inject.
    /// </summary>
    public async Task<RestartResult> DownloadVoiceAsync(string url, string? indexUrl, string? name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new RestartResult(false, "A voice URL is required.");

        var script = _options.DownloadScriptPath;
        if (!File.Exists(script))
            return new RestartResult(false, $"Download script not found at {script}");

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script,
                "-Url", url,
                "-ModelsDir", _options.ModelsDir,
                "-Force",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (!string.IsNullOrWhiteSpace(indexUrl)) { psi.ArgumentList.Add("-IndexUrl"); psi.ArgumentList.Add(indexUrl); }
        if (!string.IsNullOrWhiteSpace(name)) { psi.ArgumentList.Add("-Name"); psi.ArgumentList.Add(name); }

        using var proc = new Process { StartInfo = psi };
        var sb = new System.Text.StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) sb.AppendLine(e.Data); };

        _logger.LogInformation("Downloading RVC voice from {Url} into {Dir}", url, _options.ModelsDir);
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        // Downloads (100–200 MB .pth over community CDNs) can be slow — allow well past the
        // 5-minute conversion timeout.
        using var cap = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cap.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            await proc.WaitForExitAsync(cap.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new RestartResult(false, "Download timed out after 20 minutes.\n" + sb);
        }

        var output = sb.ToString().Trim();
        return new RestartResult(proc.ExitCode == 0, output);
    }
}
