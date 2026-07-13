namespace AI_YOUTUBER.Models;

public sealed class BatchManifest
{
    public string BatchId { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public string Status { get; set; } = BatchStatuses.Planned;
    public int RequestedVideoCount { get; set; }
    public string BatchTheme { get; set; } = "";
    public List<BatchVideoEntry> Videos { get; set; } = new();
}

public sealed class BatchVideoEntry
{
    public string VideoId { get; set; } = "";
    public int Position { get; set; }
    public string Topic { get; set; } = "";
    public string Title { get; set; } = "";
    public string LocalVideoPath { get; set; } = "";
    public string Status { get; set; } = BatchVideoStatuses.Planned;
    public string Error { get; set; } = "";
    public bool ValidationPassed { get; set; }
    public bool MemorySaved { get; set; }
    public int ScriptGenerationAttempts { get; set; }
    public ShortScriptValidationResult? ScriptValidation { get; set; }
    public VoiceDurationValidationResult? VoiceDurationValidation { get; set; }
    public VideoValidationResult? VideoValidation { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public List<string> StageHistory { get; set; } = new();
}

public static class BatchVideoStatuses
{
    public const string Planned = "planned";
    public const string GeneratingScript = "generating-script";
    public const string Scripted = "scripted";
    public const string GeneratingVoice = "generating-voice";
    public const string VoiceGenerated = "voice-generated";
    public const string PlanningScenes = "planning-scenes";
    public const string GeneratingImages = "generating-images";
    public const string Rendering = "rendering";
    public const string Rendered = "rendered";
    public const string Validating = "validating";
    public const string Validated = "validated";
    public const string MemorySaved = "memory-saved";
    public const string Completed = "completed";
    public const string Failed = "failed";
}

public static class BatchStatuses
{
    public const string Planned = "planned";
    public const string Running = "running";
    public const string PartiallyCompleted = "partially-completed";
    public const string Completed = "completed";
    public const string Failed = "failed";
}
