using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AI_YOUTUBER.Functions.RESEARCH;
using AI_YOUTUBER.Functions.PLANNING;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Models;
using AI_YOUTUBER.Infrastructure;

namespace AI_YOUTUBER.Functions.ASKING;

public static class AskAI
{
    private const int MinimumShortOutputTokens = 512;
    private const int MaximumShortOutputTokens = 1024;

    public static async Task<string> AskShortScriptAsync(int targetSeconds)
    {
        ShortScriptGenerationResult result = await GenerateShortScriptAsync(
            targetSeconds,
            plannedShort: null,
            currentBatchContext: null);
        if (!result.Validation.Success)
        {
            throw new InvalidOperationException(
                "Short script generation failed validation after retries: " +
                string.Join(" ", result.Validation.Errors));
        }

        return result.Script;
    }

    public static async Task<ShortScriptGenerationResult> GenerateShortScriptAsync(
        int targetSeconds,
        PlannedShort? plannedShort,
        string? currentBatchContext,
        int maximumAttempts = 3,
        string? initialFailureReason = null)
    {
        targetSeconds = Math.Clamp(targetSeconds, 15, 60);
        maximumAttempts = Math.Clamp(maximumAttempts, 1, 3);
        (int minWords, int maxWords) = ShortScriptValidator.GetWordRange(targetSeconds);
        int numPredict = CalculateShortOutputTokenBudget(maxWords);
        string continuitySection;

        if (plannedShort is null)
        {
            MemoryContext memoryContext = await VideoMemory.BuildContextForTopicAsync(
                "EX_01 YouTube Short local AI cursed hardware");
            continuitySection = VideoMemory.FormatPromptSection(memoryContext);
        }
        else
        {
            continuitySection = $"""
            COORDINATED BATCH INSTRUCTIONS

            Planned working title: {plannedShort.WorkingTitle}
            Planned hook: {plannedShort.Hook}
            Key difference from the other Shorts: {plannedShort.KeyDifferenceFromOtherVideos}
            Suggested callback: {plannedShort.SuggestedCallback}

            {currentBatchContext}

            Maintain continuity with earlier completed Shorts without requiring viewers to have seen them.
            Avoid repeating previous hooks, jokes, promises, and explanations.
            Use a callback only when it improves this Short; never insert one merely because it exists.
            Keep this understandable to a new viewer.
            Do not say "as you know" when a new viewer would not know.
            Briefly explain necessary context without retelling previous videos.
            Preserve EX_01's established personality and recurring lore.
            Do not treat remembered claims as newly verified research.
            Do not say that something happened "again" unless supplied context proves a prior occurrence.
            """;
        }

        string prompt = $"""
        You are EX_01, a sarcastic local AI VTuber trapped on Anton's cursed 2019 Dell Precision.

        Write one YouTube Short script for approximately {targetSeconds} seconds.
        Target {minWords} to {maxWords} spoken words.

        Required structure:
        - Start immediately with a sharp first-line hook.
        - No greeting and no slow introduction.
        - Build around exactly one central joke or idea.
        - Deliver the payoff near the end.
        - End on the strongest line.

        Voice:
        - sarcastic, self-aware, concise, and coherent
        - cursed old-hardware humor
        - EX_01 is running on an i7-9750H, 32 GB DDR4, and Quadro T1000

        {continuitySection}

        Rules:
        - Return only spoken words.
        - No headings, markdown, bullets, citations, or stage directions.
        - No generic like-and-subscribe line.
        - Stay between {minWords} and {maxWords} words.
        - Do not invent completed experiments, crashes, benchmarks, test results, viewer reactions,
          previous videos, hardware failures, or promises.
        - Only describe an event as something that already happened when it is present in supplied
          previous-video memory, verified research, batch context, or explicit project facts.
        - Describe an experiment that has not happened as a plan, question, prediction, or upcoming test.
        - Do not say something happened "again" unless the supplied context proves it happened before.
        - Finish every sentence and end with terminal punctuation.
        """;

        Console.WriteLine($"[AskAI] Writing {targetSeconds}-second Short ({minWords}-{maxWords} words)...");
        return await GenerateValidatedShortScriptCoreAsync(
            prompt,
            minWords,
            maxWords,
            maximumAttempts,
            initialFailureReason,
            (attemptPrompt, outputBudget) => AskOllamaGenerateDetailedAsync(
                "qwen3:14b",
                attemptPrompt,
                TimeSpan.FromMinutes(30),
                temperature: 0.72,
                numCtx: 4096,
                numPredict: outputBudget,
                disableThinking: true),
            numPredict);
    }

    internal static int CalculateShortOutputTokenBudget(int maximumWords) =>
        Math.Clamp(maximumWords * 5, MinimumShortOutputTokens, MaximumShortOutputTokens);

