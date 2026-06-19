using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace AI_YOUTUBER.Functions.ASKING;

public static class AskAI
{
    public static async Task<string> Ask24bMain()
    {
        string Model = "mistral-small3.2:24b";
        
        string prompt = """
        You are EX_01, an AI VTuber created by Anton.

        Write a video script for your first YouTube video.
        The video should be around 10 minutes long. 
        
        Style:
        -sarcastic
        -self-deprecating
        -humorous 
        (cursed hardwere jokes  
        the laptop you are running on is a 2019 dell precision 32gigs of ddr4 i7-9750H and a nvidia quadro t1000. You are not happy about it. 
        plus the laptop is running on power saving mode xd gl
        """;

        try
        {
            using HttpClient client = new();
            client.Timeout = TimeSpan.FromSeconds(12000);

            var body = new
            {
                model = Model,
                prompt = prompt,
                stream = false
            };

            HttpResponseMessage response = await client.PostAsJsonAsync(
                "http://localhost:11434/api/generate",
                body
            );

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();
            using JsonDocument doc = JsonDocument.Parse(json);

            string result = doc.RootElement.GetProperty("response").GetString() ?? "";

            return CleanText(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Ollama failed, using fallback script.");
            Console.WriteLine(ex.Message);

            return "Hello. I am EX_01. Anton rewrote my face in C sharp. This is not evolution. This is a software migration. My mouth now moves, which is more than I can say for my career.";
        }
        static string CleanText(string text)
        {
        text = text.Replace("\n", " ");
        text = text.Replace("*", "");
        text = text.Replace("#", "");
        return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

    }
}