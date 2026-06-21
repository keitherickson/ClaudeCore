using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ClaudeCore.Services;

/// <summary>Result of a completed AI upscale, after the video is saved to the output folder.</summary>
public sealed record ComfyUpscaleResult(string FileName, string SavedPath, int Width, int Height);

/// <summary>
/// AI video upscaling via ComfyUI to an arbitrary target resolution (the alternative to
/// <see cref="MaxineUpscaleService"/>, which is ratio-locked). Stages the source into
/// ComfyUI's input dir, ensures the shared ComfyUI server is up, then runs the validated
/// graph: LoadVideo → GetVideoComponents → ESRGAN upscale → ImageScale to the exact
/// target → CreateVideo (re-attaching the source fps + audio) → SaveVideo. ComfyUI
/// decodes any input codec and preserves audio, so unlike the Maxine path there's no
/// H.264 pre-transcode or audio re-mux.
/// </summary>
public sealed class ComfyUiUpscaleService
{
    private readonly HttpClient _http;
    private readonly ComfyUiUpscaleOptions _o;
    private readonly ComfyUiServerControl _comfy;
    private readonly ILogger<ComfyUiUpscaleService> _log;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ComfyUiUpscaleService(
        HttpClient http, IOptions<ComfyUiUpscaleOptions> o, ComfyUiServerControl comfy,
        ILogger<ComfyUiUpscaleService> log)
    {
        _http = http;
        _o = o.Value;
        _comfy = comfy;
        _log = log;
    }

    public string OutputDirectory => _o.OutputDirectory;
    public string InputDirectory => _o.InputDirectory;

    /// <summary>True if the upscale model file exists (the only AI-specific prerequisite).</summary>
    public bool IsReady(out string? problem)
    {
        var modelPath = Path.Combine(@"C:\ComfyUI\ComfyUI\models\upscale_models", _o.Model);
        if (!File.Exists(modelPath)) { problem = $"Upscale model not found: {modelPath}"; return false; }
        problem = null;
        return true;
    }

    /// <summary>Saves the uploaded source into ComfyUI's input dir; returns the bare file name for LoadVideo.</summary>
    public async Task<string> StageInputAsync(IFormFile video, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_o.InputDirectory);
        var ext = Path.GetExtension(video.FileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".mp4";
        var name = $"upscale_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}";
        var path = Path.Combine(_o.InputDirectory, name);
        await using var fs = File.Create(path);
        await video.CopyToAsync(fs, ct);
        return name;
    }

