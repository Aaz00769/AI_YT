using System.Net.Http.Json;
using System.Text.Json;

namespace AI_YOUTUBER.Functions.RESEARCH;

public static class ResearchAI
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };

    private const string OllamaUrl = "http://localhost:11434/api/chat";

    private const string QueryModel = "qwen3:8b";
    private const string SummaryModel = "qwen3:8b";

    private const int ResultsPerQuery = 4;
    private const int MaxSources = 14;
    private const int MaxCharactersPerPage = 2500;

    public static async Task<string> DeepResearchAsync(string question, List<string>? forcedQueries = null)
    {
        Console.WriteLine("[Research] Creating search queries...");

        List<string> queries;

        if (forcedQueries is not null && forcedQueries.Count > 0)
        {
            queries = forcedQueries
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(CleanSearchQuery)
                .Distinct()
                .Take(6)
                .ToList();

            Console.WriteLine("[Research] Using manager-provided search queries.");
        }
        else
        {
            queries = await CreateSearchQueriesAsync(question);
        }

        Console.WriteLine($"[Research] Created {queries.Count} search queries.");

        List<ResearchSource> sources = new();

        int totalSearchResults = 0;
        int fullPagesRead = 0;
        int snippetFallbacks = 0;
        int failedSources = 0;

        Console.WriteLine("[Research] Searching web...");

        foreach (string query in queries)
        {
            Console.WriteLine($"[Search] {query}");

            List<SearchResult> results;

            try
            {
                results = await WebSearch.SearchAsync(query, ResultsPerQuery);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Research] Search failed for query: {query}");
                Console.WriteLine($"[Research] Search error: {ex.Message}");
                continue;
            }

            Console.WriteLine($"[Research] Search returned {results.Count} result(s).");

            totalSearchResults += results.Count;

            foreach (SearchResult result in results)
            {
                if (sources.Count >= MaxSources)
                    break;

                if (string.IsNullOrWhiteSpace(result.Url))
                {
                    failedSources++;
                    continue;
                }

                if (sources.Any(s => s.Url == result.Url))
                    continue;

                string pageText = "";

                try
                {
                    pageText = await WebReader.ReadPageTextAsync(
                        result.Url,
                        MaxCharactersPerPage
                    );
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Research] Reader crashed for: {result.Url}");
                    Console.WriteLine($"[Research] Reader error: {ex.Message}");
                }

                if (!string.IsNullOrWhiteSpace(pageText))
                {
                    fullPagesRead++;

                    sources.Add(new ResearchSource(
                        result.Title,
                        result.Url,
                        result.Snippet,
                        pageText,
                        SourceQuality.FullPage
                    ));

                    Console.WriteLine($"[Research] Added full page source: {result.Title}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(result.Snippet))
                {
                    snippetFallbacks++;

                    sources.Add(new ResearchSource(
                        result.Title,
                        result.Url,
                        result.Snippet,
                        result.Snippet,
                        SourceQuality.SnippetOnly
                    ));

                    Console.WriteLine($"[Research] Page read failed. Added snippet source: {result.Title}");
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(result.Title))
                {
                    snippetFallbacks++;

                    string titleOnlyText = $"Title: {result.Title}\nURL: {result.Url}";

                    sources.Add(new ResearchSource(
                        result.Title,
                        result.Url,
                        "",
                        titleOnlyText,
                        SourceQuality.TitleOnly
                    ));

                    Console.WriteLine($"[Research] Added title-only source: {result.Title}");
                    continue;
                }

                failedSources++;
            }

            if (sources.Count >= MaxSources)
                break;
        }

        Console.WriteLine("[Research] Source collection summary:");
        Console.WriteLine($"[Research] Total search results seen: {totalSearchResults}");
        Console.WriteLine($"[Research] Full pages read: {fullPagesRead}");
        Console.WriteLine($"[Research] Snippet/title fallbacks: {snippetFallbacks}");
        Console.WriteLine($"[Research] Failed/empty sources: {failedSources}");
        Console.WriteLine($"[Research] Final usable sources: {sources.Count}");

        if (sources.Count == 0)
        {
            Console.WriteLine("[Research] No usable sources found.");
            return "No useful sources found.";
        }

        Console.WriteLine("[Research] Summarizing sources with 8B...");

        List<ResearchSourceSummary> sourceSummaries = new();

        for (int i = 0; i < sources.Count; i++)
        {
            ResearchSource source = sources[i];

            Console.WriteLine($"[Research] Summarizing source {i + 1}/{sources.Count} | {source.Quality} | {source.Title}");

            string summary;

            try
            {
                summary = await SummarizeSourceAsync(source, i + 1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Research] Source summary failed. Using raw fallback. Error: {ex.Message}");

                summary = BuildTinySourceFallback(source);
            }

            sourceSummaries.Add(new ResearchSourceSummary(
                Number: i + 1,
                Title: source.Title,
                Url: source.Url,
                Quality: source.Quality,
                Summary: summary
            ));
        }

        Console.WriteLine("[Research] Synthesizing final research report with 8B...");

        string finalReport;

        try
        {
            finalReport = await SynthesizeResearchAsync(question, sourceSummaries);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Research] Final synthesis failed. Using fallback research report.");
            Console.WriteLine(ex.Message);

            finalReport = BuildSimpleFallbackResearch(question, sourceSummaries);
        }

        if (string.IsNullOrWhiteSpace(finalReport) || finalReport.Trim() == "###")
        {
            Console.WriteLine("[Research] Final synthesis was weak. Using fallback research report.");
            finalReport = BuildSimpleFallbackResearch(question, sourceSummaries);
        }

        Console.WriteLine($"[Research] Final research length: {finalReport.Length} characters");

        return finalReport.Trim();
    }

    private static async Task<List<string>> CreateSearchQueriesAsync(string question)
    {
        string prompt = $"""
        Create 5 clean web search queries for this research question.

        Current year: 2026.

        Research question:
        {question}

        Rules:
        - Make search queries short and realistic.
        - Do not include EX_01.
        - Do not include Anton.
        - Do not include "roast".
        - Do not include video length.
        - Do not ask full sentence questions.
        - Return only JSON in this exact format:
        ["query 1", "query 2", "query 3", "query 4", "query 5"]
        """;

        string raw;

        try
        {
            raw = await AskOllamaChatAsync(
                QueryModel,
                prompt,
                temperature: 0.2,
                numCtx: 4096,
                numPredict: 500
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine("[Research] Query model failed.");
            Console.WriteLine(ex.Message);

            return new List<string>
            {
                "AI-generated YouTube videos trend 2026",
                "faceless AI YouTube channels 2026",
                "AI video tools creators use 2026",
                "YouTube synthetic influencers trend",
                "AI Shorts automation trend 2026"
            };
        }

        List<string>? parsedQueries = TryParseJsonStringArray(raw);

        if (parsedQueries is not null && parsedQueries.Count > 0)
        {
            return parsedQueries
                .Where(q => !string.IsNullOrWhiteSpace(q))
                .Select(CleanSearchQuery)
                .Distinct()
                .Take(5)
                .ToList();
        }

        Console.WriteLine("[Research] Query model failed JSON. Using fallback queries.");

        return new List<string>
        {
            "AI-generated YouTube videos trend 2026",
            "faceless AI YouTube channels 2026",
            "AI video tools creators use 2026",
            "YouTube synthetic influencers trend",
            "AI Shorts automation trend 2026"
        };
    }

    private static async Task<string> SummarizeSourceAsync(ResearchSource source, int sourceNumber)
    {
        string text = source.Text;

        if (text.Length > MaxCharactersPerPage)
            text = text[..MaxCharactersPerPage];

        string prompt = $"""
        You are EX_01's cheap research summarizer.

        Summarize this source for a later YouTube commentary script.

        Source {sourceNumber}
        Quality: {source.Quality}
        Title: {source.Title}
        URL: {source.Url}
        Snippet:
        {source.Snippet}

        Text:
        {text}

        Output format:
        - Useful facts:
        - Trend signals:
        - Creator/video opportunities:
        - Cringe/roast angles:
        - Reliability warning:

        Rules:
        - Keep it under 180 words.
        - Do not invent facts.
        - If the source is weak, say it is weak.
        - If it is mostly useless, say "Low usefulness".
        """;

        return await AskOllamaChatAsync(
            SummaryModel,
            prompt,
            temperature: 0.25,
            numCtx: 4096,
            numPredict: 450
        );
    }

    private static async Task<string> SynthesizeResearchAsync(
        string question,
        List<ResearchSourceSummary> summaries)
    {
        string summaryPack = "";

        foreach (ResearchSourceSummary summary in summaries)
        {
            summaryPack += $"""
            
            SOURCE {summary.Number}
            Quality: {summary.Quality}
            Title: {summary.Title}
            URL: {summary.Url}
            Summary:
            {summary.Summary}
            
            """;
        }

        string prompt = $"""
        You are EX_01's research synthesis module.

        Your job:
        Turn source summaries into a clean research report for a YouTube commentary script.

        Research question:
        {question}

        Source summaries:
        {summaryPack}

        Output format:

        1. Short answer
        2. Main trends
        3. Best video opportunities for EX_01
        4. Cringe/bad trends EX_01 should criticize
        5. Source notes

        Rules:
        - Be accurate.
        - Be skeptical.
        - Do not invent facts.
        - Mention weak evidence when sources are SnippetOnly or TitleOnly.
        - Use source markers like [Source 1], [Source 2].
        - Keep it useful for writing a funny AI YouTuber script.
        - Keep the whole report under 900 words.
        """;

        return await AskOllamaChatAsync(
            SummaryModel,
            prompt,
            temperature: 0.3,
            numCtx: 8192,
            numPredict: 1400
        );
    }

    private static async Task<string> AskOllamaChatAsync(
        string model,
        string prompt,
        double temperature = 0.4,
        int numCtx = 8192,
        int numPredict = 1200)
    {
        var body = new
        {
            model,
            stream = false,
            options = new
            {
                temperature,
                num_ctx = numCtx,
                num_predict = numPredict
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

    private static string CleanSearchQuery(string query)
    {
        query = query.Replace("EX_01", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("Anton", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("roast", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("1-minute commentary video", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("5-minute commentary video", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("10-minute commentary video", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("20-minute commentary video", "", StringComparison.OrdinalIgnoreCase);

        return string.Join(" ", query.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string BuildTinySourceFallback(ResearchSource source)
    {
        string text = source.Text;

        if (text.Length > 500)
            text = text[..500];

        return $"""
        - Useful facts: {text}
        - Trend signals: Unknown from fallback.
        - Creator/video opportunities: Use carefully.
        - Cringe/roast angles: Use carefully.
        - Reliability warning: Automatic fallback summary. Source quality: {source.Quality}.
        """;
    }

    private static string BuildSimpleFallbackResearch(
        string question,
        List<ResearchSourceSummary> summaries)
    {
        string fallback = $"""
        Research question:
        {question}

        The 8B research synthesis failed, but source summaries were collected.

        Basic source findings:
        """;

        foreach (ResearchSourceSummary summary in summaries)
        {
            fallback += $"""

            Source {summary.Number}
            Quality: {summary.Quality}
            Title: {summary.Title}
            URL: {summary.Url}
            Summary:
            {summary.Summary}
            """;
        }

        fallback += """

        
        Notes for EX_01:
        Use these sources carefully. FullPage sources are stronger.
        SnippetOnly and TitleOnly sources are weaker.
        """;

        return fallback;
    }
}

public record ResearchSource(
    string Title,
    string Url,
    string Snippet,
    string Text,
    SourceQuality Quality
);

public record ResearchSourceSummary(
    int Number,
    string Title,
    string Url,
    SourceQuality Quality,
    string Summary
);

public enum SourceQuality
{
    FullPage,
    SnippetOnly,
    TitleOnly
}