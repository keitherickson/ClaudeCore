using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using KeithVision.Models.Ltx;
using KeithVision.Services;
using Microsoft.Extensions.Options;

namespace KeithUI.Services;

public sealed record ExecResult(bool Ok, string? FinalVideo, IReadOnlyList<string> Log, string? Error);

/// <summary>
/// Executes a serialized LiteGraph graph by walking it and calling the reused
/// KeithVision.Core services — generate / sound / upscale / speed. Each node produces
/// a file (image/audio/video) that is handed to downstream nodes along the edges.
/// Synchronous: blocks until the whole graph finishes (video gen takes minutes).
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
    private readonly VideoModelsOptions _models;
    private readonly ILogger<GraphExecutor> _log;

    public GraphExecutor(
        LtxVideoService ltx, ActiveModelStore activeModel, VideoBackendCoordinator coordinator,
        MaxineUpscaleService maxine, ComfyUiUpscaleService aiUpscale, VideoSpeedService speed,
        SoundGenService sound, IOptions<VideoDefaultsOptions> defaults, IOptions<VideoModelsOptions> models,
        ILogger<GraphExecutor> log)
    {
        _ltx = ltx; _activeModel = activeModel; _coordinator = coordinator;
        _maxine = maxine; _aiUpscale = aiUpscale; _speed = speed; _sound = sound;
        _defaults = defaults.Value; _models = models.Value; _log = log;
    }

    public async Task<ExecResult> RunAsync(JsonElement graphJson, CancellationToken ct)
    {
        var log = new List<string>();
        try
        {
            var graph = graphJson.Deserialize<LGraph>(Web) ?? new LGraph();
            // linkId -> (originNodeId, originSlot)
            var linkMap = new Dictionary<long, (long node, int slot)>();
            foreach (var l in graph.links)
                if (l.ValueKind == JsonValueKind.Array && l.GetArrayLength() >= 4)
                    linkMap[l[0].GetInt64()] = (l[1].GetInt64(), l[2].GetInt32());

            var outputs = new Dictionary<long, string>();   // nodeId -> produced file path
            var done = new HashSet<long>();
            string? finalVideo = null;

            // Fixed-point walk: run any node whose linked inputs are all produced.
            while (done.Count < graph.nodes.Count)
            {
                var progressed = false;
                foreach (var n in graph.nodes)
                {
                    if (done.Contains(n.id)) continue;
                    var inputs = new Dictionary<string, string>();
                    var ready = true;
                    foreach (var slot in n.inputs ?? new())
                    {
                        if (slot.link is long lid && linkMap.TryGetValue(lid, out var src))
                        {
                            if (!outputs.TryGetValue(src.node, out var p)) { ready = false; break; }
                            inputs[slot.name ?? ""] = p;
                        }
                    }
                    if (!ready) continue;

                    var produced = await ExecuteNode(n, inputs, log, ct);
                    if (produced is not null) outputs[n.id] = produced;
                    if (n.type == "keithui/save" && inputs.TryGetValue("video", out var fv)) finalVideo = fv;
                    done.Add(n.id);
                    progressed = true;
                }
                if (!progressed) { log.Add("Stopped: a node has unmet inputs (cycle or missing connection)."); break; }
            }

            finalVideo ??= outputs.Values.LastOrDefault();
            return new ExecResult(true, finalVideo, log, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Graph execution failed");
            log.Add("ERROR: " + ex.Message);
            return new ExecResult(false, null, log, ex.Message);
        }
    }

    private async Task<string?> ExecuteNode(LNode n, Dictionary<string, string> inputs, List<string> log, CancellationToken ct)
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
                log.Add($"Load Image: {(string.IsNullOrWhiteSpace(file) ? "(none)" : file)}");
                return string.IsNullOrWhiteSpace(file) || !File.Exists(file) ? null : file;
            }
            case "keithui/sound":
            {
                var staged = await _sound.GenerateAsync(Str(0), Num(1, 5), ct);
                log.Add($"Generate Sound: '{Str(0)}' -> {Path.GetFileName(staged.Path)}");
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
                var r = await _ltx.GenerateAsync(req, ct);
                log.Add($"Generate Video [{model}]: {Path.GetFileName(r.SavedPath)}");
                return r.SavedPath;
            }
            case "keithui/upscale":
            {
                if (vidIn is null) { log.Add("Upscale: no video input — skipped"); return null; }
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
                    log.Add($"Upscale [AI]: -> {r.Width}x{r.Height} {Path.GetFileName(r.SavedPath)}");
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
                    log.Add($"Upscale [Maxine {factor}x]: {Path.GetFileName(r.SavedPath)}");
                    return r.SavedPath;
                }
            }
            case "keithui/speed":
            {
                if (vidIn is null) { log.Add("Speed Up: no video input — skipped"); return null; }
                var factor = double.TryParse(Str(0, "2"), out var sp) ? sp : 2.0;
                var r = await _speed.RetimeAsync(vidIn, factor, ct);
                log.Add($"Speed Up [{factor}x]: {Path.GetFileName(r.SavedPath)}");
                return r.SavedPath;
            }
            case "keithui/save":
                log.Add($"Save: {(vidIn is null ? "(no input)" : Path.GetFileName(vidIn))}");
                return null;
            default:
                log.Add($"Unknown node '{n.type}' — skipped");
                return null;
        }
    }

    /// <summary>Switches the active model + brings its backend up, then waits for the port to listen.</summary>
    private async Task EnsureModelReady(string modelId, List<string> log, CancellationToken ct)
    {
        _activeModel.Set(modelId);
        var sw = await _coordinator.ActivateAsync(modelId, ct);
        if (sw.WasAlreadyUp) return;
        log.Add($"Starting backend {sw.Backend} (port {sw.Port})…");
        for (var i = 0; i < 60 && !PortListening(sw.Port); i++)
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        // ComfyUI binds the port well before models load; first generation loads them.
    }

    private static int Even(int v) => v % 2 == 0 ? v : v + 1;

    private static bool PortListening(int port)
    {
        foreach (var ep in IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            if (ep.Port == port) return true;
        return false;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // --- LiteGraph serialize() shape ---------------------------------------
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
