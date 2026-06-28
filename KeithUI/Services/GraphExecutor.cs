using System.Net.NetworkInformation;
using System.Text.Json;
using KeithVision.Models.Ltx;
using KeithVision.Services;
using Microsoft.Extensions.Options;

namespace KeithUI.Services;

/// <summary>
/// Executes a serialized LiteGraph graph by walking it and calling the reused
/// KeithVision.Core services. Emits per-node events (start / progress / done / error)
/// as it runs so the canvas can light up like ComfyUI; each node produces a file
/// (image/audio/video) handed to downstream nodes along the edges.
/// </summary>
public sealed class GraphExecutor
{
    private readonly LtxVideoService _ltx;
    private readonly ActiveModelStore _activeModel;
    private readonly VideoBackendCoordinator _coordinator;
    private readonly MaxineUpscaleService _maxine;
    private readonly ComfyUiUpscaleService _aiUpscale;
    private readonly VideoSpeedService _speed;
    private readonly SoundGenService _sound;
    private readonly PromptEnhanceService _prompt;   // for the "Enhance Prompt" node (local LLM on the 4090)
    private readonly VideoDefaultsOptions _defaults;
    private readonly LayoutStore _layouts;   // for the "Run Group" node (a saved layout = a config file)
    private readonly RunRegistry _runs;      // for the loop node's pause-between-iterations gate
    private readonly ILogger<GraphExecutor> _log;

    public GraphExecutor(
        LtxVideoService ltx, ActiveModelStore activeModel, VideoBackendCoordinator coordinator,
        MaxineUpscaleService maxine, ComfyUiUpscaleService aiUpscale, VideoSpeedService speed,
        SoundGenService sound, PromptEnhanceService prompt, IOptions<VideoDefaultsOptions> defaults, LayoutStore layouts, RunRegistry runs, ILogger<GraphExecutor> log)
    {
        _ltx = ltx; _activeModel = activeModel; _coordinator = coordinator;
        _maxine = maxine; _aiUpscale = aiUpscale; _speed = speed; _sound = sound;
        _prompt = prompt; _defaults = defaults.Value; _layouts = layouts; _runs = runs; _log = log;
    }

    private const int MaxGroupDepth = 8;   // guard against deep/recursive Run Group chains

