using Microsoft.Extensions.Options;

namespace KeithVision.Services;

/// <summary>
/// Remembers, server-side, which video model the Generate flow should use, toggled
/// from /Admin. Persisted to a small state file so the choice survives app restarts
/// (same pattern as <see cref="LastImageStore"/>). Falls back to the configured
/// <see cref="VideoModelsOptions.Default"/> when nothing is persisted or the
/// persisted id is no longer a known model.
/// </summary>
public sealed class ActiveModelStore
{
    private readonly VideoModelsOptions _models;
    private readonly string _statePath;
    private readonly object _lock = new();
    private string _activeId;

    public ActiveModelStore(IOptions<VideoModelsOptions> models, IOptions<LtxVideoOptions> ltx)
    {
        _models = models.Value;
        var dir = ltx.Value.InputDirectory;
        try { Directory.CreateDirectory(dir); } catch { /* best effort */ }
        _statePath = Path.Combine(dir, ".active-model.txt");

        _activeId = _models.Default;
        try
        {
            if (File.Exists(_statePath))
            {
                var id = File.ReadAllText(_statePath).Trim();
                if (IsKnown(id)) _activeId = id;
            }
        }
        catch { /* ignore corrupt/missing state */ }

        // Guard against a Default that isn't in the list.
        if (!IsKnown(_activeId) && _models.Models.Count > 0)
            _activeId = _models.Models[0].Id;
    }

    private bool IsKnown(string? id) => !string.IsNullOrWhiteSpace(id) && _models.Models.Any(m => m.Id == id);

    /// <summary>The currently-selected model id.</summary>
    public string ActiveId
    {
        get { lock (_lock) return _activeId; }
    }

    /// <summary>The currently-selected model entry (or the first known model).</summary>
    public VideoModelEntry Active
    {
        get
        {
            lock (_lock)
                return _models.Models.FirstOrDefault(m => m.Id == _activeId) ?? _models.Models[0];
        }
    }

    /// <summary>Switch the active model. Returns false if the id isn't a known model.</summary>
    public bool Set(string id)
    {
        if (!IsKnown(id)) return false;
        lock (_lock)
        {
            _activeId = id;
            try { File.WriteAllText(_statePath, id); } catch { /* best effort */ }
        }
        return true;
    }
}