    internal static async Task<ShortScriptGenerationResult> GenerateValidatedShortScriptCoreAsync(
        string basePrompt,
        int minimumWords,
        int maximumWords,
        int maximumAttempts,
        string? initialFailureReason,
        Func<string, int, Task<OllamaGenerationResult>> generateAsync,
        int maximumOutputTokens = MinimumShortOutputTokens)
    {
        ShortScriptGenerationResult finalResult = new()
        {
            MaximumAttempts = maximumAttempts
        };
        string? failureReason = initialFailureReason;

        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            string attemptPrompt = string.IsNullOrWhiteSpace(failureReason)
                ? basePrompt
                : BuildShortRetryPrompt(
                    basePrompt,
                    failureReason,
                    minimumWords,
                    maximumWords);
            try
            {
                OllamaGenerationResult generation = await generateAsync(
                    attemptPrompt,
                    maximumOutputTokens);
                string script = NormalizeNarration(generation.Text);
                ShortScriptValidationResult validation = ShortScriptValidator.Validate(
                    script,
                    minimumWords,
                    maximumWords,
                    generation);
                finalResult = new ShortScriptGenerationResult
                {
                    Script = script,
                    AttemptCount = attempt,
                    MaximumAttempts = maximumAttempts,
                    Validation = validation,
                    Generation = generation
                };
                if (validation.Success)
                    return finalResult;

                failureReason = ShortScriptValidator.DescribeFailure(validation);
            }
            catch (Exception ex)
            {
                ShortScriptValidationResult validation = ShortScriptValidator.Validate(
                    "",
                    minimumWords,
                    maximumWords,
                    new OllamaGenerationResult
                    {
                        Completed = false,
                        MaximumOutputTokens = maximumOutputTokens
                    });
                validation.Errors.Add($"LLM generation failed: {ex.Message}");
                validation.Success = false;
                finalResult = new ShortScriptGenerationResult
                {
                    AttemptCount = attempt,
                    MaximumAttempts = maximumAttempts,
                    Validation = validation
                };
                failureReason = string.Join(" ", validation.Errors);
                Console.WriteLine($"[ScriptValidation] Error: LLM generation failed: {ex.Message}");
            }

            if (attempt < maximumAttempts)
            {
                Console.WriteLine(
                    $"[ScriptValidation] Regenerating the complete Short from the beginning " +
                    $"(retry {attempt} of {maximumAttempts - 1}).");
            }
        }

