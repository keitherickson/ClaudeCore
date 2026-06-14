using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;

namespace ClaudeCore.Services;

public sealed record UpscaleResult(string FileName, string SavedPath, string Command);

/// <summary>
/// Runs NVIDIA Maxine's VideoEffectsApp.exe as a per-job subprocess to upscale a
/// video, then exposes the result from the output directory.
/// </summary>
public sealed class MaxineUpscaleService
{
    private static readonly string[] ModelEffects = { "SuperRes", "ArtifactReduction" };

    private readonly MaxineUpscaleOptions _o;
    private readonly ILogger<MaxineUpscaleService> _log;

    public MaxineUpscaleService(IOptions<MaxineUpscaleOptions> o, ILogger<MaxineUpscaleService> log)
    {
        _o = o.Value;
        _log = log;
    }

    public string OutputDirectory => _o.OutputDirectory;

    /// <summary>Staging dir for uploaded source videos.</summary>
    public string InputDirectory => _o.InputDirectory;

    /// <summary>Reports whether the SDK looks installed/configured, for a status badge.</summary>
    public bool IsReady(out string? problem)
    {
        if (!File.Exists(_o.ExecutablePath))
        {
            problem = $"VideoEffectsApp.exe not found at {_o.ExecutablePath}";
            return false;
        }
        if (!Directory.Exists(_o.ModelDir))
        {
            problem = $"SDK models not found at {_o.ModelDir} — install the Maxine Video Effects redistributable.";
            return false;
        }
        problem = null;
        return true;
    }

    public async Task<string> StageInputAsync(IFormFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_o.InputDirectory);
        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".mp4";
        var path = Path.Combine(_o.InputDirectory, $"in_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}");
        await using var fs = File.Create(path);
        await file.CopyToAsync(fs, ct);
        return path;
    }

    public async Task<UpscaleResult> UpscaleAsync(
        string inputPath, string effect, int resolution, int mode, float strength, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_o.OutputDirectory);
        var outName = $"upscaled_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.mp4";
        var outPath = Path.Combine(_o.OutputDirectory, outName);

        var args = new List<string>
        {
            $"--in_file={inputPath}",
            $"--out_file={outPath}",
            $"--effect={effect}",
            $"--resolution={resolution}",
        };
        if (ModelEffects.Contains(effect))
        {
            args.Add($"--mode={mode}");
            if (!string.IsNullOrWhiteSpace(_o.ModelDir)) args.Add($"--model_dir={_o.ModelDir}");
        }
        if (effect == "Upscale")
            args.Add($"--strength={strength.ToString(CultureInfo.InvariantCulture)}");
        if (!string.IsNullOrWhiteSpace(_o.Codec))
            args.Add($"--codec={_o.Codec}");

        var exeDir = Path.GetDirectoryName(_o.ExecutablePath)!;
        var psi = new ProcessStartInfo
        {
            FileName = _o.ExecutablePath,
            WorkingDirectory = exeDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // The exe loads SDK + OpenCV DLLs from disk; make sure they're on PATH.
        var extra = new[] { _o.SdkBinDir, _o.OpenCvBinDir }.Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p));
        psi.Environment["PATH"] = string.Join(';', extra) + ";" + Environment.GetEnvironmentVariable("PATH");

        var cmd = $"\"{_o.ExecutablePath}\" {string.Join(' ', args)}";
        _log.LogInformation("Maxine upscale: {Cmd}", cmd);

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMinutes(_o.TimeoutMinutes));
        try
        {
            await proc.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new InvalidOperationException("Upscale timed out or was cancelled.");
        }

        if (proc.ExitCode != 0 || !File.Exists(outPath))
            throw new InvalidOperationException(
                $"Maxine upscale failed (exit {proc.ExitCode}).\n{stderr}\n{stdout}".Trim());

        _log.LogInformation("Upscaled video saved to {Out}", outPath);
        return new UpscaleResult(outName, outPath, cmd);
    }

    public string GetOutputFilePath(string name) => Path.Combine(_o.OutputDirectory, Path.GetFileName(name));
}
