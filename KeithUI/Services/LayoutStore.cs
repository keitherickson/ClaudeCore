using System.Text;
using System.Text.Json;

namespace KeithUI.Services;

/// <summary>
/// Persists named studio layouts (serialized LiteGraph graphs) as JSON files on
/// disk, so a graph can be saved by name and reloaded from the toolbar dropdown.
/// One file per layout under {ContentRoot}/App_Data/studio-layouts; the on-disk
/// filename is a sanitized slug (path-guarded to the store dir), while the
/// user-facing display name is kept inside the file for the dropdown.
/// </summary>
public sealed class LayoutStore
{
    private readonly string _dir;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public LayoutStore(IWebHostEnvironment env)
    {
        _dir = Path.Combine(env.ContentRootPath, "App_Data", "studio-layouts");
        Directory.CreateDirectory(_dir);
    }

    public sealed record LayoutInfo(string Name, DateTime SavedUtc);

    /// <summary>Lists saved layouts, ordered by display name.</summary>
    public IReadOnlyList<LayoutInfo> List()
    {
        var list = new List<LayoutInfo>();
        foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
        {
            try
            {
                var s = JsonSerializer.Deserialize<StoredLayout>(File.ReadAllText(f), Web);
                if (s is not null && !string.IsNullOrWhiteSpace(s.name))
                    list.Add(new LayoutInfo(s.name, s.savedUtc));
            }
            catch { /* skip an unreadable/foreign file rather than fail the whole list */ }
        }
        return list.OrderBy(l => l.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Saves (or overwrites) the layout under <paramref name="name"/>.</summary>
    public async Task SaveAsync(string name, JsonElement graph, CancellationToken ct = default)
    {
        var path = PathFor(name);   // also validates the name
        var stored = new StoredLayout { name = name.Trim(), savedUtc = DateTime.UtcNow, graph = graph };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(stored, Web), ct);
    }

    /// <summary>Returns the saved graph for <paramref name="name"/>, or null if there is none.</summary>
    public async Task<JsonElement?> LoadAsync(string name, CancellationToken ct = default)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        var s = JsonSerializer.Deserialize<StoredLayout>(await File.ReadAllTextAsync(path, ct), Web);
        return s?.graph;
    }

    /// <summary>Deletes the named layout; returns false if it didn't exist.</summary>
    public bool Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    /// <summary>Maps a display name to its on-disk path via a sanitized slug, guarded to the store dir.</summary>
    private string PathFor(string name)
    {
        var slug = Slug(name);
        if (slug.Length == 0) throw new ArgumentException("Layout name has no usable characters.", nameof(name));
        var path = Path.GetFullPath(Path.Combine(_dir, slug + ".json"));
        var root = Path.GetFullPath(_dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Invalid layout name.", nameof(name));
        return path;
    }

    /// <summary>Lowercases and keeps [a-z0-9], collapsing spaces/dashes/underscores to '-'.</summary>
    private static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (ch is ' ' or '-' or '_') sb.Append('-');
            // anything else is dropped
        }
        return sb.ToString().Trim('-');
    }

    private sealed class StoredLayout
    {
        public string name { get; set; } = "";
        public DateTime savedUtc { get; set; }
        public JsonElement graph { get; set; }
    }
}
