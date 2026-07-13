using System.Text.Json.Serialization;

namespace AI_YOUTUBER.Models;

public enum VideoOrientation
{
    Portrait,
    Landscape
}

public sealed class ScriptValidationResult
{
    public bool Success { get; set; }
    public int WordCount { get; set; }
    public int MinimumWords { get; set; }
    public int MaximumWords { get; set; }
    public bool AppearsTruncated { get; set; }
    public bool ReachedOutputTokenLimit { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class ScriptGenerationResult
{
    public string Script { get; set; } = "";
    public int AttemptCount { get; set; }
    public int MaximumAttempts { get; set; }
    public TimeSpan Elapsed { get; set; }
    public ScriptValidationResult Validation { get; set; } = new();
    public OllamaGenerationResult? Generation { get; set; }
}

public sealed class VoiceDurationValidationResult
{
    public bool Success { get; set; }
    public double ActualDurationSeconds { get; set; }
    public double RequestedDurationSeconds { get; set; }
    public double MinimumAcceptedDurationSeconds { get; set; }
    public double MaximumAcceptedDurationSeconds { get; set; }
    public List<string> Errors { get; set; } = new();
}

public sealed class VideoValidationRequest
{
    public string VideoPath { get; set; } = "";
    public string ScriptPath { get; set; } = "";
    public double RequestedDurationSeconds { get; set; }
    public double MinimumDurationRatio { get; set; } = 0.75;
    public double MaximumDurationRatio { get; set; } = 1.25;
    public double MaximumAudioVideoDifferenceSeconds { get; set; } = 1.5;
    public long MinimumFileSizeBytes { get; set; } = 10_000;
    public VideoOrientation Orientation { get; set; }
    public ScriptValidationResult? ScriptValidation { get; set; }
    public VoiceDurationValidationResult? VoiceDurationValidation { get; set; }
}

public sealed class VideoValidationResult
{
    public bool Success { get; set; }
    public bool FullValidationPerformed { get; set; }
    public List<string> ChecksPassed { get; set; } = new();
    public List<string> Errors { get; set; } = new();
    public double DurationSeconds { get; set; }
    public double RequestedDurationSeconds { get; set; }
    public double MinimumAcceptedDurationSeconds { get; set; }
    public double MaximumAcceptedDurationSeconds { get; set; }
    public double AudioDurationSeconds { get; set; }
    public double AudioVideoDurationDifferenceSeconds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool HasVideo { get; set; }
    public bool HasAudio { get; set; }
    public bool ScriptValidationPassed { get; set; }
    public bool VoiceDurationValidationPassed { get; set; }
    public long FileSizeBytes { get; set; }
    public string FileHash { get; set; } = "";
}

public sealed class ProductionValidationEvidence
{
    public ScriptValidationResult? ScriptValidation { get; set; }
    public bool TtsCompleted { get; set; }
    public VoiceDurationValidationResult? VoiceDurationValidation { get; set; }
    public bool RenderingCompleted { get; set; }
    public VideoValidationResult? VideoValidation { get; set; }
    public bool IsTestOrPreview { get; set; }
    public bool UserApprovedOfficialMemory { get; set; }
}

public sealed class RunMetrics
{
    public string VideoId { get; set; } = "";
    public string PromptVersion { get; set; } = "";
    public string ScriptModel { get; set; } = "";
    public int TargetSeconds { get; set; }
    public int WordCount { get; set; }
    public int ScriptGenerationAttempts { get; set; }
    public double ScriptGenerationSeconds { get; set; }
    public double TtsSeconds { get; set; }
    public double VoiceDurationSeconds { get; set; }
    public double VisualRenderSeconds { get; set; }
    public double VideoRenderSeconds { get; set; }
    public double VideoDurationSeconds { get; set; }
    public string ScriptHash { get; set; } = "";
    public string VideoHash { get; set; } = "";
    public string FailureStage { get; set; } = "";
    public bool UserApprovedScript { get; set; }
    public bool UserApprovedOfficialMemory { get; set; }
    public DateTime StartedUtc { get; set; }
    public DateTime CompletedUtc { get; set; }
}

public sealed class ProductionResult
{
    public bool Success { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string ScriptPath { get; set; } = "";
    public string VoicePath { get; set; } = "";
    public string VideoPath { get; set; } = "";
    public string MetricsPath { get; set; } = "";
    public VoiceDurationValidationResult? VoiceValidation { get; set; }
    public VideoValidationResult? VideoValidation { get; set; }
    public RunMetrics Metrics { get; set; } = new();
}

public sealed class OllamaGenerateRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("think")]
    public bool Think { get; set; }

    [JsonPropertyName("options")]
    public OllamaGenerateOptions Options { get; set; } = new();
}

public sealed class OllamaGenerateOptions
{
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("num_ctx")]
    public int NumContextTokens { get; set; }

    [JsonPropertyName("num_predict")]
    public int MaximumOutputTokens { get; set; }
}

public sealed class OllamaGenerationResult
{
    public string Text { get; set; } = "";
    public bool Completed { get; set; }
    public string DoneReason { get; set; } = "";
    public int OutputTokenCount { get; set; }
    public int MaximumOutputTokens { get; set; }
    public bool ReachedOutputTokenLimit { get; set; }
}
