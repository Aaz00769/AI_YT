namespace AI_YOUTUBER.Models;

public sealed class VideoValidationResult
{
    public bool Success { get; set; }
    public bool FullValidationPerformed { get; set; }
    public List<string> ChecksPassed { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
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
