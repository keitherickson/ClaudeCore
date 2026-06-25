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

    /// <summary>What a paused iteration loop should do next: keep going (optionally with a new
    /// prompt) or stop early and stitch what's already been produced.</summary>
    public sealed record ContinueSignal(string? Prompt, bool Stop);

    private sealed record Entry(CancellationTokenSource Cts, DateTime StartedUtc);

    private readonly ConcurrentDictionary<string, Entry> _runs = new();

    // Per-run "pause gates": a paused loop awaits the run's TCS; a separate Continue request
    // completes it. Armed just before the loop emits its "paused" event so the gate exists by
    // the time the client can react.
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ContinueSignal>> _pauses = new();

    /// <summary>Registers a run linked to its request token; returns the id and the token that should drive the run.</summary>
    public (string Id, CancellationToken Token) Register(CancellationToken requestToken)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var cts = CancellationTokenSource.CreateLinkedTokenSource(requestToken);
        _runs[id] = new Entry(cts, DateTime.UtcNow);
        return (id, cts.Token);
    }

    /// <summary>Removes and disposes a finished run (and drops any pending pause gate).</summary>
    public void Unregister(string id)
    {
        if (_runs.TryRemove(id, out var e)) e.Cts.Dispose();
        if (_pauses.TryRemove(id, out var tcs)) tcs.TrySetCanceled();
    }

    /// <summary>Arms a pause gate for a run just before it emits its "paused" event, so a
    /// Continue request that arrives immediately after has a gate to complete.</summary>
    public void ArmPause(string id)
        => _pauses[id] = new TaskCompletionSource<ContinueSignal>(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Awaits the next Continue signal for a paused run. Returns "keep going, no prompt
    /// change" if no gate is armed; throws if the run is cancelled while paused.</summary>
    public async Task<ContinueSignal> WaitForContinueAsync(string id, CancellationToken ct)
    {
        if (!_pauses.TryGetValue(id, out var tcs)) return new ContinueSignal(null, false);
        try
        {
            using (ct.Register(static s => ((TaskCompletionSource<ContinueSignal>)s!).TrySetCanceled(), tcs))
                return await tcs.Task;
        }
        finally { _pauses.TryRemove(id, out _); }
    }

    /// <summary>Releases a paused run with the caller's signal; false if it wasn't paused.</summary>
    public bool Continue(string id, ContinueSignal signal)
        => _pauses.TryGetValue(id, out var tcs) && tcs.TrySetResult(signal);

    /// <summary>True if the run is currently sitting at a pause gate.</summary>
    public bool IsPaused(string id) => _pauses.ContainsKey(id);

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