    /// <summary>Runs the top-level graph, calling <paramref name="emit"/> for each event.
    /// <paramref name="runId"/> (when supplied) lets pausing nodes await a Continue request.</summary>
    public async Task RunAsync(JsonElement graphJson, Func<object, Task> emit, CancellationToken ct, string? runId = null)
    {
        Task Log(string t) => emit(new { type = "log", text = t });
        try
        {
            var graph = graphJson.Deserialize<LGraph>(Web) ?? new LGraph();
            var finalVideo = await ExecuteGraphAsync(graph, emit, Log, depth: 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase), runId, ct);
            await emit(new { type = "done", finalVideo, ok = true });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Graph execution failed");
            await emit(new { type = "done", finalVideo = (string?)null, ok = false, error = ex.Message });
        }
    }

    /// <summary>
    /// Walks a single graph (top-level or a sub-graph loaded by a "Run Group" node) and
    /// returns the video that reaches its Preview Save (or the last produced output).
    /// Emits per-node events through <paramref name="emit"/>; on a node failure it emits
    /// node-error + a log line and rethrows, leaving the final "done" to the caller.
    /// <paramref name="groupChain"/> is the set of layout names already on the stack, so a
    /// group can't recurse into itself; <paramref name="depth"/> bounds nesting.
    /// </summary>
    private async Task<string?> ExecuteGraphAsync(LGraph graph, Func<object, Task> emit, Func<string, Task> Log, int depth, HashSet<string> groupChain, string? runId, CancellationToken ct)
    {
        var linkMap = new Dictionary<long, (long node, int slot)>();
        foreach (var l in graph.links)
            if (l.ValueKind == JsonValueKind.Array && l.GetArrayLength() >= 4)
                linkMap[l[0].GetInt64()] = (l[1].GetInt64(), l[2].GetInt32());

        // Only run the nodes that actually feed a Preview Save — walk upstream
        // from each Save node along the links. Floating nodes and dead-end
        // branches that don't reach an output are ignored. (No Save node at all
        // => fall back to running everything, preserving the last-output result.)
        var byId = graph.nodes.GroupBy(n => n.id).ToDictionary(g => g.Key, g => g.First());
        var saveRoots = graph.nodes.Where(n => n.type == "Preview Save/save").Select(n => n.id).ToList();
        HashSet<long> active;
        if (saveRoots.Count > 0)
        {
            active = new HashSet<long>();
            var stack = new Stack<long>(saveRoots);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!active.Add(id) || !byId.TryGetValue(id, out var node)) continue;
                foreach (var slot in node.inputs ?? new())
                    if (slot.link is long lid && linkMap.TryGetValue(lid, out var src) && !active.Contains(src.node))
                        stack.Push(src.node);
            }
        }
        else
        {
            active = graph.nodes.Select(n => n.id).ToHashSet();
        }
        var runNodes = graph.nodes.Where(n => active.Contains(n.id)).ToList();

        var outputs = new Dictionary<long, string>();
        var done = new HashSet<long>();
        string? finalVideo = null;

        while (done.Count < runNodes.Count)
        {
            var progressed = false;
            foreach (var n in runNodes)
            {
                if (done.Contains(n.id)) continue;
                var inputs = new Dictionary<string, string>();
                var ready = true;
                foreach (var slot in n.inputs ?? new())
                    if (slot.link is long lid && linkMap.TryGetValue(lid, out var src))
                    {
                        if (!outputs.TryGetValue(src.node, out var p)) { ready = false; break; }
                        inputs[slot.name ?? ""] = p;
                    }
                if (!ready) continue;

                await emit(new { type = "node-start", node = n.id });
                try
                {
                    var produced = await ExecuteNode(n, inputs, emit, Log, depth, groupChain, runId, ct);
                    if (produced is not null) outputs[n.id] = produced;
                    if (n.type == "Preview Save/save" && inputs.TryGetValue("video", out var fv))
                    {
                        finalVideo = fv;
                        await emit(new { type = "node-result", node = n.id, video = fv });  // play it in the node
                    }
                    await emit(new { type = "node-done", node = n.id });
                }
                catch (Exception ex)
                {
                    await emit(new { type = "node-error", node = n.id, error = ex.Message });
                    await Log("ERROR @ node " + n.id + ": " + ex.Message);
                    throw;   // let the caller (top-level run, or the enclosing Run Group node) decide
                }
                done.Add(n.id);
                progressed = true;
            }
            if (!progressed) { await Log("Stopped: a node has unmet inputs (cycle or missing connection)."); break; }
        }

        return finalVideo ?? outputs.Values.LastOrDefault();
    }

    private async Task<string?> ExecuteNode(LNode n, Dictionary<string, string> inputs, Func<object, Task> emit, Func<string, Task> log, int depth, HashSet<string> groupChain, string? runId, CancellationToken ct)
    {
        var w = n.widgets_values ?? new();
        string Str(int i, string d = "") => i < w.Count && w[i].ValueKind == JsonValueKind.String ? w[i].GetString() ?? d : d;
        int Num(int i, int d) => i < w.Count && w[i].ValueKind == JsonValueKind.Number ? w[i].GetInt32() : d;
        double Dbl(int i, double d) => i >= w.Count ? d
            : w[i].ValueKind == JsonValueKind.Number ? w[i].GetDouble()
            : w[i].ValueKind == JsonValueKind.String && double.TryParse(w[i].GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v
            : d;
        bool Bool(int i, bool d) => i < w.Count && w[i].ValueKind is JsonValueKind.True or JsonValueKind.False ? w[i].GetBoolean() : d;
        inputs.TryGetValue("image", out var imgIn);
        inputs.TryGetValue("audio", out var audIn);
        inputs.TryGetValue("video", out var vidIn);
        inputs.TryGetValue("prompt", out var promptIn);   // wired Enhance Prompt output (overrides the widget)

        switch (n.type)
        {
            case "Image/load_image":
            {
                var file = Str(0);
                await log($"Load Image: {(string.IsNullOrWhiteSpace(file) ? "(none)" : Path.GetFileName(file))}");
                return string.IsNullOrWhiteSpace(file) || !File.Exists(file) ? null : file;
            }
            case "Sound/load_sound":
            {
                var file = Str(0);
                await log($"Load Sound: {(string.IsNullOrWhiteSpace(file) ? "(none)" : Path.GetFileName(file))}");
                return string.IsNullOrWhiteSpace(file) || !File.Exists(file) ? null : file;
            }
            case "Video/load_video":
            {
                var file = Str(0);
                await log($"Load Video: {(string.IsNullOrWhiteSpace(file) ? "(none)" : Path.GetFileName(file))}");
                return string.IsNullOrWhiteSpace(file) || !File.Exists(file) ? null : file;
            }
            case "Sound/sound":
            {
                // A wired Enhance Prompt (TEXT) overrides the prompt box, mirroring the video nodes.
                var text = string.IsNullOrWhiteSpace(promptIn) ? Str(0) : promptIn;
                if (string.IsNullOrWhiteSpace(text)) { await log("Generate Sound: no prompt — skipped"); return null; }
                var staged = await _sound.GenerateAsync(text, Num(1, 5), ct);
                await log($"Generate Sound: '{text}' -> {Path.GetFileName(staged.Path)}");
                return staged.Path;
            }
            case "Video/generate":
            {
                var model = Str(0, "bf16-2.3");
                await EnsureModelReady(model, log, ct);
                var req = new GenerateVideoRequest
                {
                    Prompt = string.IsNullOrWhiteSpace(promptIn) ? Str(1) : promptIn,
                    Resolution = Str(2, "540p"),
                    Duration = Num(3, 5),
                    Fps = _defaults.Fps,
                    AspectRatio = Str(4, "16:9"),
                    ImagePath = imgIn,
                    AudioPath = audIn,
                    Audio = audIn is not null,
                };
                await log($"Generating [{model}] {req.Resolution} {req.Duration}s…");
                var genTask = _ltx.GenerateAsync(req, ct);
                while (await Task.WhenAny(genTask, Task.Delay(1500, ct)) != genTask)
                {
                    try
                    {
                        var p = await _ltx.GetProgressAsync(ct);
                        if (p is not null) await emit(new { type = "node-progress", node = n.id, pct = p.Progress });
                    }
                    catch { /* progress is best-effort */ }
                }
                var r = await genTask;
                await log($"Generate Video [{model}]: {Path.GetFileName(r.SavedPath)}");
                return r.SavedPath;
            }
            case "Video/extend":
            {
                var model = Str(0, "bf16-2.3");
                await EnsureModelReady(model, log, ct);
                var reso = Str(2, "540p");
                var secPerSeg = Num(3, 5);
                var segments = Math.Clamp(Num(4, 3), 2, 8);
                var aspect = Str(5, "16:9");
                await log($"Extend [{model}] {reso} {segments}×{secPerSeg}s — conditioning each segment on the previous tail frame…");

                var segmentPaths = new List<string>();
                string? condImage = imgIn;
                for (var s = 0; s < segments; s++)
                {
                    var segReq = new GenerateVideoRequest
                    {
                        Prompt = string.IsNullOrWhiteSpace(promptIn) ? Str(1) : promptIn,
                        Resolution = reso,
                        Duration = secPerSeg,
                        Fps = _defaults.Fps,
                        AspectRatio = aspect,
                        ImagePath = condImage,
                    };
                    var segIndex = s;   // capture for the progress closure
                    var segTask = _ltx.GenerateAsync(segReq, ct);
                    while (await Task.WhenAny(segTask, Task.Delay(1500, ct)) != segTask)
                    {
                        try
                        {
                            var p = await _ltx.GetProgressAsync(ct);
                            // Scale the per-segment percent into overall graph-node progress.
                            if (p is not null)
                                await emit(new { type = "node-progress", node = n.id, pct = (int)((segIndex + p.Progress / 100.0) / segments * 100) });
                        }
                        catch { /* progress is best-effort */ }
                    }
                    var seg = await segTask;
                    segmentPaths.Add(seg.SavedPath);
                    await log($"  segment {s + 1}/{segments}: {Path.GetFileName(seg.SavedPath)}");
                    if (s < segments - 1)
                        condImage = await _speed.ExtractLastFrameAsync(seg.SavedPath, _ltx.InputDirectory, ct);
                }

                var stitchName = $"studio_extended_{DateTime.Now:yyyyMMdd_HHmmss}_{segments}seg.mp4";
                var stitchPath = _ltx.GetOutputFilePath(stitchName);
                await _speed.ConcatAsync(segmentPaths, stitchPath, ct);
                foreach (var p in segmentPaths)
                    try { File.Delete(p); } catch { /* best effort — segments are superseded by the stitch */ }
                await log($"Extend: stitched {segments} segments -> {stitchName}");
                return stitchPath;
            }
            case "Video/trim_continue":
            {
                // Chained continuation: generate a clip, trim the last N seconds off it, take the
                // trimmed clip's final frame, generate the next clip from it — repeated "iterations"
                // times — then stitch the (trimmed) segments into one continuous video.
                var model = Str(0, "bf16-2.3");
                await EnsureModelReady(model, log, ct);
                var reso = Str(3, "540p");
                var secPerSeg = Num(4, 5);
                var iterations = Math.Clamp(Num(5, 3), 2, 8);
                var trim = Math.Max(0, Dbl(6, 1));
                var aspect = Str(7, "16:9");
                var pauseEach = Bool(8, false);   // pause after each iteration to edit the enhanced prompt
                await log($"Trim & Continue [{model}] {reso} {iterations}×{secPerSeg}s — trimming {trim}s off each segment's tail to condition the next{(pauseEach ? ", pausing between iterations" : "")}…");

                var segmentPaths = new List<string>();
                string? condImage = imgIn;
                // The node carries a raw "prompt" (widget 1, overridable by a wired prompt input) and an
                // editable "enhanced" field (widget 2). On the first run we enhance the raw idea into the
                // enhanced field and generate from it; at each pause the operator's edited text replaces
                // it and is used as-is (no re-enhancement). With no raw idea, fall back to the enhanced field.
                var rawPrompt = string.IsNullOrWhiteSpace(promptIn) ? Str(1) : promptIn;
                var currentPrompt = !string.IsNullOrWhiteSpace(rawPrompt)
                    ? await _prompt.EnhanceAsync(rawPrompt, null, null, ct)
                    : Str(2);
                await log($"Trim & Continue: enhanced prompt → {currentPrompt}");
                var stoppedEarly = false;
                for (var s = 0; s < iterations; s++)
                {
                    await emit(new { type = "node-prompt", node = n.id, enhanced = currentPrompt });   // reflect the active prompt in the node's "enhanced" field
                    var segReq = new GenerateVideoRequest
                    {
                        Prompt = currentPrompt,
                        Resolution = reso,
                        Duration = secPerSeg,
                        Fps = _defaults.Fps,
                        AspectRatio = aspect,
                        ImagePath = condImage,
                    };
                    var segIndex = s;   // capture for the progress closure
                    var segTask = _ltx.GenerateAsync(segReq, ct);
                    while (await Task.WhenAny(segTask, Task.Delay(1500, ct)) != segTask)
                    {
                        try
                        {
                            var p = await _ltx.GetProgressAsync(ct);
                            if (p is not null)
                                await emit(new { type = "node-progress", node = n.id, pct = (int)((segIndex + p.Progress / 100.0) / iterations * 100) });
                        }
                        catch { /* progress is best-effort */ }
                    }
                    var seg = await segTask;

                    if (s < iterations - 1)
                    {
                        // Trim this segment's tail, keep the shortened clip for the stitch, and
                        // condition the next generation on the trimmed clip's final frame.
                        var trimmed = await _speed.TrimTailAsync(seg.SavedPath, trim, ct);
                        condImage = await _speed.ExtractLastFrameAsync(trimmed, _ltx.InputDirectory, ct);
                        await emit(new { type = "node-image", node = n.id, image = condImage });   // show the latest frame in the node
                        try { File.Delete(seg.SavedPath); } catch { /* superseded by the trimmed clip */ }
                        segmentPaths.Add(trimmed);
                        await log($"  iteration {s + 1}/{iterations}: {Path.GetFileName(trimmed)} (tail trimmed {trim}s)");

                        // Pause point: let the operator review the frame and set the next prompt,
                        // or finish now with what's been produced. A separate Continue request
                        // releases the gate (see RunRegistry / StudioController.Continue).
                        if (pauseEach && runId is not null)
                        {
                            _runs.ArmPause(runId);
                            await emit(new { type = "iteration-paused", node = n.id, iteration = s + 1, total = iterations, image = condImage, prompt = currentPrompt });
                            await log($"Paused after iteration {s + 1}/{iterations} — adjust the prompt and continue, or finish now.");
                            var signal = await _runs.WaitForContinueAsync(runId, ct);
                            if (signal.Stop) { stoppedEarly = true; await log($"Trim & Continue: finishing early after {s + 1} iteration(s) at your request."); break; }
                            if (!string.IsNullOrWhiteSpace(signal.Prompt)) { currentPrompt = signal.Prompt!; await log($"  next prompt → {currentPrompt}"); }
                        }
                    }
                    else
                    {
                        // Keep the final segment full, but convert it to H.264 so every input to
                        // the concat demuxer shares a codec (LTX emits HEVC; the trimmed segments
                        // above are already H.264). No-op if it's already H.264.
                        await _speed.ConvertToH264InPlaceAsync(seg.SavedPath, ct);
                        segmentPaths.Add(seg.SavedPath);
                        await log($"  iteration {s + 1}/{iterations}: {Path.GetFileName(seg.SavedPath)}");
                    }
                }

                var produced = segmentPaths.Count;
                var loopName = $"studio_continue_{DateTime.Now:yyyyMMdd_HHmmss}_{produced}x.mp4";
                var loopPath = _ltx.GetOutputFilePath(loopName);
                await _speed.ConcatAsync(segmentPaths, loopPath, ct);
                foreach (var p in segmentPaths)
                    try { File.Delete(p); } catch { /* best effort — segments are superseded by the stitch */ }
                await log($"Trim & Continue: stitched {produced} segment(s){(stoppedEarly ? " (stopped early)" : "")} -> {loopName}");
                return loopPath;
            }
            case "Upscaling/upscale_ai":
            {
                if (vidIn is null) { await log("Upscale (AI): no video input — skipped"); return null; }
                var th = int.TryParse(Str(0, "1080"), out var v) ? v : 1080;
                var dims = VideoProbe.TryGetDimensions(vidIn);
                int tw = dims is { } d ? (int)Math.Round((double)d.Width / d.Height * th) : th * 16 / 9;
                tw = Even(Math.Max(2, tw)); th = Even(th);
                var name = $"studio_{Guid.NewGuid():N}{Path.GetExtension(vidIn)}";
                File.Copy(vidIn, Path.Combine(_aiUpscale.InputDirectory, name), true);
                var r = await _aiUpscale.UpscaleAsync(name, tw, th, ct);
                await log($"Upscale [AI]: -> {r.Width}x{r.Height} {Path.GetFileName(r.SavedPath)}");
                return r.SavedPath;
            }
            case "Upscaling/upscale_maxine":
            {
                if (vidIn is null) { await log("Upscale (MAXINE): no video input — skipped"); return null; }
                // Fail fast with the actionable cause (mirrors VideoController) instead of
                // letting the exe abort with a cryptic "exit -11 / Cannot find nvCVImage DLL".
                if (!_maxine.IsReady(out var problem))
                    throw new InvalidOperationException($"Maxine upscaling unavailable: {problem}");
                var factor = int.TryParse(Str(0, "2"), out var f) && f is 2 or 3 or 4 ? f : 2;
                var input = await _speed.EnsureH264Async(vidIn, ct);
                var srcH = VideoProbe.TryGetHeight(input) ?? 1080;
                var r = await _maxine.UpscaleAsync(input, "SuperRes", srcH * factor, 1, 0f, ct);
                await _speed.RestoreAudioAsync(r.SavedPath, vidIn, ct);
                if (!string.Equals(input, vidIn, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(input); } catch { /* best effort */ }
                await log($"Upscale [Maxine {factor}x]: {Path.GetFileName(r.SavedPath)}");
                return r.SavedPath;
            }
            case "Speed/speed":
            {
                if (vidIn is null) { await log("Speed Up: no video input — skipped"); return null; }
                var factor = double.TryParse(Str(0, "2"), out var sp) ? sp : 2.0;
                var r = await _speed.RetimeAsync(vidIn, factor, ct);
                await log($"Speed Up [{factor}x]: {Path.GetFileName(r.SavedPath)}");
                return r.SavedPath;
            }
            case "Video/trim_tail_frame":
            {
                // Remove the last N seconds of the clip and emit the final remaining frame as
                // an image (the frame at duration − N). VIDEO in, IMAGE out.
                if (vidIn is null) { await log("Trim Tail → Frame: no video input — skipped"); return null; }
                var trim = Math.Max(0, Dbl(0, 1));
                var frame = await _speed.ExtractFrameBeforeTailAsync(vidIn, trim, _ltx.InputDirectory, ct);
                await emit(new { type = "node-image", node = n.id, image = frame });   // show it in the node
                await log($"Trim Tail → Frame: removed last {trim}s -> {Path.GetFileName(frame)}");
                return frame;
            }
            case "Sound/add_audio":
            {
                if (vidIn is null) { await log("Add Audio: no video input — skipped"); return null; }
                if (audIn is null) { await log("Add Audio: no audio input — passing the video through unchanged"); return vidIn; }
                var dubbed = await _speed.MuxAudioAsync(vidIn, audIn, ct);
                await log($"Add Audio: {Path.GetFileName(audIn)} -> {Path.GetFileName(dubbed)}");
                return dubbed;
            }
            case "Prompts/enhance":
            {
                // Local LLM (4090) rewrites the idea into a vivid prompt; the string output
                // flows along the edge into a Generate/Extend Video "prompt" input.
                var idea = Str(0);
                if (string.IsNullOrWhiteSpace(idea)) { await log("Enhance Prompt: empty — skipped"); return null; }
                var style = Str(1, "cinematic");
                var model = Str(2);   // "" / "(default)" => the server's configured model
                if (string.Equals(model, "(default)", StringComparison.OrdinalIgnoreCase)) model = "";
                await log($"Enhance Prompt [{style}{(string.IsNullOrEmpty(model) ? "" : ", " + model)}]: \"{idea}\"…");
                var enhanced = await _prompt.EnhanceAsync(idea, style, model, ct);
                await log($"Enhance Prompt → {enhanced}");
                return enhanced;
            }
            case "Groups/run_group":
            {
                // A saved layout IS the config file: load it and run that group of nodes
                // as a self-contained sub-graph, emitting its result as this node's output.
                var name = Str(0);
                if (string.IsNullOrWhiteSpace(name)) { await log("Run Group: no layout selected — skipped"); return null; }
                if (depth >= MaxGroupDepth)
                    throw new InvalidOperationException($"Run Group '{name}': nesting too deep (> {MaxGroupDepth}).");
                if (!groupChain.Add(name))
                    throw new InvalidOperationException($"Run Group '{name}': recursive group reference.");
                try
                {
                    var layout = await _layouts.LoadAsync(name, ct)
                        ?? throw new InvalidOperationException($"Run Group: layout '{name}' not found.");
                    var sub = layout.Deserialize<LGraph>(Web) ?? new LGraph();
                    await log($"Run Group '{name}': running {sub.nodes.Count} node(s)…");

                    // Hide the sub-graph's per-node lifecycle from the parent canvas (those
                    // ids aren't on this graph). Forward log lines, and relabel generation
                    // progress onto this group node so it shows live progress while running.
                    var groupId = n.id;
                    Func<object, Task> subEmit = ev =>
                    {
                        var t = ev.GetType().GetProperty("type")?.GetValue(ev) as string;
                        if (t == "log") return emit(ev);
                        if (t == "node-progress")
                            return emit(new { type = "node-progress", node = groupId, pct = ev.GetType().GetProperty("pct")?.GetValue(ev) });
                        return Task.CompletedTask;   // drop node-start/done/error/result from the sub-graph
                    };

                    var result = await ExecuteGraphAsync(sub, subEmit, log, depth + 1, groupChain, runId, ct);
                    await log($"Run Group '{name}': {(result is null ? "(no video output)" : Path.GetFileName(result))}");
                    return result;
                }
                finally
                {
                    groupChain.Remove(name);
                }
            }
            case "Preview Save/save":
                await log($"Save: {(vidIn is null ? "(no input)" : Path.GetFileName(vidIn))}");
                return null;
            default:
                await log($"Unknown node '{n.type}' — skipped");
                return null;
        }
    }

    private async Task EnsureModelReady(string modelId, Func<string, Task> log, CancellationToken ct)
    {
        _activeModel.Set(modelId);
        var sw = await _coordinator.ActivateAsync(modelId, ct);
        if (sw.WasAlreadyUp) return;
        await log($"Starting backend {sw.Backend} (port {sw.Port})…");
        for (var i = 0; i < 60 && !PortListening(sw.Port); i++)
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
    }

    private static int Even(int v) => v % 2 == 0 ? v : v + 1;

    private static bool PortListening(int port)
    {
        foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (ep.Port == port) return true;
        return false;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class LGraph
    {
        public List<LNode> nodes { get; set; } = new();
        public List<JsonElement> links { get; set; } = new();
    }
    private sealed class LNode
    {
        public long id { get; set; }
        public string type { get; set; } = "";
        public List<LSlot>? inputs { get; set; }
        public List<JsonElement>? widgets_values { get; set; }
    }
    private sealed class LSlot
    {
        public string? name { get; set; }
        public long? link { get; set; }
    }
}
