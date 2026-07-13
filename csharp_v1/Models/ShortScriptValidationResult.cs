namespace AI_YOUTUBER.Models;

public sealed class ShortScriptValidationResult
{
    public bool Success { get; set; }
    public int WordCount { get; set; }
    public int MinimumWords { get; set; }
    public int MaximumWords { get; set; }
    public bool AppearsTruncated { get; set; }
    public bool ReachedOutputTokenLimit { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public sealed class ShortScriptGenerationResult
{
    public string Script { get; set; } = "";
    public int AttemptCount { get; set; }
    public int MaximumAttempts { get; set; }
    public ShortScriptValidationResult Validation { get; set; } = new();
    public OllamaGenerationResult? Generation { get; set; }
}
