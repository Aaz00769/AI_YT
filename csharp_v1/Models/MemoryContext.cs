namespace AI_YOUTUBER.Models;

public sealed class MemoryContext
{
    public List<VideoMemoryRecord> RecentVideos { get; init; } = new();
    public List<VideoMemoryRecord> RelevantVideos { get; init; } = new();
    public List<string> RelevantUnresolvedPromises { get; init; } = new();
    public ChannelMemoryState ChannelState { get; init; } = new();
    public string FormattedContext { get; init; } = "";
}
