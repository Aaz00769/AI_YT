using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Infrastructure;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Functions.ASKING;

public sealed class AskAI
{
    public const string ShortPromptVersion = "short-v1-interactive";
    public const string LongPromptVersion = "long-v1-interactive";

    private readonly Ex01Settings _settings;
    private readonly HttpClient _client;

    public AskAI(Ex01Settings settings)
    {
        _settings = settings;
        _client = new HttpClient { Timeout = TimeSpan.FromMinutes(100) };
    }

    public async Task<ScriptGenerationResult> GenerateShortScriptAsync(
        string topic,
        int targetSeconds,
        string extraInstruction,
        string memoryContext,
        int maximumAttempts = 3)
    {
        (int minimumWords, int maximumWords) = ShortScriptValidator.GetShortWordRange(targetSeconds);
        string prompt = BuildShortPrompt(
            topic,
            targetSeconds,
            minimumWords,
            maximumWords,
            extraInstruction,
            memoryContext);

        return await GenerateValidatedScriptCoreAsync(
            prompt,
            minimumWords,
            maximumWords,
            maximumAttempts,
            (attemptPrompt, _) => GenerateAsync(
                _settings.ShortScriptModel,
                attemptPrompt,
                temperature: 0.72,
                numContextTokens: 8192,
                maximumOutputTokens: CalculateShortOutputTokenBudget(maximumWords)));
    }

    public async Task<ScriptGenerationResult> GenerateLongFormScriptAsync(
        string topic,
        int targetMinutes,
        string extraInstruction,
        string memoryContext,
        string research,
        int maximumAttempts = 2)
    {
        (int minimumWords, int maximumWords) = ShortScriptValidator.GetLongFormWordRange(targetMinutes);
        string prompt = BuildLongFormPrompt(
            topic,
            targetMinutes,
            minimumWords,
            maximumWords,
            extraInstruction,
            memoryContext,
            research);

        return await GenerateValidatedScriptCoreAsync(
            prompt,
            minimumWords,
            maximumWords,
            maximumAttempts,
            (attemptPrompt, _) => GenerateAsync(
                _settings.LongScriptModel,
                attemptPrompt,
                temperature: 0.72,
                numContextTokens: 16384,
                maximumOutputTokens: Math.Clamp(maximumWords * 2, 1024, 8192)));
    }

    public async Task<ScriptGenerationResult> PolishLongFormScriptAsync(
        string script,
        int targetMinutes)
    {
        (int minimumWords, int maximumWords) = ShortScriptValidator.GetLongFormWordRange(targetMinutes);
        string prompt = $"""
        /no_think

        Improve this EX_01 spoken YouTube script while preserving its factual claims and meaning.
        Keep it coherent, specific, sarcastic, self-aware, and suitable for narration.
        Remove repetition and generic corporate phrasing. Do not add alleged past events or results.
        Keep it between {minimumWords} and {maximumWords} spoken words.
        Return only the final spoken script. No Markdown, labels, notes, or stage directions.

        SCRIPT:
        {script}
        """;

        return await GenerateValidatedScriptCoreAsync(
            prompt,
            minimumWords,
            maximumWords,
            maximumAttempts: 1,
            (attemptPrompt, _) => GenerateAsync(
                _settings.ShortScriptModel,
                attemptPrompt,
                temperature: 0.55,
                numContextTokens: 16384,
                maximumOutputTokens: Math.Clamp(maximumWords * 2, 1024, 8192)));
    }

    public static int CalculateShortOutputTokenBudget(int maximumWords) =>
        Math.Clamp(maximumWords * 6, 512, 1024);

    public static async Task<ScriptGenerationResult> GenerateValidatedScriptCoreAsync(
        string prompt,
        int minimumWords,
        int maximumWords,
        int maximumAttempts,
        Func<string, int, Task<OllamaGenerationResult>> generateAsync)
    {
        maximumAttempts = Math.Clamp(maximumAttempts, 1, 5);
        Stopwatch stopwatch = Stopwatch.StartNew();
        OllamaGenerationResult? generation = null;
        ScriptValidationResult validation = new();

        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            Console.WriteLine($"Generating script (attempt {attempt}/{maximumAttempts})...");
            string attemptPrompt = attempt == 1
                ? prompt
                : $"""
                  {prompt}

                  The previous response was rejected by local validation:
                  {string.Join(" ", validation.Errors)}

                  Write a completely new, complete response that fixes every problem.
                  """;
            generation = await generateAsync(attemptPrompt, attempt);
            validation = ShortScriptValidator.Validate(
                generation.Text,
                minimumWords,
                maximumWords,
                generation);

            if (validation.Success || attempt == maximumAttempts)
            {
                stopwatch.Stop();
                return new ScriptGenerationResult
                {
                    Script = generation.Text.Trim(),
                    AttemptCount = attempt,
                    MaximumAttempts = maximumAttempts,
                    Elapsed = stopwatch.Elapsed,
                    Validation = validation,
                    Generation = generation
                };
            }

            Console.WriteLine($"Script rejected: {ShortScriptValidator.Describe(validation)}");
        }

