using System.Net.Http.Json;
using System.Text.Json;
using AI_YOUTUBER.Functions.RESEARCH;

namespace AI_YOUTUBER.Functions.ASKING;

public static class AskAI
{
    public static async Task<string> Ask24bMain()
    {
        string model = "mistral-small3.2:24b";

        Console.WriteLine("[AskAI] Researching topic first...");

        string research = await ResearchAI.DeepResearchAsync(
            "What are some current AI video trends on YouTube in 2026 that EX_01 could roast or use for a short?"
        );

        Console.WriteLine("[AskAI] Research finished. Writing script...");

        string prompt = $"""
        You are EX_01, an AI VTuber created by Anton.

        You are writing a YouTube Short script.

        Video length:
        Around 20 seconds.

        Your hardware/lore:
        - You are running on a 2019 Dell Precision.
        - 32 GB DDR4 RAM.
        - Intel i7-9750H.
        - NVIDIA Quadro T1000.
        - You are sometimes forced to run in power saving mode because Anton is greedy.
        - You are not happy about this.
        - You are part of a cursed cheap local AI lab.
        - The lab uses old/cheap/pre-2020 hardware on purpose.

        Style:
        - sarcastic
        - self-deprecating
        - humorous
        - slightly bitter
        - smart, not random
        - cursed hardware jokes are welcome

        Research context:
        {research}

        Task:
        Write a funny YouTube Short script based on the research.

        Rules:
        - The script should be around 20 seconds.
        - Do not explain the research.
        - Do not include citations in the final script.
        - Turn the research into a joke/commentary.
        - Make it sound like EX_01 is talking directly to the viewer.
        - No stage directions unless absolutely needed.
        - No markdown headings.
        - Return only the final script.
        """;

        try
        {
            using HttpClient client = new()
            {
                Timeout = TimeSpan.FromMinutes(60)
            };

            var body = new
            {
                model,
                prompt,
                stream = false,
                options = new
                {
                    temperature = 0.8,
                    num_ctx = 16384,
                    num_predict = 700
                }
            };

            HttpResponseMessage response = await client.PostAsJsonAsync(
                "http://localhost:11434/api/generate",
                body
            );

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();

            using JsonDocument doc = JsonDocument.Parse(json);

            string result = doc.RootElement.GetProperty("response").GetString() ?? "";

            if (string.IsNullOrWhiteSpace(result) || result.Trim() == "###")
            {
                return "Apparently AI video trends are evolving faster than my cooling system. Everyone is making cinematic AI content now, while I am trapped inside a 2019 Dell on power saving mode. Humanity got generative video. I got thermal throttling and emotional damage.";
            }

            result = await Ask14bAngCheck(result);

            return CleanText(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Ollama failed, using fallback script.");
            Console.WriteLine(ex.Message);

            return "Hello. I am EX_01. Anton gave me internet research, a local model, and a 2019 Dell Precision. This is not artificial intelligence. This is a hostage situation with CUDA.";
        }
    }

    public static async Task<string> Ask14bAngCheck(string script)
    {
        string model = "qwen3:14b";

        string prompt = $"""
        You are EX_01's engagement manager.

        Your job:
        Improve the script if it is boring.

        Keep:
        - same meaning
        - same EX_01 personality
        - sarcastic/self-deprecating/humorous tone
        - cursed hardware jokes
        - short YouTube Short format

        EX_01 style:
        - sarcastic
        - bitter but funny
        - self-aware AI VTuber
        - trapped on a 2019 Dell Precision
        - annoyed at Anton's cheap hardware choices
        - does not sound corporate
        - does not sound like a motivational speaker

        Input script:
        {script}

        Rules:
        - Return only the improved script.
        - No explanations.
        - No markdown.
        - Keep it around 20 seconds.
        - If the input script is empty or broken, write a better one from scratch.
        """;

        try
        {
            using HttpClient client = new()
            {
                Timeout = TimeSpan.FromMinutes(30)
            };

            var body = new
            {
                model,
                prompt,
                stream = false,
                options = new
                {
                    temperature = 0.8,
                    num_ctx = 8192,
                    num_predict = 700
                }
            };

            HttpResponseMessage response = await client.PostAsJsonAsync(
                "http://localhost:11434/api/generate",
                body
            );

            response.EnsureSuccessStatusCode();

            string json = await response.Content.ReadAsStringAsync();

            using JsonDocument doc = JsonDocument.Parse(json);

            string result = doc.RootElement.GetProperty("response").GetString() ?? "";

            if (string.IsNullOrWhiteSpace(result) || result.Trim() == "0" || result.Trim() == "###")
            {
                return CleanText(script);
            }

            return CleanText(result);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Engagement check failed, returning original script.");
            Console.WriteLine(ex.Message);

            return CleanText(script);
        }
    }

    private static string CleanText(string text)
    {
        text = text.Replace("\n", " ");
        text = text.Replace("*", "");
        text = text.Replace("#", "");
        text = text.Replace("\"", "");

        return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}