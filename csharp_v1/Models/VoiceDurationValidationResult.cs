namespace AI_YOUTUBER.Models;

public sealed class VoiceDurationValidationResult
{
    public bool Success { get; set; }
    public double ActualDurationSeconds { get; set; }
    public double RequestedDurationSeconds { get; set; }
    public double MinimumAcceptedDurationSeconds { get; set; }
    public double MaximumAcceptedDurationSeconds { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}
