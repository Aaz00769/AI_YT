namespace AI_YOUTUBER.Models;

public sealed class BatchShortMetadata
{
    public string BatchId { get; set; } = "";
    public string VideoId { get; set; } = "";
    public int Position { get; set; }
    public string Title { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Hook { get; set; } = "";
    public double AudioDurationSeconds { get; set; }
    public DateTime UpdatedUtc { get; set; }
    public List<string> RequiredPoints { get; set; } = new();
    public List<string> AvoidRepeating { get; set; } = new();
}
