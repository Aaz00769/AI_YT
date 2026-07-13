namespace AI_YOUTUBER.Models;

public sealed class ShortBatchPlan
{
    public string BatchId { get; set; } = "";
    public string BatchTheme { get; set; } = "";
    public string OverallGoal { get; set; } = "";
    public List<PlannedShort> Videos { get; set; } = new();
}

public sealed class PlannedShort
{
    public int Position { get; set; }
    public string WorkingTitle { get; set; } = "";
    public string Topic { get; set; } = "";
    public string Hook { get; set; } = "";
    public string PurposeInBatch { get; set; } = "";
    public string KeyDifferenceFromOtherVideos { get; set; } = "";
    public List<string> RequiredPoints { get; set; } = new();
    public List<string> AvoidRepeating { get; set; } = new();
    public string SuggestedCallback { get; set; } = "";
}
