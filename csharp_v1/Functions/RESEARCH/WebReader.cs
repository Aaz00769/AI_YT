using HtmlAgilityPack;
using System.Net;

namespace AI_YOUTUBER.Functions.RESEARCH;

public static class WebReader
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public static async Task<string> ReadPageTextAsync(string url, int maxCharacters = 6000)
    {
        try
        {
            string html = await Client.GetStringAsync(url);

            HtmlDocument doc = new();
            doc.LoadHtml(html);

            doc.DocumentNode
                .SelectNodes("//script|//style|//noscript|//svg|//nav|//footer|//header")
                ?.ToList()
                .ForEach(node => node.Remove());

            string text = doc.DocumentNode.InnerText;

            text = WebUtility.HtmlDecode(text);
            text = CleanText(text);

            if (text.Length > maxCharacters)
                text = text[..maxCharacters];

            return text;
        }
        catch
        {
            return "";
        }
    }

    private static string CleanText(string text)
    {
        string[] lines = text
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return string.Join("\n", lines);
    }
}