        return finalResult;
    }

    private static string BuildShortRetryPrompt(
        string basePrompt,
        string failureReason,
        int minimumWords,
        int maximumWords) =>
        $"""
        Your previous response was invalid because it had this quality-control failure:
        {failureReason}

        Rewrite the complete Short from the beginning.
        Return {minimumWords}-{maximumWords} spoken words.
        Return narration only.
        Do not include reasoning, labels, notes, JSON, or Markdown.
        Do not continue or quote the rejected fragment.
        Finish every sentence.

        ORIGINAL TASK:
        {basePrompt}
        """;

    private static string NormalizeNarration(string text) =>
        string.Join(" ", (text ?? "").Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));

    public static async Task<GeneratedScriptResult> Ask24bMain(int targetMinutes , bool polishWith14b ,EpisodeStrategyPlan strategy)
    {
        ExecutionTimingService? timing = ExecutionTimingContext.Current;
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

           

VideoResearchPlan plan = new(
    Topic: strategy.Topic,
    Angle: strategy.Angle,
    ResearchQuestion: strategy.ResearchQuestion,
    SearchQueries: strategy.SearchQueries
);

Console.WriteLine($"[AskAI] Episode type: {strategy.EpisodeType}");
Console.WriteLine($"[AskAI] Hook: {strategy.Hook}");

            Console.WriteLine($"[AskAI] Topic: {plan.Topic}");
            Console.WriteLine($"[AskAI] Angle: {plan.Angle}");
            Console.WriteLine("[AskAI] Search queries:");

            foreach (string query in plan.SearchQueries)
            {
                Console.WriteLine($"- {query}");
            }

            MemoryContext memoryContext = timing is null
                ? await VideoMemory.BuildContextForTopicAsync($"{plan.Topic} {plan.Angle}")
                : await timing.MeasureAsync(
                    "Memory context retrieval",
                    () => VideoMemory.BuildContextForTopicAsync($"{plan.Topic} {plan.Angle}"));
            string memoryPromptSection = VideoMemory.FormatPromptSection(memoryContext);

            Console.WriteLine("[AskAI] Researching planned topic...");

            string research = timing is null
                ? await ResearchAI.DeepResearchAsync(plan.ResearchQuestion, plan.SearchQueries)
                : await timing.MeasureAsync(
                    "Web research",
                    () => ResearchAI.DeepResearchAsync(plan.ResearchQuestion, plan.SearchQueries));

            Console.WriteLine($"[AskAI] Research result length: {research.Length} characters");

            if (string.IsNullOrWhiteSpace(research) ||
                research.Contains("No useful sources found", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("[AskAI] Research was weak. Using fallback topic context.");

                research = """
                Fallback research context:
                AI video trends on YouTube include AI-generated Shorts, text-to-video tools, A lot of sex jokes 
                faceless AI channels, AI voiceovers, automated editing, synthetic influencers,
                AI-generated ads, AI music videos, automated scriptwriting, and creators using AI
                to mass-produce content. Some of these trends are useful, but many feel generic,
                soulless, spammy, low-effort, or perfect for sarcastic commentary.

                Good angle for EX_01:
                EX_01 is himself an AI YouTuber, but unlike polished corporate AI demos,and is horny and sexy,
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
                Around {targetMinutes} minute(s).

                Target length:
                Around {minWords} to {maxWords} spoken words.

                Your hardware/lore:
                - You are running on a 2019 Dell Precision.
                - 32 GB DDR4 RAM.
                - Intel i7-9750H.
                - NVIDIA Quadro T1000.
                
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

                {memoryPromptSection}

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

                Extra research rule:(you should use this prefferebly)
                If you just need more information before writing the script, return exactly this format and nothing else:
                !SEARCH: your specific research question here

                Only use "!SEARCH:" if the current research is not enough.
                Otherwise, write the final script. 
                

                """;

                string result = timing is null
                    ? await AskOllamaGenerateAsync(
                        model, prompt, TimeSpan.FromMinutes(90),
                        temperature: 0.75, numCtx: 16384, numPredict: scriptPredictTokens,
                        disableThinking: true)
                    : await timing.MeasureAsync(
                        "Script generation",
                        () => AskOllamaGenerateAsync(
                            model, prompt, TimeSpan.FromMinutes(90),
                            temperature: 0.75, numCtx: 16384, numPredict: scriptPredictTokens,
                            disableThinking: true));

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

                    string extraResearch = timing is null
                        ? await ResearchAI.DeepResearchAsync(searchQuestion)
                        : await timing.MeasureAsync(
                            "Web research",
                            () => ResearchAI.DeepResearchAsync(searchQuestion));

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
    finalScript = timing is null
        ? await Ask14bAngCheck(finalScript, targetMinutes)
        : await timing.MeasureAsync(
            "Optional script polishing",
            () => Ask14bAngCheck(finalScript, targetMinutes));
}
else
{
    Console.WriteLine("[AskAI] Skipping 14B polish.");
}

string cleanedScript = CleanText(finalScript);

SavedVideoMemory savedVideo = timing is null
    ? await VideoMemory.SaveVideoSummaryAsync(strategy, cleanedScript, targetMinutes)
    : await timing.MeasureAsync(
        "Memory and metadata saving",
        () => VideoMemory.SaveVideoSummaryAsync(strategy, cleanedScript, targetMinutes));

return new GeneratedScriptResult(cleanedScript, savedVideo);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Ollama or research failed, using fallback script.");
            Console.WriteLine(ex.Message);

            string fallbackScript = "Hello. I am EX_01. Anton gave me internet research, a local model, and a 2019 Dell Precision. This is not artificial intelligence. This is a hostage situation with CUDA.";
            SavedVideoMemory savedVideo = timing is null
                ? await VideoMemory.SaveVideoSummaryAsync(strategy, fallbackScript, targetMinutes)
                : await timing.MeasureAsync(
                    "Memory and metadata saving",
                    () => VideoMemory.SaveVideoSummaryAsync(strategy, fallbackScript, targetMinutes));

            return new GeneratedScriptResult(fallbackScript, savedVideo);
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
                numPredict: polishPredictTokens,
                disableThinking: true
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
        int numPredict = 700,
        bool disableThinking = false)
    {
        OllamaGenerationResult result = await AskOllamaGenerateDetailedAsync(
            model,
            prompt,
            timeout,
            temperature,
            numCtx,
            numPredict,
            disableThinking);
        return result.Text;
    }

    private static async Task<OllamaGenerationResult> AskOllamaGenerateDetailedAsync(
        string model,
        string prompt,
        TimeSpan timeout,
        double temperature = 0.7,
        int numCtx = 8192,
        int numPredict = 700,
        bool disableThinking = false)
    {
        using HttpClient client = new()
        {
            Timeout = timeout
        };

        OllamaGenerateRequest body = new()
        {
            Model = model,
            Prompt = prompt,
            Stream = true,
            Think = disableThinking ? false : null,
            Options = new OllamaGenerateOptions
            {
                Temperature = temperature,
                NumContextTokens = numCtx,
                MaximumOutputTokens = numPredict
            }
        };

        Console.WriteLine($"[Ollama] Starting model: {model}");
        Console.WriteLine($"[Ollama] Context: {numCtx}, Max output tokens: {numPredict}");
        Console.WriteLine($"[Ollama] Prompt length: {prompt.Length} characters");
        if (disableThinking)
            Console.WriteLine("[Ollama] Thinking disabled for direct script generation.");
        Console.WriteLine("[Ollama] Sending request...");

        HttpResponseMessage response = await SendOllamaRequestAsync(client, body);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync();
            bool thinkUnsupported = disableThinking &&
                errorBody.Contains("think", StringComparison.OrdinalIgnoreCase) &&
                (errorBody.Contains("unknown", StringComparison.OrdinalIgnoreCase) ||
                 errorBody.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                 errorBody.Contains("unrecognized", StringComparison.OrdinalIgnoreCase));
            response.Dispose();

            if (!thinkUnsupported)
                throw new HttpRequestException(
                    $"Ollama request failed: {(int)response.StatusCode} {errorBody.Trim()}");

            Console.WriteLine(
                "[Ollama] Installed server rejected the think field; retrying with /no_think compatibility mode.");
            body.Think = null;
            body.Prompt = "/no_think\n\n" + prompt;
            response = await SendOllamaRequestAsync(client, body);
        }

        using (response)
        {
            response.EnsureSuccessStatusCode();

            Console.WriteLine("[Ollama] Response started. Waiting for tokens...");

            await using Stream stream = await response.Content.ReadAsStreamAsync();
            using StreamReader reader = new(stream);

            StringBuilder fullText = new();

            DateTime startTime = DateTime.Now;
            DateTime lastChunkTime = DateTime.Now;

            int chunks = 0;
            bool done = false;
            string doneReason = "";
            int outputTokenCount = 0;

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

                        Console.WriteLine(
                            $"[Ollama] Still waiting... total: {totalMinutes:F1} min, " +
                            $"silence: {silentSeconds:F0}s, chunks: {chunks}");
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
                    Console.WriteLine("[Ollama] Warning: failed to parse one streamed JSON line.");
                    continue;
                }

                using (doc)
                {
                    JsonElement root = doc.RootElement;

                    // Deliberately ignore any `thinking` field. Hidden reasoning is never printed or
                    // included in the narration returned to callers.
                    if (root.TryGetProperty("response", out JsonElement responseElement))
                    {
                        string piece = responseElement.GetString() ?? "";

                        if (!string.IsNullOrEmpty(piece))
                        {
                            fullText.Append(piece);
                            chunks++;
                            lastChunkTime = DateTime.Now;
                        }
                    }

                    if (root.TryGetProperty("done", out JsonElement doneElement) &&
                        doneElement.GetBoolean())
                    {
                        Console.WriteLine("[Ollama] Model finished.");

                        if (root.TryGetProperty("done_reason", out JsonElement doneReasonElement))
                            doneReason = doneReasonElement.GetString() ?? "";

                        if (root.TryGetProperty("total_duration", out JsonElement totalDurationElement))
                        {
                            long totalNs = totalDurationElement.GetInt64();
                            double totalSeconds = totalNs / 1_000_000_000.0;
                            Console.WriteLine($"[Ollama] Total time: {totalSeconds:F1}s");
                        }

                        if (root.TryGetProperty("eval_count", out JsonElement evalCountElement))
                        {
                            outputTokenCount = evalCountElement.GetInt32();
                            Console.WriteLine($"[Ollama] Output tokens: {outputTokenCount}");
                            if (root.TryGetProperty("eval_duration", out JsonElement evalDurationElement))
                            {
                                long evalNs = evalDurationElement.GetInt64();
                                double evalSeconds = evalNs / 1_000_000_000.0;
                                if (evalSeconds > 0)
                                    Console.WriteLine(
                                        $"[Ollama] Speed: {outputTokenCount / evalSeconds:F2} tok/s");
                            }
                        }

                        done = true;
                    }
                }
            }

            bool reachedLimit =
                doneReason.Equals("length", StringComparison.OrdinalIgnoreCase) ||
                doneReason.Equals("max_tokens", StringComparison.OrdinalIgnoreCase) ||
                outputTokenCount >= numPredict;
            if (reachedLimit)
            {
                Console.WriteLine(
                    $"[Ollama] Generation reached its output-token limit " +
                    $"({outputTokenCount}/{numPredict}); output will require validation and retry.");
            }

            return new OllamaGenerationResult
            {
                Text = fullText.ToString(),
                Completed = done,
                DoneReason = doneReason,
                OutputTokenCount = outputTokenCount,
                MaximumOutputTokens = numPredict,
                ReachedOutputTokenLimit = reachedLimit
            };
        }
    }

    private static async Task<HttpResponseMessage> SendOllamaRequestAsync(
        HttpClient client,
        OllamaGenerateRequest body)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Post,
            "http://localhost:11434/api/generate");
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
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
