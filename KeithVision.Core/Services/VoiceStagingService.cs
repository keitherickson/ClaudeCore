using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>The processed clip: the file name and its full path in the output dir.</summary>
public sealed record VoiceResult(string FileName, string SavedPath);

/// <summary>
/// Shared file plumbing for the /Voice page: stages an uploaded/recorded clip into the
/// input dir and guards the input/output trees. Pure filesystem — no model server and no
/// ffmpeg — so it just reuses <see cref="VideoSpeedOptions"/> for the staging input dir and
/// the output dir. The actual voice conversion lives in <see cref="RvcVoiceService"/>.
/// </summary>
public sealed class VoiceStagingService
{
    private readonly VideoSpeedOptions _o;

    public VoiceStagingService(IOptions<VideoSpeedOptions> o) => _o = o.Value;

    public string OutputDirectory => _o.OutputDirectory;
    public string InputDirectory => _o.InputDirectory;

    /// <summary>Saves an uploaded/recorded clip to the staging input dir and returns its path.</summary>
    public async Task<string> StageInputAsync(IFormFile file, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_o.InputDirectory);
        var ext = Path.GetExtension(file.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".webm"; // browser MediaRecorder default
        var path = Path.Combine(_o.InputDirectory, $"voicein_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}");
        await using var fs = File.Create(path);
        await file.CopyToAsync(fs, ct);
        return path;
    }

    /// <summary>True if the path is a real file inside the output dir (guards the serve/download endpoint).</summary>
    public bool IsOutputFile(string? path) => IsInside(path, _o.OutputDirectory);

    /// <summary>True if the path is a real file inside the staging input dir.</summary>
    public bool IsInputFile(string? path) => IsInside(path, _o.InputDirectory);

    private static bool IsInside(string? path, string dir)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
        }
        catch { return false; }
    }
}
