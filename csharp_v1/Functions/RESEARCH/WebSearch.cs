using System.Text.Json;

namespace AI_YOUTUBER.Functions.RESEARCH;

public static class WebSearch
{
    private static readonly HttpClient Client = new();

    public static async Task<List<SearchResult>> SearchAsync(string query, int maxResults = 5)
    {
        string searxngUrl = "http://localhost:8080/search";

        string url =
            $"{searxngUrl}?q={Uri.EscapeDataString(query)}&format=json&language=en";

        string json = await Client.GetStringAsync(url);

        using JsonDocument doc = JsonDocument.Parse(json);

        List<SearchResult> results = new();

        if (!doc.RootElement.TryGetProperty("results", out JsonElement resultsElement))
            return results;

        foreach (JsonElement item in resultsElement.EnumerateArray().Take(maxResults))
        {
            string title = item.TryGetProperty("title", out JsonElement titleEl)
                ? titleEl.GetString() ?? ""
                : "";

            string link = item.TryGetProperty("url", out JsonElement urlEl)
                ? urlEl.GetString() ?? ""
                : "";

            string snippet = item.TryGetProperty("content", out JsonElement contentEl)
                ? contentEl.GetString() ?? ""
                : "";

            if (!string.IsNullOrWhiteSpace(link))
            {
                results.Add(new SearchResult(title, link, snippet));
            }
        }

        return results;
    }
}

public record SearchResult(string Title, string Url, string Snippet);