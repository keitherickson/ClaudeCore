using System.Collections.Concurrent;

namespace KeithUI.Services;

/// <summary>
/// Tracks in-flight graph runs so a *separate* request — the admin dashboard, or a
/// Stop button — can cancel one. Each run gets a CancellationTokenSource linked to
/// its request's token; cancelling fires the token the <see cref="GraphExecutor"/>
/// is cooperatively checking, which aborts the in-flight backend HTTP call and
/// unwinds the run.
///
/// The video backends expose no server-side "interrupt" endpoint, so a cancel
/// abandons the run from KeithUI's side (the next node won't start, the current
/// backend call is aborted) rather than force-killing remote work already in flight.
/// </summary>
public sealed class RunRegistry
{
    public sealed record RunInfo(string Id, DateTime StartedUtc);

    private sealed record Entry(CancellationTokenSource Cts, DateTime StartedUtc);

    private readonly ConcurrentDictionary<string, Entry> _runs = new();

    /// <summary>Registers a run linked to its request token; returns the id and the token that should drive the run.</summary>
    public (string Id, CancellationToken Token) Register(CancellationToken requestToken)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var cts = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        _runs[id] = new Entry(cts, DateTime.UtcNow);
        return (id, cts.Token);
    }

    /// <summary>Removes and disposes a finished run.</summary>
    public void Unregister(string id)
    {
        if (_runs.TryRemove(id, out var e)) e.Cts.Dispose();
    }

    /// <summary>Cancels one run; false if it wasn't found (already finished/unknown id).</summary>
    public bool Cancel(string id)
    {
        if (!_runs.TryGetValue(id, out var e)) return false;
        try { e.Cts.Cancel(); } catch { /* already disposed/cancelled — treat as handled */ }
        return true;
    }

    /// <summary>Cancels every active run; returns how many were signalled.</summary>
    public int CancelAll()
    {
        var n = 0;
        foreach (var e in _runs.Values)
            try { e.Cts.Cancel(); n++; } catch { /* best effort */ }
        return n;
    }

    /// <summary>Active runs, oldest first.</summary>
    public IReadOnlyList<RunInfo> List()
        => _runs.Select(kv => new RunInfo(kv.Key, kv.Value.StartedUtc)).OrderBy(r => r.StartedUtc).ToList();
}
