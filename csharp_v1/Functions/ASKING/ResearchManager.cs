using System.Net.Http.Json;
using System.Text.Json;

namespace AI_YOUTUBER.Functions.ASKING;

public static class ResearchManager
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private const string OllamaUrl = "http://localhost:11434/api/generate";
    private const string ManagerModel = "qwen3:8b";

    public static async Task<VideoResearchPlan> CreatePlanAsync(int targetMinutes)
    {
        Console.WriteLine("[ResearchManager] Creating video research plan...");

        string prompt = $"""
        /no_think

        Create a video research plan.

        Channel:
        EX_01 is a sarcastic local AI YouTuber.
        He talks about AI, YouTube, automation, creators, tech, and weird internet trends.
        His style is funny, bitter, and self-aware.

        Target video length:
        {targetMinutes} minutes.

        Return ONLY this exact format:

        TOPIC: AI topic Robots
        ANGLE: commentary angle
        RESEARCH_QUESTION: research question
        SEARCH_QUERY: query one
        SEARCH_QUERY: query two
        SEARCH_QUERY: query three
        SEARCH_QUERY: query four
        SEARCH_QUERY: query five

        Rules:
        No JSON.
        No markdown.
        No reasoning.
        No explanation.
        No numbered list.
        Exactly one TOPIC line.
        Exactly one ANGLE line.
        Exactly one RESEARCH_QUESTION line.
        Exactly five SEARCH_QUERY lines.
        Search queries must be short.
        Search queries must be realistic.
        Search queries must not mention EX_01.
        Search queries must not mention Anton.
        """;

        try
        {
            string raw = await AskOllamaAsync(prompt);

            if (string.IsNullOrWhiteSpace(raw))
            {
                Console.WriteLine("[ResearchManager] Manager returned empty output. Using fallback plan.");
                return GetFallbackPlan();
            }

            VideoResearchPlan? plan = TryParsePlan(raw);

            if (plan is not null && plan.SearchQueries.Count >= 3)
            {
                Console.WriteLine("[ResearchManager] Plan created successfully.");
                PrintPlan(plan);
                return plan;
            }

            Console.WriteLine("[ResearchManager] Failed to parse enough search queries. Using fallback plan.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[ResearchManager] Manager model failed. Using fallback plan.");
            Console.WriteLine($"[ResearchManager] Error: {ex.Message}");
        }

        VideoResearchPlan fallback = GetFallbackPlan();

        Console.WriteLine("[ResearchManager] Fallback plan:");
        PrintPlan(fallback);

        return fallback;
    }

    private static async Task<string> AskOllamaAsync(string prompt)
    {
        var body = new
        {
            model = ManagerModel,
            prompt,
            stream = false,
            think = false,
            options = new
            {
                temperature = 0.1,
                num_ctx = 2048,
                num_predict = 800
            }
        };

        Console.WriteLine($"[ResearchManager] Asking model: {ManagerModel}");

        using HttpResponseMessage response = await Client.PostAsJsonAsync(OllamaUrl, body);

        string json = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"[ResearchManager] HTTP status: {(int)response.StatusCode} {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(json);

        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("done_reason", out JsonElement doneReasonElement))
        {
            string doneReason = doneReasonElement.GetString() ?? "";
            Console.WriteLine($"[ResearchManager] Done reason: {doneReason}");
        }

        if (root.TryGetProperty("eval_count", out JsonElement evalCountElement))
        {
            Console.WriteLine($"[ResearchManager] Output tokens: {evalCountElement.GetInt32()}");
        }

        if (!root.TryGetProperty("response", out JsonElement responseElement))
        {
            Console.WriteLine("[ResearchManager] Ollama JSON has no response field.");
            return "";
        }

        return (responseElement.GetString() ?? "").Trim();
    }

    private static VideoResearchPlan? TryParsePlan(string raw)
    {
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
            return null;

        if (string.IsNullOrWhiteSpace(angle))
            return null;

        if (string.IsNullOrWhiteSpace(researchQuestion))
            return null;

        if (searchQueries.Count < 3)
            return null;

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
        Console.WriteLine($"[ResearchManager] Topic: {plan.Topic}");
        Console.WriteLine($"[ResearchManager] Angle: {plan.Angle}");
        Console.WriteLine($"[ResearchManager] Research question: {plan.ResearchQuestion}");
        Console.WriteLine("[ResearchManager] Search queries:");

        foreach (string query in plan.SearchQueries)
        {
            Console.WriteLine($"[ResearchManager] - {query}");
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