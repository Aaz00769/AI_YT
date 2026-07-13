namespace AI_YOUTUBER.Models;

public sealed class VideoMemoryRecord
{
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Topic { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string VideoPath { get; set; } = "";
    public string ScriptPath { get; set; } = "";
    public string Summary { get; set; } = "";
    public List<string> KeyPoints { get; set; } = new();
    public List<string> Ex01Opinions { get; set; } = new();
    public List<string> EventsAndExperiments { get; set; } = new();
    public List<string> JokesAndLore { get; set; } = new();
    public List<string> PromisesAndCallbacks { get; set; } = new();
    public List<string> UnresolvedQuestions { get; set; } = new();
    public List<string> Keywords { get; set; } = new();
    public string CompactScriptExcerpt { get; set; } = "";
    public string ScriptHash { get; set; } = "";
}

public sealed class ChannelMemoryState
{
    public string ChannelSummary { get; set; } = "";
    public List<string> RecurringLore { get; set; } = new();
    public List<string> ActiveProjects { get; set; } = new();
    public List<string> UnresolvedPromises { get; set; } = new();
    public List<string> KnownHardware { get; set; } = new();
    public DateTime UpdatedUtc { get; set; }
}

public sealed class MemoryContext
{
    public List<VideoMemoryRecord> RecentVideos { get; set; } = new();
    public List<VideoMemoryRecord> RelevantVideos { get; set; } = new();
    public ChannelMemoryState ChannelState { get; set; } = new();
    public string FormattedContext { get; set; } = "";
}

public sealed class MemoryExtractionResult
{
    public string Summary { get; set; } = "";
    public List<string> KeyPoints { get; set; } = new();
    public List<string> Ex01Opinions { get; set; } = new();
    public List<string> EventsAndExperiments { get; set; } = new();
    public List<string> JokesAndLore { get; set; } = new();
    public List<string> PromisesAndCallbacks { get; set; } = new();
    public List<string> UnresolvedQuestions { get; set; } = new();
    public List<string> Keywords { get; set; } = new();
    public string CompactScriptExcerpt { get; set; } = "";
}
