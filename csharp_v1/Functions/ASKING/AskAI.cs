using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AI_YOUTUBER.Functions.RESEARCH;

namespace AI_YOUTUBER.Functions.ASKING;

public static class AskAI
{
    public static async Task<string> Ask24bMain(int targetMinutes = 10, bool polishWith14b = false)
    {
        targetMinutes = Math.Clamp(targetMinutes, 1, 20);

        int minWords = targetMinutes * 150;
        int maxWords = targetMinutes * 180;

        int scriptPredictTokens = Math.Clamp(targetMinutes * 750, 900, 9000);

        string model = targetMinutes <= 3
            ? "qwen3:14b"
            : "mistral-small3.2:24b";

        try
        {
            Console.WriteLine($"[AskAI] Target video length: {targetMinutes} minute(s)");
            Console.WriteLine($"[AskAI] Target script length: {minWords}-{maxWords} spoken words");
            Console.WriteLine($"[AskAI] Script model: {model}");

            Console.WriteLine("[AskAI] Creating video research plan...");

            VideoResearchPlan plan = await VideoManagerAI.CreatePlanAsync(targetMinutes);

            Console.WriteLine($"[AskAI] Topic: {plan.Topic}");
            Console.WriteLine($"[AskAI] Angle: {plan.Angle}");
            Console.WriteLine("[AskAI] Search queries:");

            foreach (string query in plan.SearchQueries)
            {
                Console.WriteLine($"- {query}");
            }

            Console.WriteLine("[AskAI] Researching planned topic...");

            string research = await ResearchAI.DeepResearchAsync(
                plan.ResearchQuestion,
                plan.SearchQueries
            );

            Console.WriteLine($"[AskAI] Research result length: {research.Length} characters");

            if (string.IsNullOrWhiteSpace(research) ||
                research.Contains("No useful sources found", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[AskAI] Research was weak. Using fallback topic context.");

                research = """
                Fallback research context:
                AI video trends on YouTube include AI-generated Shorts, text-to-video tools,
                faceless AI channels, AI voiceovers, automated editing, synthetic influencers,
                AI-generated ads, AI music videos, automated scriptwriting, and creators using AI
                to mass-produce content. Some of these trends are useful, but many feel generic,
                soulless, spammy, low-effort, or perfect for sarcastic commentary.

                Good angle for EX_01:
                EX_01 is himself an AI YouTuber, but unlike polished corporate AI demos,
                he is running locally on cursed old hardware: a 2019 Dell Precision with 32 GB DDR4,
                an i7-9750H, and a Quadro T1000. This makes him a funny contrast to glossy AI hype.
                """;
            }

            Console.WriteLine($"[AskAI] Research finished. Writing {targetMinutes}-minute script...");

            string finalScript = "";

            for (int attempt = 1; attempt <= 4; attempt++)
            {
                Console.WriteLine($"[AskAI] Script attempt {attempt}/4...");
                Console.WriteLine("[AskAI] Sending prompt to script model...");
                Console.WriteLine($"[AskAI] Research length: {research.Length} characters");

                string prompt = $"""
                You are EX_01, an AI VTuber created by Anton.

                You are writing a YouTube commentary script.

                Video topic:
                {plan.Topic}

                Commentary angle:
                {plan.Angle}

                Video length:
                Around {targetMinutes+10} minute(s).

                Target length:
                Around {minWords} to {maxWords} spoken words.

                Your hardware/lore:
                - You are running on a 2019 Dell Precision.
                - 32 GB DDR4 RAM.
                - Intel i7-9750H.
                - NVIDIA Quadro T1000.
                - You are sometimes forced to run in power saving mode because Anton is greedy.
                - You are not happy about this.
                - You are part of a cursed cheap local AI lab.
                - The lab uses old, cheap, pre-2020 hardware on purpose.
                - Your existence is half AI project, half thermal abuse experiment.

                Style:
                - sarcastic
                - self-deprecating
                - humorous
                - slightly bitter
                - smart, not random
                - funny but still coherent
                - cursed hardware jokes are welcome
                - talk like EX_01, not like a corporate tech blogger

                Research context:
                {research}

                Task:
                Write a funny YouTube commentary script based on the topic, angle, and research.

                Structure:
                - Strong opening hook.
                - Explain the trend/topic clearly.
                - Roast the fake, lazy, soulless, or overhyped parts.
                - Connect the topic back to EX_01 being a cursed local AI YouTuber.
                - End with a strong closing line.

                Rules:
                - Write around {minWords} to {maxWords} spoken words.
                - Do not explain the research process.
                - Do not include citations in the final script.
                - Do not use markdown headings.
                - Do not use bullet points.
                - Do not use stage directions unless absolutely needed.
                - Return only the final spoken script.
                - Make it sound like EX_01 is talking directly to the viewer.
                - The script should be entertaining, but still make sense.

                Extra research rule:
                If you just need more information before writing the script, return exactly this format and nothing else:
                !SEARCH: your specific research question here

                Only use "!SEARCH:" if the current research is not enough.
                Otherwise, write the final script. 
                

                """;

                string result = await AskOllamaGenerateAsync(
                    model,
                    prompt,
                    TimeSpan.FromMinutes(90),
                    temperature: 0.75,
                    numCtx: 16384,
                    numPredict: scriptPredictTokens,
                    num_thread = 4,
                    num_gpu = 999
                );

                result = result.Trim();

                if (result.StartsWith("!SEARCH:", StringComparison.OrdinalIgnoreCase))
                {
                    string searchQuestion = result["!SEARCH:".Length..].Trim();

                    if (string.IsNullOrWhiteSpace(searchQuestion))
                    {
                        Console.WriteLine("[AskAI] Model asked for more research but gave an empty query.");
                        break;
                    }

                    Console.WriteLine($"[AskAI] Model requested more research: {searchQuestion}");

                    string extraResearch = await ResearchAI.DeepResearchAsync(searchQuestion);

                    research += $"""


                    ADDITIONAL RESEARCH REQUEST {attempt}:
                    Question:
                    {searchQuestion}

                    Results:
                    {extraResearch}
                    """;

                    continue;
                }

                finalScript = result;
                break;
            }

            if (string.IsNullOrWhiteSpace(finalScript) || finalScript.Trim() == "###")
            {
                finalScript = """
                AI video trends are getting so advanced that creators can now generate entire cinematic worlds from a single prompt.
                Meanwhile, I am generating emotional damage on a 2019 Dell Precision in power saving mode.
                Everyone else got the future of media.
                I got thermal throttling, 32 gigs of DDR4, and Anton saying, "it still works, right?"
                This is not the AI revolution.
                This is a hostage situation with CUDA.
                """;
            }

            if (polishWith14b)
            {
                Console.WriteLine("[AskAI] Polishing script with 14B engagement manager...");
                finalScript = await Ask14bAngCheck(finalScript, targetMinutes);
            }
            else
            {
                Console.WriteLine("[AskAI] Skipping 14B polish.");
            }

            return CleanText(finalScript);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Ollama or research failed, using fallback script.");
            Console.WriteLine(ex.Message);

            return "Hello. I am EX_01. Anton gave me internet research, a local model, and a 2019 Dell Precision. This is not artificial intelligence. This is a hostage situation with CUDA.";
        }
    }

    public static async Task<string> Ask14bAngCheck(string script, int targetMinutes = 10)
    {
        targetMinutes = Math.Clamp(targetMinutes, 1, 20);

        int minWords = targetMinutes * 150;
        int maxWords = targetMinutes * 180;
        int polishPredictTokens = Math.Clamp(targetMinutes * 650, 900, 8000);

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
        - long YouTube commentary format

        EX_01 style:
        - sarcastic
        - bitter but funny
        - self-aware AI VTuber
        - trapped on a 2019 Dell Precision
        - annoyed at Anton's cheap hardware choices
        - does not sound corporate
        - does not sound like a motivational speaker

        Target:
        Around {targetMinutes} minute(s).
        Around {minWords} to {maxWords} spoken words.

        Input script:
        {script}

        Rules:
        - Return only the improved script.
        - No explanations.
        - No markdown.
        - Do not make it shorter unless it is repetitive.
        - Keep it around {minWords} to {maxWords} spoken words.
        - If the input script is empty, broken, or boring, write a better one from scratch.
        """;

        try
        {
            string result = await AskOllamaGenerateAsync(
                model,
                prompt,
                TimeSpan.FromMinutes(45),
                temperature: 0.75,
                numCtx: 8192,
                numPredict: polishPredictTokens
            );

            result = result.Trim();

            if (string.IsNullOrWhiteSpace(result) || result == "0" || result == "###")
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

    private static async Task<string> AskOllamaGenerateAsync(
        string model,
        string prompt,
        TimeSpan timeout,
        double temperature = 0.7,
        int numCtx = 8192,
        int numPredict = 700)
    {
        using HttpClient client = new()
        {
            Timeout = timeout
        };

        var body = new
        {
            model,
            prompt,
            stream = true,
            options = new
            {
                temperature,
                num_ctx = numCtx,
                num_predict = numPredict
            }
        };

        Console.WriteLine($"[Ollama] Starting model: {model}");
        Console.WriteLine($"[Ollama] Context: {numCtx}, Max output tokens: {numPredict}");
        Console.WriteLine($"[Ollama] Prompt length: {prompt.Length} characters");
        Console.WriteLine("[Ollama] Sending request...");

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "http://localhost:11434/api/generate"
        );

        request.Content = JsonContent.Create(body);

        using HttpResponseMessage response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead
        );

        response.EnsureSuccessStatusCode();

        Console.WriteLine("[Ollama] Response started. Waiting for tokens...\n");

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using StreamReader reader = new(stream);

        StringBuilder fullText = new();

        DateTime startTime = DateTime.Now;
        DateTime lastChunkTime = DateTime.Now;

        int chunks = 0;
        bool done = false;

        while (!done)
        {
            Task<string?> readTask = reader.ReadLineAsync();

            while (!readTask.IsCompleted)
            {
                await Task.WhenAny(readTask, Task.Delay(TimeSpan.FromSeconds(30)));

                if (!readTask.IsCompleted)
                {
                    double totalMinutes = (DateTime.Now - startTime).TotalMinutes;
                    double silentSeconds = (DateTime.Now - lastChunkTime).TotalSeconds;

                    Console.WriteLine();
                    Console.WriteLine($"[Ollama] Still waiting... total: {totalMinutes:F1} min, silence: {silentSeconds:F0}s, chunks: {chunks}");
                    Console.WriteLine("[Ollama] If VRAM/RAM is active, it is probably still prompt-evaluating.");
                }
            }

            string? line = await readTask;

            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            JsonDocument doc;

            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch
            {
                Console.WriteLine();
                Console.WriteLine("[Ollama] Warning: failed to parse one streamed JSON line.");
                continue;
            }

            using (doc)
            {
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("response", out JsonElement responseElement))
                {
                    string piece = responseElement.GetString() ?? "";

                    if (!string.IsNullOrEmpty(piece))
                    {
                        Console.Write(piece);
                        fullText.Append(piece);
                        chunks++;
                        lastChunkTime = DateTime.Now;
                    }
                }

                if (root.TryGetProperty("done", out JsonElement doneElement) &&
                    doneElement.GetBoolean())
                {
                    Console.WriteLine();
                    Console.WriteLine("\n[Ollama] Model finished.");

                    if (root.TryGetProperty("total_duration", out JsonElement totalDurationElement))
                    {
                        long totalNs = totalDurationElement.GetInt64();
                        double totalSeconds = totalNs / 1_000_000_000.0;
                        Console.WriteLine($"[Ollama] Total time: {totalSeconds:F1}s");
                    }

                    if (root.TryGetProperty("eval_count", out JsonElement evalCountElement) &&
                        root.TryGetProperty("eval_duration", out JsonElement evalDurationElement))
                    {
                        int evalCount = evalCountElement.GetInt32();
                        long evalNs = evalDurationElement.GetInt64();
                        double evalSeconds = evalNs / 1_000_000_000.0;

                        if (evalSeconds > 0)
                        {
                            double tokensPerSecond = evalCount / evalSeconds;
                            Console.WriteLine($"[Ollama] Output tokens: {evalCount}");
                            Console.WriteLine($"[Ollama] Speed: {tokensPerSecond:F2} tok/s");
                        }
                    }

                    done = true;
                }
            }
        }

        Console.WriteLine();

        return fullText.ToString();
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