namespace AI_YOUTUBER.Models;

public sealed class VideoValidationRequest
{
    public string VideoPath { get; set; } = "";
    public string ScriptPath { get; set; } = "";
    public double RequestedDurationSeconds { get; set; }
    public double MinimumDurationRatio { get; set; } = 0.75;
    public double MaximumDurationRatio { get; set; } = 1.25;
    public double MaximumAudioVideoDifferenceSeconds { get; set; } = 1.0;
    public int AbsoluteMaximumDurationSeconds { get; set; } = 60;
    public long MinimumFileSizeBytes { get; set; } = 10_000;
    public IReadOnlyCollection<string> CompletedVideoHashes { get; set; } = Array.Empty<string>();
    public bool AllowLimitedValidationWithoutFfprobe { get; set; }
    public ShortScriptValidationResult? ScriptValidation { get; set; }
    public VoiceDurationValidationResult? VoiceDurationValidation { get; set; }
}

public sealed class ProductionValidationEvidence
{
    public ShortScriptValidationResult? ScriptValidation { get; set; }
    public bool TtsCompleted { get; set; }
    public VoiceDurationValidationResult? VoiceDurationValidation { get; set; }
    public bool RenderingCompleted { get; set; }
    public VideoValidationResult? VideoValidation { get; set; }
    public bool IsTestOrPreview { get; set; }
}
