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
