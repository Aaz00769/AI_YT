using System.Net.Http.Json;
using System.Text.Json;

namespace AI_YOUTUBER.Functions.ASKING;

public static class VideoManagerAI
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private const string OllamaUrl = "http://localhost:11434/api/generate";
    private const string ManagerModel = "qwen3:8b";

    public static async Task<VideoResearchPlan> CreatePlanAsync(int targetMinutes)
    {
        string prompt = $"""
        You are EX_01's video topic manager.

        EX_01 is an AI VTuber created by Anton.

        Channel identity:
        - local AI YouTuber
        - cursed cheap hardware
        - old/pre-2020 AI lab
        - sarcastic commentary
        - criticizes soulless AI trends
        - talks about tech, AI, YouTube, creators, automation, and weird internet trends

        Target video length:
        {targetMinutes} minutes.

        Your job:
        Pick one clear video topic and create clean web search queries for research.

        Search query rules:
        - Search queries are for a search engine.
        - Do NOT include EX_01.
        - Do NOT include Anton.
        - Do NOT include "roast".
        - Do NOT include the full video prompt.
        - Do NOT make long question-style queries.
        - Make short, realistic search queries.

        Good query examples:
        - AI-generated YouTube videos trend 2026
        - faceless AI YouTube channels 2026
        - AI video tools creators use 2026
        - YouTube synthetic influencers trend
        - AI Shorts automation trend 2026

        Return ONLY plain text in this exact format:

        TOPIC: short topic name
        ANGLE: the commentary angle
        RESEARCH_QUESTION: what the research should answer
        SEARCH_QUERY: query 1
        SEARCH_QUERY: query 2
        SEARCH_QUERY: query 3
        SEARCH_QUERY: query 4
        SEARCH_QUERY: query 5

        Rules:
        - Do not return JSON.
        - Do not use markdown.
        - Do not add explanations.
        - Do not write your reasoning.
        - Do not include numbered lists.
        - Output exactly one TOPIC line.
        - Output exactly one ANGLE line.
        - Output exactly one RESEARCH_QUESTION line.
        - Output exactly five SEARCH_QUERY lines.
        """;

        try
        {
            string raw = await AskOllamaAsync(prompt);

            Console.WriteLine("[VideoManager] Raw manager output:");
            Console.WriteLine("---------- MANAGER OUTPUT START ----------");
            Console.WriteLine(raw);
            Console.WriteLine("---------- MANAGER OUTPUT END ------------");

            if (string.IsNullOrWhiteSpace(raw))
            {
                Console.WriteLine("[VideoManager] ERROR: Manager returned empty output.");
                return GetFallbackPlan();
            }

            VideoResearchPlan? plan = TryParsePlan(raw);

            if (plan is not null && plan.SearchQueries.Count >= 3)
            {
                Console.WriteLine("[VideoManager] Plan parsed successfully.");
                PrintPlan(plan);
                return plan;
            }

            Console.WriteLine("[VideoManager] Failed to parse enough search queries. Using fallback plan.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[VideoManager] Manager model failed. Using fallback plan.");
            Console.WriteLine(ex.Message);
        }

        VideoResearchPlan fallback = GetFallbackPlan();

        Console.WriteLine("[VideoManager] Fallback plan:");
        PrintPlan(fallback);

        return fallback;
    }

    private static async Task<string> AskOllamaAsync(string prompt)
    {
        var body = new
        {
            model = ManagerModel,

            // Qwen3 tends to waste output tokens on thinking.
            // /no_think reduces that. think=false helps if your Ollama version supports it.
            prompt = "/no_think\n" + prompt,

            stream = false,
            think = false,

            options = new
            {
                temperature = 0.3,
                num_ctx = 4096,
                num_predict = 2000
            }
        };

        Console.WriteLine("[VideoManager] Sending request to Ollama...");
        Console.WriteLine($"[VideoManager] Model: {ManagerModel}");
        Console.WriteLine($"[VideoManager] Prompt length: {prompt.Length}");

        using HttpResponseMessage response = await Client.PostAsJsonAsync(OllamaUrl, body);

        string json = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"[VideoManager] HTTP status: {(int)response.StatusCode} {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(json);

        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("done_reason", out JsonElement doneReasonElement))
        {
            string doneReason = doneReasonElement.GetString() ?? "";
            Console.WriteLine($"[VideoManager] Done reason: {doneReason}");

            if (doneReason.Equals("length", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[VideoManager] WARNING: Manager output hit token limit.");
            }
        }

        if (root.TryGetProperty("eval_count", out JsonElement evalCountElement))
        {
            Console.WriteLine($"[VideoManager] Output tokens: {evalCountElement.GetInt32()}");
        }

        if (!root.TryGetProperty("response", out JsonElement responseElement))
        {
            Console.WriteLine("[VideoManager] ERROR: Ollama JSON has no 'response' property.");
            return "";
        }

        string modelOutput = responseElement.GetString() ?? "";

        return modelOutput.Trim();
    }

    private static VideoResearchPlan? TryParsePlan(string raw)
    {
        Console.WriteLine("[VideoManager] Parsing manager output...");

        string topic = "";
        string angle = "";
        string researchQuestion = "";
        List<string> searchQueries = new();

        string[] lines = raw
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        foreach (string line in lines)
        {
            if (line.StartsWith("TOPIC:", StringComparison.OrdinalIgnoreCase))
            {
                topic = line["TOPIC:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("ANGLE:", StringComparison.OrdinalIgnoreCase))
            {
                angle = line["ANGLE:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("RESEARCH_QUESTION:", StringComparison.OrdinalIgnoreCase))
            {
                researchQuestion = line["RESEARCH_QUESTION:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("SEARCH_QUERY:", StringComparison.OrdinalIgnoreCase))
            {
                string query = line["SEARCH_QUERY:".Length..].Trim();

                query = CleanSearchQuery(query);

                if (!string.IsNullOrWhiteSpace(query))
                    searchQueries.Add(query);

                continue;
            }
        }

        searchQueries = searchQueries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct()
            .Take(5)
            .ToList();

        if (string.IsNullOrWhiteSpace(topic))
        {
            Console.WriteLine("[VideoManager] Parse failed: missing TOPIC.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(angle))
        {
            Console.WriteLine("[VideoManager] Parse failed: missing ANGLE.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(researchQuestion))
        {
            Console.WriteLine("[VideoManager] Parse failed: missing RESEARCH_QUESTION.");
            return null;
        }

        if (searchQueries.Count < 3)
        {
            Console.WriteLine($"[VideoManager] Parse failed: only {searchQueries.Count} SEARCH_QUERY line(s). Need at least 3.");
            return null;
        }

        return new VideoResearchPlan(
            Topic: topic,
            Angle: angle,
            ResearchQuestion: researchQuestion,
            SearchQueries: searchQueries
        );
    }

    private static string CleanSearchQuery(string query)
    {
        query = query.Replace("EX_01", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("Anton", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("roast", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("\"", "");
        query = query.Replace("*", "");
        query = query.Replace("-", " ");

        return string.Join(" ", query.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static void PrintPlan(VideoResearchPlan plan)
    {
        Console.WriteLine($"[VideoManager] Topic: {plan.Topic}");
        Console.WriteLine($"[VideoManager] Angle: {plan.Angle}");
        Console.WriteLine($"[VideoManager] Research question: {plan.ResearchQuestion}");
        Console.WriteLine("[VideoManager] Search queries:");

        foreach (string query in plan.SearchQueries)
        {
            Console.WriteLine($"[VideoManager] - {query}");
        }
    }

    private static VideoResearchPlan GetFallbackPlan()
    {
        return new VideoResearchPlan(
            Topic: "AI-generated YouTube content",
            Angle: "AI tools are making content easier, but they are also flooding YouTube with soulless low-effort videos.",
            ResearchQuestion: "What are the current AI-generated video trends on YouTube in 2026?",
            SearchQueries: new List<string>
            {
                "AI-generated YouTube videos trend 2026",
                "faceless AI YouTube channels 2026",
                "AI video tools creators use 2026",
                "YouTube synthetic influencers trend",
                "AI Shorts automation trend 2026"
            }
        );
    }
}

public record VideoResearchPlan(
    string Topic,
    string Angle,
    string ResearchQuestion,
    List<string> SearchQueries
);