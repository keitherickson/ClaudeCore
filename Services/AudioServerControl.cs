using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Options;

namespace ClaudeCore.Services;

/// <summary>
/// Controls the local Stable Audio server process (tools/run-audio-server.ps1 on
/// port 8770). Used by the Admin page to report whether the port is listening and
/// to start/stop the server on demand. It is intentionally NOT an auto-start
/// service, so it only runs when started here (or from the command line).
/// Reuses <see cref="RestartResult"/> from LtxServerControl for the stop outcome.
/// </summary>
public sealed class AudioServerControl
{
    private readonly LocalAudioOptions _options;
    private readonly ILogger<AudioServerControl> _logger;

    public AudioServerControl(IOptions<LocalAudioOptions> options, ILogger<AudioServerControl> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Port parsed from LocalAudio:BaseUrl (defaults to 8770).</summary>
    public int Port => Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var u) && u.Port > 0 ? u.Port : 8770;

    /// <summary>True if anything is listening on the audio port (cheap, no HTTP call).</summary>
    public bool IsPortListening()
    {
        var port = Port;
        foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (ep.Port == port && (IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(IPAddress.Any)))
                return true;
        return false;
    }

    /// <summary>
    /// Launches the audio server detached and returns immediately — it loads the
    /// model in the background (~30-60s), so the caller polls /Admin/Status to see
    /// it come online. Detached on purpose, so the server outlives this request.
    /// </summary>
    public bool StartDetached()
    {
        var script = _options.StartScriptPath;
        if (!File.Exists(script))
        {
            _logger.LogWarning("Audio start script not found at {Script}", script);
            return false;
        }

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList =
            {
                "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                "-File", script, "-Port", Port.ToString()
            },
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        _logger.LogInformation("Starting audio server (detached) on port {Port}", Port);
        Process.Start(psi);
        return true;
    }

    /// <summary>Runs the stop script (kills audio_server.py + the port owner); returns its output.</summary>
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
