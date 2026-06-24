using System.Net.Http.Json;
using System.Text.Json;

namespace AI_YOUTUBER.Functions.RESEARCH;

public static class ResearchAI
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(60)
    };

    private const string OllamaUrl = "http://localhost:11434/api/chat";

    // Small model: creates search queries.
    private const string QueryModel = "qwen3:8b";

    // Big model: final cursed deep research brain.
    private const string ResearchModel = "finance-24b-16k";

    // Deep mode settings.
    private const int ResultsPerQuery = 5;
    private const int MaxSources = 20;

    public static async Task<string> DeepResearchAsync(string question)
    {
        Console.WriteLine("[Research] Creating search queries...");

        List<string> queries = await CreateSearchQueriesAsync(question);

        List<ResearchSource> sources = new();

        Console.WriteLine("[Research] Searching web...");

        foreach (string query in queries)
        {
            Console.WriteLine($"[Search] {query}");

            List<SearchResult> results = await WebSearch.SearchAsync(query, ResultsPerQuery);

            foreach (SearchResult result in results)
            {
                if (sources.Any(s => s.Url == result.Url))
                    continue;

                string pageText = await WebReader.ReadPageTextAsync(result.Url);

                if (string.IsNullOrWhiteSpace(pageText))
                    continue;

                sources.Add(new ResearchSource(
                    result.Title,
                    result.Url,
                    result.Snippet,
                    pageText
                ));

                if (sources.Count >= MaxSources)
                    break;
            }

            if (sources.Count >= MaxSources)
                break;
        }

        if (sources.Count == 0)
            return "No useful sources found.";

        Console.WriteLine($"[Research] Found {sources.Count} sources.");
        Console.WriteLine("[Research] Packing raw sources for cursed deep mode...");

        string sourcePack = "";

        for (int i = 0; i < sources.Count; i++)
        {
            ResearchSource source = sources[i];

            Console.WriteLine($"[Raw Source] {i + 1}/{sources.Count}");

            sourcePack += $"""
            
            SOURCE {i + 1}
            Title: {source.Title}
            URL: {source.Url}
            Snippet:
            {source.Snippet}

            Text:
            {source.Text}
            
            """;
        }

        Console.WriteLine("[Research] Asking big model...");

        string finalPrompt = $"""
        You are EX_01's cursed deep research module.

        Your job is to turn raw web research into useful intelligence for an AI YouTuber.

        User question:
        {question}

        Raw sources:
        {sourcePack}

        Output format:

        1. Short answer
        Give a clear answer in 3-5 sentences.

        2. Main trends
        Explain the most important trends found across the sources.

        3. Best opportunities for EX_01
        Explain what EX_01 could use for videos, shorts, jokes, commentary, experiments, or channel strategy.

        4. Bad/cringe trends EX_01 should roast
        Mention trends that feel overused, fake, lazy, annoying, soulless, or perfect for sarcastic commentary.

        5. Source notes
        Mention which source numbers supported the main claims.

        Rules:
        - Write at least 500 words.
        - Do not answer with only "Here".
        - Do not invent facts.
        - If something is uncertain, say it is uncertain.
        - If sources disagree, mention the disagreement.
        - Use source markers like [Source 1], [Source 2].
        - Keep the answer useful for making a YouTube video.
        - EX_01 can be sarcastic, but the research itself must be accurate.
        """;

        return await AskOllamaAsync(ResearchModel, finalPrompt);
    }

    private static async Task<List<string>> CreateSearchQueriesAsync(string question)
    {
        string prompt = $"""
        Create 4 web search queries for this research question.

        Current year: 2026.

        Question:
        {question}

        Rules:
        - If the question asks about current trends, use 2026 in the search query.
        - Do not use 2023 unless the user specifically asks about 2023.
        - Make the queries different from each other.
        - Prefer practical search phrases that would find real articles, reports, forum discussions, YouTube creator advice, or current examples.
        - Return only JSON in this exact format:
        ["query 1", "query 2", "query 3", "query 4"]
        """;

        string raw = await AskOllamaAsync(QueryModel, prompt);

        List<string>? parsedQueries = TryParseJsonStringArray(raw);

        if (parsedQueries is not null && parsedQueries.Count > 0)
            return parsedQueries;

        Console.WriteLine("[Research] Query model failed JSON. Using original question.");

        return new List<string>
        {
            question
        };
    }

    private static async Task<string> AskOllamaAsync(string model, string prompt)
    {
        var body = new
        {
            model,
            stream = false,
            options = new
            {
                temperature = 0.4,
    num_ctx = 16384,
    num_predict = 2500
            },
            messages = new[]
            {
                new
                {
                    role = "system",
                    content = "You are a careful research assistant. Be accurate, skeptical, and useful."
                },
                new
                {
                    role = "user",
                    content = prompt
                }
            }
        };

        using HttpResponseMessage response = await Client.PostAsJsonAsync(OllamaUrl, body);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();

        using JsonDocument doc = JsonDocument.Parse(json);

        return doc.RootElement
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";
    }

    private static List<string>? TryParseJsonStringArray(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw);
        }
        catch
        {
            int start = raw.IndexOf('[');
            int end = raw.LastIndexOf(']');

            if (start == -1 || end == -1 || end <= start)
                return null;

            string extractedJson = raw[start..(end + 1)];

            try
            {
                return JsonSerializer.Deserialize<List<string>>(extractedJson);
            }
            catch
            {
                return null;
            }
        }
    }
}

public record ResearchSource(
    string Title,
    string Url,
    string Snippet,
    string Text
);