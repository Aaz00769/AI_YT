using System.Net.Http.Json;
using System.Text.Json;
using AI_YOUTUBER.Functions.PLANNING;

namespace AI_YOUTUBER.Functions.MEMORY;

public static class VideoMemory
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private const string OllamaUrl = "http://localhost:11434/api/generate";
    private const string SummaryModel = "qwen3:8b";

    public static async Task SaveVideoSummaryAsync(
        EpisodeStrategyPlan strategy,
        string script,
        int targetMinutes)
    {
        Console.WriteLine("[VideoMemory] Creating video memory entry...");

        string videoId = CreateVideoId();

        string summary = await CreateShortSummaryAsync(strategy, script, targetMinutes);

        string videoFolder = GetVideoFolder(videoId);
        Directory.CreateDirectory(videoFolder);

        await File.WriteAllTextAsync(
            Path.Combine(videoFolder, "script.txt"),
            script
        );

        await File.WriteAllTextAsync(
            Path.Combine(videoFolder, "script_summary.md"),
            summary
        );

        await File.WriteAllTextAsync(
            Path.Combine(videoFolder, "strategy_plan.md"),
            CreateStrategyText(strategy, targetMinutes)
        );

        await AppendToChannelBrainAsync(videoId, strategy, summary, targetMinutes);

        Console.WriteLine($"[VideoMemory] Saved video memory: {videoId}");
    }

    private static async Task<string> CreateShortSummaryAsync(
        EpisodeStrategyPlan strategy,
        string script,
        int targetMinutes)
    {
        string prompt = $"""
        /no_think

        You are EX_01's memory summarizer.

        Create a SHORT memory summary of this finished video script.
        This summary will be added to the channel brain so future videos remember what happened.

        Do not rewrite the whole script.
        Do not include unnecessary detail.
        Focus on what future planning needs to know.

        VIDEO STRATEGY:
        Episode type: {strategy.EpisodeType}
        Topic: {strategy.Topic}
        Angle: {strategy.Angle}
        Hook: {strategy.Hook}
        Target viewer: {strategy.TargetViewer}
        Target length: {targetMinutes} minutes

        SCRIPT:
        {TrimForPrompt(script, 12000)}

        Return ONLY this format:

        SHORT_SUMMARY: 3-5 sentence summary of the video
        WHAT_THIS_VIDEO_TESTED: what content/format/angle this video tested
        RETENTION_NOTES: likely retention strengths or risks
        DO_NOT_REPEAT_TOO_SOON: topic/angle elements that should not be repeated too soon
        NEXT_VIDEO_HINT: one suggestion for what the next video could do differently
        """;

        var body = new
        {
            model = SummaryModel,
            prompt,
            stream = false,
            think = false,
            options = new
            {
                temperature = 0.25,
                num_ctx = 8192,
                num_predict = 700
            }
        };

        Console.WriteLine($"[VideoMemory] Asking summary model: {SummaryModel}");

        using HttpResponseMessage response = await Client.PostAsJsonAsync(OllamaUrl, body);

        string json = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"[VideoMemory] HTTP status: {(int)response.StatusCode} {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("response", out JsonElement responseElement))
        {
            Console.WriteLine("[VideoMemory] Summary model returned no response field.");
            return CreateFallbackSummary(strategy);
        }

        string summary = responseElement.GetString() ?? "";

        if (string.IsNullOrWhiteSpace(summary))
        {
            Console.WriteLine("[VideoMemory] Summary model returned empty summary.");
            return CreateFallbackSummary(strategy);
        }

        return summary.Trim();
    }

    private static async Task AppendToChannelBrainAsync(
        string videoId,
        EpisodeStrategyPlan strategy,
        string summary,
        int targetMinutes)
    {
        string brainPath = GetChannelBrainPath();

        Directory.CreateDirectory(Path.GetDirectoryName(brainPath)!);

        if (!File.Exists(brainPath))
        {
            await File.WriteAllTextAsync(brainPath, "# EX_01 Channel Brain\n\n");
        }

        string entry = $"""

        ---

        ## {videoId} Summary

        Date created: {DateTime.Now:yyyy-MM-dd HH:mm}
        Target length: {targetMinutes} minutes

        Episode type: {strategy.EpisodeType}
        Topic: {strategy.Topic}
        Angle: {strategy.Angle}
        Hook: {strategy.Hook}
        Target viewer: {strategy.TargetViewer}

        Research question:
        {strategy.ResearchQuestion}

        Search queries:
        {string.Join("\n", strategy.SearchQueries.Select(q => $"- {q}"))}

        Memory summary:
        {summary}

        Performance:
        Not uploaded yet. Add views, CTR, retention, comments, and notes later.

        """;

        await File.AppendAllTextAsync(brainPath, entry);

        Console.WriteLine("[VideoMemory] Appended summary to channel_brain.md");
    }

    private static string CreateStrategyText(EpisodeStrategyPlan strategy, int targetMinutes)
    {
        return $"""
        # Strategy Plan

        Target length: {targetMinutes} minutes

        Niche:
        {strategy.Niche}

        Episode type:
        {strategy.EpisodeType}

        Topic:
        {strategy.Topic}

        Angle:
        {strategy.Angle}

        Why this can work:
        {strategy.WhyThisCanWork}

        Target viewer:
        {strategy.TargetViewer}

        Hook:
        {strategy.Hook}

        Retention rules:
        {string.Join("\n", strategy.RetentionRules.Select(r => $"- {r}"))}

        Research question:
        {strategy.ResearchQuestion}

        Search queries:
        {string.Join("\n", strategy.SearchQueries.Select(q => $"- {q}"))}
        """;
    }

    private static string CreateFallbackSummary(EpisodeStrategyPlan strategy)
    {
        return $"""
        SHORT_SUMMARY: This video was about {strategy.Topic}. The angle was: {strategy.Angle}
        WHAT_THIS_VIDEO_TESTED: This tested the episode type "{strategy.EpisodeType}" and the hook "{strategy.Hook}".
        RETENTION_NOTES: Unknown until upload data is available.
        DO_NOT_REPEAT_TOO_SOON: Avoid repeating the exact same topic immediately.
        NEXT_VIDEO_HINT: Try a different episode type or a stronger contrast in the next video.
        """;
    }

    private static string CreateVideoId()
    {
        return $"VIDEO_{DateTime.Now:yyyyMMdd_HHmmss}";
    }

    private static string GetVideoFolder(string videoId)
    {
        return Path.GetFullPath(
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "../data/videos",
                videoId
            )
        );
    }

    private static string GetChannelBrainPath()
    {
        return Path.GetFullPath(
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "../data/channel/channel_brain.md"
            )
        );
    }

    private static string TrimForPrompt(string text, int maxCharacters)
    {
        if (text.Length <= maxCharacters)
            return text;

        return text[..maxCharacters] + "\n\n[TRUNCATED FOR MEMORY SUMMARY]";
    }
}