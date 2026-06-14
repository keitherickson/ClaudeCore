using ClaudeCore.Models.Ltx;
using Microsoft.Extensions.Options;

namespace ClaudeCore.Services;

/// <summary>Result of a completed generation, after the video is copied to the local output folder.</summary>
public sealed record VideoResult(string FileName, string SavedPath, string ServerPath);

/// <summary>
/// Orchestrates a generation end to end: stage the input image where the server
/// can read it, call the server, then copy the finished video to the configured
/// local output directory.
/// </summary>
public sealed class LtxVideoService
{
    private readonly LtxVideoClient _client;
    private readonly LtxVideoOptions _options;
    private readonly ILogger<LtxVideoService> _logger;

    public LtxVideoService(LtxVideoClient client, IOptions<LtxVideoOptions> options, ILogger<LtxVideoService> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public string OutputDirectory => _options.OutputDirectory;

    /// <summary>Staging dir for uploaded conditioning images.</summary>
    public string InputDirectory => _options.InputDirectory;

    public Task<LtxHealth?> GetHealthAsync(CancellationToken ct = default) => _client.GetHealthAsync(ct);

    public Task<GenerationProgress?> GetProgressAsync(CancellationToken ct = default) => _client.GetProgressAsync(ct);

    public Task<string> GetModelSpecsRawAsync(CancellationToken ct = default) => _client.GetModelSpecsRawAsync(ct);

    public Task<string> GetHealthRawAsync(CancellationToken ct = default) => _client.GetHealthRawAsync(ct);

    /// <summary>Saves an uploaded conditioning image to a local path the server can open, and returns that path.</summary>
    public async Task<string> StageImageAsync(IFormFile image, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_options.InputDirectory);
        var ext = Path.GetExtension(image.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".png";

        var path = Path.Combine(
            _options.InputDirectory,
            $"input_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}");

        await using var fs = File.Create(path);
        await image.CopyToAsync(fs, ct);
        return path;
    }

    public async Task<VideoResult> GenerateAsync(GenerateVideoRequest request, CancellationToken ct = default)
    {
        var response = await _client.GenerateAsync(request, ct);
        if (response.Status != "complete" || string.IsNullOrEmpty(response.VideoPath))
            throw new LtxServerException(500, $"Generation did not complete (status={response.Status}).");

        Directory.CreateDirectory(_options.OutputDirectory);
        var fileName = Path.GetFileName(response.VideoPath);
        var dest = Path.Combine(_options.OutputDirectory, fileName);
        File.Copy(response.VideoPath, dest, overwrite: true);

        _logger.LogInformation("LTX video saved to {Dest}", dest);
        return new VideoResult(fileName, dest, response.VideoPath);
    }

    /// <summary>Resolves a bare file name to a path inside the output dir (guards against path traversal).</summary>
    public string GetOutputFilePath(string fileName)
        => Path.Combine(_options.OutputDirectory, Path.GetFileName(fileName));

    /// <summary>True if the path is a real file inside the staging input directory (prevents reading arbitrary files).</summary>
    public bool IsStagedInputImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            var dir = Path.GetFullPath(_options.InputDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) && File.Exists(full);
        }
        catch
        {
            return false;
        }
    }
}
