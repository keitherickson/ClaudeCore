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
    private readonly VideoDefaultsOptions _defaults;
    private readonly ILogger<GraphExecutor> _log;

    public GraphExecutor(
        LtxVideoService ltx, ActiveModelStore activeModel, VideoBackendCoordinator coordinator,
        MaxineUpscaleService maxine, ComfyUiUpscaleService aiUpscale, VideoSpeedService speed,
        SoundGenService sound, IOptions<VideoDefaultsOptions> defaults, ILogger<GraphExecutor> log)
    {
        _ltx = ltx; _activeModel = activeModel; _coordinator = coordinator;
        _maxine = maxine; _aiUpscale = aiUpscale; _speed = speed; _sound = sound;
        _defaults = defaults.Value; _log = log;
    }

    /// <summary>Runs the graph, calling <paramref name="emit"/> for each event.</summary>
    public async Task RunAsync(JsonElement graphJson, Func<object, Task> emit, CancellationToken ct)
    {
        Task Log(string t) => emit(new { type = "log", text = t });
        try
        {
            var graph = graphJson.Deserialize<LGraph>(Web) ?? new LGraph();
            var linkMap = new Dictionary<long, (long node, int slot)>();
            foreach (var l in graph.links)
                if (l.ValueKind == JsonValueKind.Array && l.GetArrayLength() >= 4)
                    linkMap[l[0].GetInt64()] = (l[1].GetInt64(), l[2].GetInt32());

            var outputs = new Dictionary<long, string>();
            var done = new HashSet<long>();
            string? finalVideo = null;

            while (done.Count < graph.nodes.Count)
            {
                var progressed = false;
                foreach (var n in graph.nodes)
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
                        var produced = await ExecuteNode(n, inputs, emit, Log, ct);
                        if (produced is not null) outputs[n.id] = produced;
                        if (n.type == "keithui/save" && inputs.TryGetValue("video", out var fv)) finalVideo = fv;
                        await emit(new { type = "node-done", node = n.id });
                    }
                    catch (Exception ex)
                    {
                        await emit(new { type = "node-error", node = n.id, error = ex.Message });
                        await Log("ERROR @ node " + n.id + ": " + ex.Message);
                        await emit(new { type = "done", finalVideo = (string?)null, ok = false });
                        return;
                    }
                    done.Add(n.id);
                    progressed = true;
                }
                if (!progressed) { await Log("Stopped: a node has unmet inputs (cycle or missing connection)."); break; }
            }

            finalVideo ??= outputs.Values.LastOrDefault();
            await emit(new { type = "done", finalVideo, ok = true });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Graph execution failed");
            await emit(new { type = "done", finalVideo = (string?)null, ok = false, error = ex.Message });
        }
    }

    private async Task<string?> ExecuteNode(LNode n, Dictionary<string, string> inputs, Func<object, Task> emit, Func<string, Task> log, CancellationToken ct)
    {
        var w = n.widgets_values ?? new();
        string Str(int i, string d = "") => i < w.Count && w[i].ValueKind == JsonValueKind.String ? w[i].GetString() ?? d : d;
        int Num(int i, int d) => i < w.Count && w[i].ValueKind == JsonValueKind.Number ? w[i].GetInt32() : d;
        inputs.TryGetValue("image", out var imgIn);
        inputs.TryGetValue("audio", out var audIn);
        inputs.TryGetValue("video", out var vidIn);

        switch (n.type)
        {
            case "keithui/load_image":
            {
                var file = Str(0);
                await log($"Load Image: {(string.IsNullOrWhiteSpace(file) ? "(none)" : Path.GetFileName(file))}");
                return string.IsNullOrWhiteSpace(file) || !File.Exists(file) ? null : file;
            }
            case "keithui/sound":
            {
                var staged = await _sound.GenerateAsync(Str(0), Num(1, 5), ct);
                await log($"Generate Sound: '{Str(0)}' -> {Path.GetFileName(staged.Path)}");
                return staged.Path;
            }
            case "keithui/generate":
            {
                var model = Str(0, "bf16-2.3");
                await EnsureModelReady(model, log, ct);
                var req = new GenerateVideoRequest
                {
                    Prompt = Str(1),
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
            case "keithui/upscale":
            {
                if (vidIn is null) { await log("Upscale: no video input — skipped"); return null; }
                var engine = Str(0, "ai");
                if (engine == "ai")
                {
                    var th = int.TryParse(Str(1, "1080"), out var v) ? v : 1080;
                    var dims = VideoProbe.TryGetDimensions(vidIn);
                    int tw = dims is { } d ? (int)Math.Round((double)d.Width / d.Height * th) : th * 16 / 9;
                    tw = Even(Math.Max(2, tw)); th = Even(th);
                    var name = $"studio_{Guid.NewGuid():N}{Path.GetExtension(vidIn)}";
                    File.Copy(vidIn, Path.Combine(_aiUpscale.InputDirectory, name), true);
                    var r = await _aiUpscale.UpscaleAsync(name, tw, th, ct);
                    await log($"Upscale [AI]: -> {r.Width}x{r.Height} {Path.GetFileName(r.SavedPath)}");
                    return r.SavedPath;
                }
                else
                {
                    var factor = int.TryParse(Str(2, "2"), out var f) && f is 2 or 3 or 4 ? f : 2;
                    var input = await _speed.EnsureH264Async(vidIn, ct);
                    var srcH = VideoProbe.TryGetHeight(input) ?? 1080;
                    var r = await _maxine.UpscaleAsync(input, "SuperRes", srcH * factor, 1, 0f, ct);
                    await _speed.RestoreAudioAsync(r.SavedPath, vidIn, ct);
                    if (!string.Equals(input, vidIn, StringComparison.OrdinalIgnoreCase))
                        try { File.Delete(input); } catch { /* best effort */ }
                    await log($"Upscale [Maxine {factor}x]: {Path.GetFileName(r.SavedPath)}");
                    return r.SavedPath;
                }
            }
            case "keithui/speed":
            {
                if (vidIn is null) { await log("Speed Up: no video input — skipped"); return null; }
                var factor = double.TryParse(Str(0, "2"), out var sp) ? sp : 2.0;
                var r = await _speed.RetimeAsync(vidIn, factor, ct);
                await log($"Speed Up [{factor}x]: {Path.GetFileName(r.SavedPath)}");
                return r.SavedPath;
            }
            case "keithui/save":
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
