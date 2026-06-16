using System.Text.Json.Serialization;

namespace ClaudeCore.Models.SoundGen;

/// <summary>Body for POST /generate on the local Stable Audio server.</summary>
public sealed class LocalAudioRequest
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";

    /// <summary>Requested length in seconds, or null to let the server choose.</summary>
    [JsonPropertyName("seconds")] public double? Seconds { get; set; }
}

/// <summary>A generated WAV written into the staging dir, ready for audio-to-video.</summary>
public sealed record StagedAudio(string FileName, string Path, string Name);
