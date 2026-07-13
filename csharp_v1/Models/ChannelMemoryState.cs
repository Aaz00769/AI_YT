namespace AI_YOUTUBER.Models;

public sealed class ChannelMemoryState
{
    public string ChannelSummary { get; set; } = "";
    public List<string> RecurringLore { get; set; } = new();
    public List<string> ActiveProjects { get; set; } = new();
    public List<string> UnresolvedPromises { get; set; } = new();
    public List<string> KnownHardware { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}