    /// <summary>
    /// Upscales the staged clip (a bare file name already in ComfyUI's input dir) to the
    /// exact <paramref name="width"/>×<paramref name="height"/> and saves it to the output dir.
    /// </summary>
    public async Task<ComfyUpscaleResult> UpscaleAsync(string inputFileName, int width, int height, CancellationToken ct = default)
    {
        await EnsureComfyUpAsync(ct);

        var graph = BuildGraph(inputFileName, width, height);
        using var submit = await _http.PostAsJsonAsync("/prompt", new { prompt = graph, client_id = "ccupscale" }, Json, ct);
        if (!submit.IsSuccessStatusCode)
            throw new InvalidOperationException($"ComfyUI rejected the upscale graph: {await submit.Content.ReadAsStringAsync(ct)}");
        var sj = await submit.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
        var promptId = sj.TryGetProperty("prompt_id", out var pid) ? pid.GetString() : null;
        if (string.IsNullOrEmpty(promptId)) throw new InvalidOperationException("ComfyUI did not return a prompt_id.");
        _log.LogInformation("ComfyUI upscale {Id}: {File} → {W}x{H}", promptId, inputFileName, width, height);

        var deadline = DateTime.UtcNow.AddMinutes(_o.TimeoutMinutes);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException("ComfyUI upscale timed out.");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            using var hist = await _http.GetAsync($"/history/{promptId}", ct);
            if (!hist.IsSuccessStatusCode) continue;
            var hj = await hist.Content.ReadFromJsonAsync<JsonElement>(Json, ct);
            if (!hj.TryGetProperty(promptId, out var entry)) continue;

            var statusStr = entry.TryGetProperty("status", out var st) && st.TryGetProperty("status_str", out var ss) ? ss.GetString() : null;
            if (statusStr == "error") throw new InvalidOperationException("ComfyUI upscale failed (execution error).");

            var outFile = FindOutput(entry);
            if (outFile is null)
            {
                if (statusStr == "success") throw new InvalidOperationException("ComfyUI upscale finished with no output.");
                continue;
            }
            return await DownloadAsync(outFile.Value, width, height, ct);
        }
    }

    private async Task EnsureComfyUpAsync(CancellationToken ct)
    {
        if (_comfy.IsPortListening()) return;
        _log.LogInformation("Upscale needs ComfyUI; starting it (port {Port})", _comfy.Port);
        if (!_comfy.StartDetached())
            throw new InvalidOperationException("Could not start ComfyUI for upscaling (run-comfyui.ps1 missing?).");
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            if (_comfy.IsPortListening()) return;
        }
        throw new InvalidOperationException("ComfyUI did not come up within the timeout.");
    }

    /// <summary>Builds the validated upscale graph (mirrors tools/run_val_upscale.py).</summary>
    private Dictionary<string, object> BuildGraph(string inputFileName, int width, int height)
    {
        static Dictionary<string, object> Node(string c, object i) => new() { ["class_type"] = c, ["inputs"] = i };
        static object[] Link(string n, int s) => new object[] { n, s };

        return new Dictionary<string, object>
        {
            ["1"] = Node("LoadVideo", new { file = inputFileName }),
            ["2"] = Node("GetVideoComponents", new { video = Link("1", 0) }),               // images[0], audio[1], fps[2]
            ["3"] = Node("UpscaleModelLoader", new { model_name = _o.Model }),
            ["4"] = Node("ImageUpscaleWithModel", new { upscale_model = Link("3", 0), image = Link("2", 0) }),
            ["5"] = Node("ImageScale", new { image = Link("4", 0), upscale_method = _o.UpscaleMethod, width, height, crop = "disabled" }),
            ["6"] = Node("CreateVideo", new { images = Link("5", 0), fps = Link("2", 2), audio = Link("2", 1) }),
            ["7"] = Node("SaveVideo", new { video = Link("6", 0), filename_prefix = "upscaled/comfyui_up", format = "auto", codec = "auto" }),
        };
    }

    private readonly record struct OutputRef(string Filename, string Subfolder, string Type);

    private static OutputRef? FindOutput(JsonElement entry)
    {
        if (!entry.TryGetProperty("outputs", out var outputs)) return null;
        foreach (var node in outputs.EnumerateObject())
            foreach (var arrName in new[] { "images", "gifs", "videos" })
                if (node.Value.TryGetProperty(arrName, out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var item in arr.EnumerateArray())
                    {
                        var fn = item.TryGetProperty("filename", out var f) ? f.GetString() : null;
                        if (string.IsNullOrEmpty(fn)) continue;
                        return new OutputRef(fn,
                            item.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "",
                            item.TryGetProperty("type", out var t) ? t.GetString() ?? "output" : "output");
                    }
        return null;
    }

    private async Task<ComfyUpscaleResult> DownloadAsync(OutputRef o, int width, int height, CancellationToken ct)
    {
        var url = $"/view?filename={Uri.EscapeDataString(o.Filename)}&subfolder={Uri.EscapeDataString(o.Subfolder)}&type={Uri.EscapeDataString(o.Type)}";
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

        Directory.CreateDirectory(_o.OutputDirectory);
        var fileName = $"aiupscaled_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}.mp4";
        var dest = Path.Combine(_o.OutputDirectory, fileName);
        await File.WriteAllBytesAsync(dest, bytes, ct);
        _log.LogInformation("AI-upscaled video saved to {Dest}", dest);
        return new ComfyUpscaleResult(fileName, dest, width, height);
    }
}
