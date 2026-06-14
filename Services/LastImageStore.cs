using Microsoft.Extensions.Options;

namespace ClaudeCore.Services;

/// <summary>
/// Remembers, server-side, the last successfully-used starting image so the next
/// generation can reuse it automatically. Persisted to a small state file in the
/// input directory so it survives app restarts.
/// </summary>
public sealed class LastImageStore
{
    private readonly string _statePath;
    private readonly object _lock = new();
    private string? _path;

    public LastImageStore(IOptions<LtxVideoOptions> options)
    {
        var dir = options.Value.InputDirectory;
        try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        _statePath = Path.Combine(dir, ".last-image.txt");

        try
        {
            if (File.Exists(_statePath))
            {
                var p = File.ReadAllText(_statePath).Trim();
                if (!string.IsNullOrEmpty(p) && File.Exists(p)) _path = p;
            }
        }
        catch { /* ignore corrupt/missing state */ }
    }

    /// <summary>The remembered image path, or null if none / the file no longer exists.</summary>
    public string? Get()
    {
        lock (_lock)
        {
            if (_path != null && !File.Exists(_path)) _path = null;
            return _path;
        }
    }

    /// <summary>Remember a path (or clear with null). Persisted to disk.</summary>
    public void Set(string? path)
    {
        lock (_lock)
        {
            _path = string.IsNullOrWhiteSpace(path) ? null : path;
            try
            {
                if (_path != null) File.WriteAllText(_statePath, _path);
                else if (File.Exists(_statePath)) File.Delete(_statePath);
            }
            catch { /* best effort */ }
        }
    }
}