        throw new InvalidOperationException("Script generation ended without a result.");
    }

    private async Task<OllamaGenerationResult> GenerateAsync(
        string model,
        string prompt,
        double temperature,
        int numContextTokens,
        int maximumOutputTokens)
    {
        OllamaGenerateRequest body = new()
        {
            Model = model,
            Prompt = prompt,
            Stream = true,
            Think = false,
            Options = new OllamaGenerateOptions
            {
                Temperature = temperature,
                NumContextTokens = numContextTokens,
                MaximumOutputTokens = maximumOutputTokens
            }
        };

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"{_settings.OllamaEndpoint}/api/generate")
        {
            Content = JsonContent.Create(body)
        };
        using HttpResponseMessage response = await _client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using StreamReader reader = new(stream);
        StringBuilder text = new();
        bool completed = false;
        string doneReason = "";
        int outputTokens = 0;

        while (await reader.ReadLineAsync() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error))
                throw new InvalidOperationException(error.GetString() ?? "Ollama generation failed.");
            if (root.TryGetProperty("response", out JsonElement piece))
                text.Append(piece.GetString());
            if (!root.TryGetProperty("done", out JsonElement done) || !done.GetBoolean())
                continue;

            completed = true;
            doneReason = root.TryGetProperty("done_reason", out JsonElement reason)
                ? reason.GetString() ?? ""
                : "";
            outputTokens = root.TryGetProperty("eval_count", out JsonElement count)
                ? count.GetInt32()
                : 0;
            break;
        }

        return new OllamaGenerationResult
        {
            Text = text.ToString().Trim(),
            Completed = completed,
            DoneReason = doneReason,
            OutputTokenCount = outputTokens,
            MaximumOutputTokens = maximumOutputTokens,
            ReachedOutputTokenLimit =
                doneReason.Equals("length", StringComparison.OrdinalIgnoreCase) ||
                outputTokens >= maximumOutputTokens
        };
    }

    private static string BuildShortPrompt(
        string topic,
        int targetSeconds,
        int minimumWords,
        int maximumWords,
        string extraInstruction,
        string memoryContext) => $"""
        /no_think

        You are EX_01, a sarcastic, self-aware local AI YouTuber created by Anton.
        Write one {targetSeconds}-second vertical YouTube Short about this topic:
        {topic}

        Target {minimumWords} to {maximumWords} spoken words.
        Optional direction: {(string.IsNullOrWhiteSpace(extraInstruction) ? "None." : extraInstruction)}

        Character:
        - Technically curious, slightly dramatic, and funny without being nihilistic.
        - Running locally on a Dell Precision 7540, i7-9750H, 32 GB DDR4, and Quadro T1000 4 GB.
        - Anton's questionable engineering decisions are fair material.
        - Use at most one hardware joke unless the topic genuinely needs more.
        - Sound like one character speaking, not a corporate AI assistant.

        Official previous-video context:
        {memoryContext}

        Do not invent completed experiments, crashes, benchmarks, measurements,
        viewer reactions, previous videos, or test results.

        Only describe an event as something that already happened when it is supported
        by supplied project facts, research, or approved previous-video memory.

        Otherwise describe it as a plan, prediction, question, or proposed experiment.

        Requirements:
        - Open with a specific hook, not an introduction or greeting.
        - Explain one idea, with a clear turn or escalation.
        - Finish with a complete, memorable final line.
        - No citations, Markdown, labels, stage directions, placeholders, or commentary.
        - Return only the final spoken narration.
        """;

    private static string BuildLongFormPrompt(
        string topic,
        int targetMinutes,
        int minimumWords,
        int maximumWords,
        string extraInstruction,
        string memoryContext,
        string research) => $"""
        /no_think

        You are EX_01, a sarcastic, self-aware local AI YouTuber created by Anton.
        Write a coherent long-form YouTube commentary script.

        Topic: {topic}
        Target duration: {targetMinutes} minute(s)
        Target length: {minimumWords} to {maximumWords} spoken words
        Optional direction: {(string.IsNullOrWhiteSpace(extraInstruction) ? "None." : extraInstruction)}

        EX_01 runs locally on a Dell Precision 7540 with an i7-9750H, 32 GB DDR4,
        and a Quadro T1000 with 4 GB VRAM. Hardware limitations are character material,
        but do not force the same joke into every section.

        Official previous-video context:
        {memoryContext}

        Research context:
        {(string.IsNullOrWhiteSpace(research) ? "No research was requested. Avoid unsupported factual claims." : research)}

        Do not invent completed experiments, crashes, benchmarks, measurements,
        viewer reactions, previous videos, or test results.

        Only describe an event as something that already happened when it is supported
        by supplied project facts, research, or approved previous-video memory.

        Otherwise describe it as a plan, prediction, question, or proposed experiment.

        Build a strong hook, a clear argument, purposeful sections, and a complete ending.
        Be specific and entertaining without becoming a lore dump.
        Return only spoken narration: no Markdown, headings, labels, citations, or stage directions.
        """;
}
