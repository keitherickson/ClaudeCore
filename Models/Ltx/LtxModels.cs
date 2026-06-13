using System.Text.Json.Serialization;

namespace ClaudeCore.Models.Ltx;

/// <summary>
/// Body for POST /api/generate. Field names mirror the LTX backend's
/// GenerateVideoRequest pydantic model exactly (it uses strict typing).
/// </summary>
public sealed class GenerateVideoRequest
{
    [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";

    /// <summary>Local pipeline supports "540p", "720p", "1080p".</summary>
    [JsonPropertyName("resolution")] public string Resolution { get; set; } = "1080p";

    /// <summary>"fast" or "pro". Local generation always uses the fast distilled pipeline.</summary>
    [JsonPropertyName("model")] public string Model { get; set; } = "fast";

    [JsonPropertyName("cameraMotion")] public string CameraMotion { get; set; } = "none";
    [JsonPropertyName("negativePrompt")] public string NegativePrompt { get; set; } = "";

    /// <summary>One of 5, 6, 8, 10, 12, 14, 16, 18, 20 (seconds).</summary>
    [JsonPropertyName("duration")] public int Duration { get; set; } = 5;

    /// <summary>One of 24, 25, 48, 50.</summary>
    [JsonPropertyName("fps")] public int Fps { get; set; } = 24;

    [JsonPropertyName("audio")] public bool Audio { get; set; }

    /// <summary>Absolute server-readable path to the conditioning image (image-to-video). Null = text-to-video.</summary>
    [JsonPropertyName("imagePath")] public string? ImagePath { get; set; }

    [JsonPropertyName("audioPath")] public string? AudioPath { get; set; }

    /// <summary>"16:9" or "9:16".</summary>
    [JsonPropertyName("aspectRatio")] public string AspectRatio { get; set; } = "16:9";
}

/// <summary>Response from POST /api/generate.</summary>
public sealed class GenerateVideoResponse
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";

    /// <summary>Absolute path to the generated .mp4 in the server's outputs dir (only on status == "complete").</summary>
    [JsonPropertyName("video_path")] public string? VideoPath { get; set; }
}

/// <summary>Response from GET /api/generation/progress (polled during a generation).</summary>
public sealed class GenerationProgress
{
    [JsonPropertyName("status")] public string Status { get; set; } = "idle";
    [JsonPropertyName("phase")] public string Phase { get; set; } = "";
    [JsonPropertyName("progress")] public int Progress { get; set; }
    [JsonPropertyName("currentStep")] public int? CurrentStep { get; set; }
    [JsonPropertyName("totalSteps")] public int? TotalSteps { get; set; }
}

/// <summary>Subset of GET /health used for a connectivity/model-status check.</summary>
public sealed class LtxHealth
{
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("models_loaded")] public bool ModelsLoaded { get; set; }
    [JsonPropertyName("active_model")] public string? ActiveModel { get; set; }
}
