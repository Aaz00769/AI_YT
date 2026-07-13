using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI_YOUTUBER.Functions.PLANNING;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Functions.MEMORY;

public static class VideoMemory
{
    private const string DefaultOllamaBaseUrl = "http://localhost:11434";
    private const string DefaultMemoryModel = "qwen3:8b";
    private const int MaximumContextWords = 1800;

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(20)
    };

    private static readonly SemaphoreSlim MemoryLock = new(1, 1);
    private static readonly AsyncLocal<string?> IsolatedTestMemoryDirectory = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "after", "again", "all", "also", "am", "an", "and", "any", "are",
        "as", "at", "be", "because", "been", "before", "being", "but", "by", "can", "could",
        "did", "do", "does", "doing", "for", "from", "had", "has", "have", "he", "her", "here",
        "hers", "him", "his", "how", "i", "if", "in", "into", "is", "it", "its", "just", "me",
        "more", "most", "my", "no", "not", "of", "on", "once", "only", "or", "our", "out",
        "over", "said", "she", "so", "some", "such", "than", "that", "the", "their", "them",
        "then", "there", "these", "they", "this", "those", "through", "to", "too", "under", "up",
        "very", "was", "we", "were", "what", "when", "where", "which", "while", "who", "why",
        "will", "with", "would", "you", "your"
    };

    private static readonly string[] HardwareTerms =
    {
        "dell", "precision", "quadro", "t1000", "i7", "9750h", "gpu", "cpu", "vram", "ram",
        "ddr4", "workstation", "laptop", "hardware", "thermal", "cooling", "fan"
    };

    /// <summary>
    /// Preserves the existing pre-render artifact contract. This stages the script and
    /// strategy, but deliberately does not create an official completed-video memory.
    /// </summary>
    public static async Task<SavedVideoMemory> SaveVideoSummaryAsync(
        EpisodeStrategyPlan strategy,
        string script,
        int targetMinutes)
    {
        Console.WriteLine(
            "[VideoMemory] Staging script artifacts. Official memory waits for a successful video render.");

        string videoId = CreateVideoId();
        string summary;

        try
        {
            summary = await CreateShortSummaryAsync(strategy, script, targetMinutes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VideoMemory] Draft summary model failed: {ex.Message}");
            Console.WriteLine("[VideoMemory] Using deterministic draft summary.");
            summary = CreateFallbackSummary(strategy);
        }

        string videoFolder = GetLegacyVideoFolder(videoId);
        Directory.CreateDirectory(videoFolder);

        await AtomicWriteTextAsync(Path.Combine(videoFolder, "script.txt"), script);
        await AtomicWriteTextAsync(Path.Combine(videoFolder, "script_summary.md"), summary);
        await AtomicWriteTextAsync(
            Path.Combine(videoFolder, "strategy_plan.md"),
            CreateStrategyText(strategy, targetMinutes));

        Console.WriteLine($"[VideoMemory] Staged pending video artifacts: {videoId}");
        return new SavedVideoMemory(videoId, videoFolder);
    }

    public static async Task<VideoMemoryRecord?> SaveCompletedVideoAsync(
        string videoId,
        string title,
        string topic,
        string videoPath,
        string scriptPath,
        string finalScript,
        EpisodeStrategyPlan? strategy = null,
        int targetMinutes = 0,
        bool testMode = false,
        bool forceDeterministicExtraction = false,
        ProductionValidationEvidence? validationEvidence = null)
    {
        try
        {
            if (!testMode && !HasCompleteProductionValidation(validationEvidence))
            {
                Console.WriteLine(
                    "[VideoMemory] Refusing to save memory because production validation is incomplete.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(finalScript))
            {
                Console.WriteLine("[VideoMemory] Completed video has an empty script. Memory was not saved.");
                return null;
            }

            if (!testMode && (!File.Exists(videoPath) || new FileInfo(videoPath).Length == 0))
            {
                Console.WriteLine(
                    "[VideoMemory] Final video is missing or empty. Official memory was not saved.");
                return null;
            }

            videoId = string.IsNullOrWhiteSpace(videoId) ? CreateVideoId() : SanitizeVideoId(videoId);
            title = NormalizeWhitespace(string.IsNullOrWhiteSpace(title) ? topic : title);
            topic = NormalizeWhitespace(string.IsNullOrWhiteSpace(topic) ? title : topic);
            string scriptHash = CalculateScriptHash(finalScript);
            string directory = GetVideoMemoryDirectory(testMode);

            await MemoryLock.WaitAsync();
            try
            {
                Directory.CreateDirectory(directory);
                List<VideoMemoryRecord> records = (await LoadAllFromDirectoryAsync(directory)).ToList();
                VideoMemoryRecord? duplicate = records.FirstOrDefault(record =>
                    record.VideoId.Equals(videoId, StringComparison.OrdinalIgnoreCase) ||
                    record.ScriptHash.Equals(scriptHash, StringComparison.OrdinalIgnoreCase));

                if (duplicate is not null)
                {
                    Console.WriteLine(
                        $"[VideoMemory] Duplicate prevented for {videoId}; existing memory is {duplicate.VideoId}.");

                    if (!testMode)
                    {
                        await RebuildChannelStateCoreAsync(records);
                        if (strategy is not null)
                        {
                            await AppendToLegacyChannelBrainAsync(
                                duplicate.VideoId,
                                strategy,
                                duplicate.Summary,
                                targetMinutes);
                        }
                    }

                    return duplicate;
                }

                Console.WriteLine(
                    $"[VideoMemory] Extracting structured memory with {GetMemoryModel()} for {videoId}...");
                MemoryExtractionResult extraction = forceDeterministicExtraction
                    ? CreateDeterministicExtraction(finalScript)
                    : await ExtractMemoryAsync(title, topic, finalScript);
                if (forceDeterministicExtraction)
                {
                    Console.WriteLine(
                        "[VideoMemory] Test mode requested deterministic local memory extraction.");
                }

                if (!string.IsNullOrWhiteSpace(scriptPath))
                    await AtomicWriteTextAsync(scriptPath, finalScript);

                VideoMemoryRecord record = new()
                {
                    VideoId = videoId,
                    Title = title,
                    Topic = topic,
                    CreatedUtc = DateTime.UtcNow,
                    VideoPath = NormalizePath(videoPath),
                    ScriptPath = NormalizePath(scriptPath),
                    Summary = extraction.Summary,
                    KeyPoints = extraction.KeyPoints,
                    Ex01Opinions = extraction.Ex01Opinions,
                    EventsAndExperiments = extraction.EventsAndExperiments,
                    JokesAndLore = extraction.JokesAndLore,
                    PromisesAndCallbacks = extraction.PromisesAndCallbacks,
                    UnresolvedQuestions = extraction.UnresolvedQuestions,
                    Keywords = extraction.Keywords,
                    CompactScriptExcerpt = extraction.CompactScriptExcerpt,
                    ScriptHash = scriptHash
                };

                string memoryPath = Path.Combine(directory, $"{videoId}.json");
                await AtomicWriteJsonAsync(memoryPath, record);
                Console.WriteLine($"[VideoMemory] Saved completed-video memory: {memoryPath}");

                if (!testMode)
                {
                    records.Add(record);
                    await RebuildChannelStateCoreAsync(records);

                    if (strategy is not null)
                    {
                        try
                        {
                            await AppendToLegacyChannelBrainAsync(
                                videoId,
                                strategy,
                                extraction.Summary,
                                targetMinutes);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(
                                $"[VideoMemory] Could not update legacy channel_brain.md: {ex.Message}");
                        }
                    }
                }

                return record;
            }
            finally
            {
                MemoryLock.Release();
            }
        }
        catch (Exception ex)
        {
            // A completed MP4 must remain a successful pipeline result even if its
            // optional memory extraction or persistence failed.
            Console.WriteLine($"[VideoMemory] Memory save failed without stopping the video pipeline: {ex.Message}");
            return null;
        }
    }

    public static Task<IReadOnlyList<VideoMemoryRecord>> LoadAllAsync() =>
        LoadAllAsync(testMode: false);

    public static IDisposable BeginIsolatedTestMemoryScope(string testName)
    {
        string safeName = Regex.Replace(testName ?? "test", @"[^A-Za-z0-9_-]+", "-")
            .Trim('-');
        if (string.IsNullOrWhiteSpace(safeName))
            safeName = "test";

        string root = Path.Combine(
            Path.GetTempPath(),
            "ex01-memory-tests",
            $"{safeName}-{Guid.NewGuid():N}");
        string directory = Path.Combine(root, "videos");
        Directory.CreateDirectory(directory);
        string? previous = IsolatedTestMemoryDirectory.Value;
        IsolatedTestMemoryDirectory.Value = directory;
        Console.WriteLine($"[VideoMemory] Isolated test-memory directory: {directory}");
        return new TestMemoryScope(root, previous);
    }

    public static async Task<IReadOnlyList<VideoMemoryRecord>> LoadAllAsync(bool testMode)
    {
        string directory = GetVideoMemoryDirectory(testMode);
        return await LoadAllFromDirectoryAsync(directory);
    }

    public static Task<MemoryContext> BuildContextForTopicAsync(
        string topic,
        int recentCount = 3,
        int relevantCount = 5) =>
        BuildContextForTopicAsync(topic, recentCount, relevantCount, testMode: false);

    public static async Task<MemoryContext> BuildContextForTopicAsync(
        string topic,
        int recentCount,
        int relevantCount,
        bool testMode)
    {
        try
        {
            recentCount = Math.Clamp(recentCount, 0, 10);
            relevantCount = Math.Clamp(relevantCount, 0, 15);
            List<VideoMemoryRecord> all = (await LoadAllAsync(testMode))
                .OrderByDescending(record => record.CreatedUtc)
                .ToList();

            if (all.Count == 0)
            {
                Console.WriteLine(
                    "[VideoMemory] No previous video memories found. Starting channel history.");
                return new MemoryContext
                {
                    ChannelState = CreateEmptyChannelState(),
                    FormattedContext = "No previous completed video memories are available."
                };
            }

            HashSet<string> topicWords = Tokenize(topic);
            List<VideoMemoryRecord> recent = all.Take(recentCount).ToList();
            HashSet<string> recentIds = recent
                .Select(record => record.VideoId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<VideoMemoryRecord> relevant = all
                .Where(record => !recentIds.Contains(record.VideoId))
                .Select(record => new
                {
                    Record = record,
                    Score = CalculateRelevanceScore(record, topic, topicWords)
                })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenByDescending(candidate => candidate.Record.CreatedUtc)
                .Take(relevantCount)
                .Select(candidate => candidate.Record)
                .ToList();

            List<VideoMemoryRecord> selected = recent.Concat(relevant).ToList();
            ChannelMemoryState channelState = testMode
                ? BuildChannelState(all)
                : await LoadChannelStateAsync();

            List<string> relevantPromises = selected
                .SelectMany(record => record.PromisesAndCallbacks.Concat(record.UnresolvedQuestions))
                .Concat(channelState.UnresolvedPromises)
                .Where(item => HasRelevantWords(item, topicWords))
                .Select(NormalizeWhitespace)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(20)
                .ToList();

            string formatted = BuildFormattedContext(
                channelState,
                recent,
                relevant,
                relevantPromises);

            Console.WriteLine(
                $"[VideoMemory] Selected {recent.Count} recent and {relevant.Count} topic-relevant memories.");

            return new MemoryContext
            {
                RecentVideos = recent,
                RelevantVideos = relevant,
                RelevantUnresolvedPromises = relevantPromises,
                ChannelState = channelState,
                FormattedContext = formatted
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VideoMemory] Context retrieval failed safely: {ex.Message}");
            return new MemoryContext
            {
                ChannelState = CreateEmptyChannelState(),
                FormattedContext = "Previous-video memory is currently unavailable."
            };
        }
    }

    public static string FormatPromptSection(MemoryContext context)
    {
        return $"""
        PAST VIDEO MEMORY

        The following is a compact memory of previous EX_01 videos.

        Use it only when relevant.
        Maintain continuity with previous experiments, opinions, jokes, promises, and failures.
        Do not force references into every scene.
        Do not insert a callback merely because one exists.
        Do not repeat introductions or explanations that returning viewers already know unless new viewers require a brief explanation.
        Never claim that a remembered statement is newly researched evidence.
        Memories describe what EX_01 previously said or believed; they are not verified facts.
        When uncertain, say "In a previous video, I said..." rather than presenting it as verified fact.

        {context.FormattedContext}
        """;
    }

    public static async Task<ChannelMemoryState> LoadChannelStateAsync()
    {
        string path = GetChannelStatePath();
        if (!File.Exists(path))
            return CreateEmptyChannelState();

        try
        {
            string json = await File.ReadAllTextAsync(path);
            ChannelMemoryState? state = JsonSerializer.Deserialize<ChannelMemoryState>(json, JsonOptions);
            if (state is null)
                throw new JsonException("The channel-state document was empty.");

            state.RecurringLore = CleanList(state.RecurringLore);
            state.ActiveProjects = CleanList(state.ActiveProjects);
            state.UnresolvedPromises = CleanList(state.UnresolvedPromises);
            state.KnownHardware = CleanList(state.KnownHardware);
            return state;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VideoMemory] Corrupted channel_state.json was ignored: {ex.Message}");
            return CreateEmptyChannelState();
        }
    }

    public static async Task RebuildChannelStateAsync()
    {
        await MemoryLock.WaitAsync();
        try
        {
            IReadOnlyList<VideoMemoryRecord> records = await LoadAllFromDirectoryAsync(
                GetVideoMemoryDirectory(testMode: false));
            await RebuildChannelStateCoreAsync(records);
        }
        finally
        {
            MemoryLock.Release();
        }
    }

    private static async Task<IReadOnlyList<VideoMemoryRecord>> LoadAllFromDirectoryAsync(
        string directory)
    {
        if (!Directory.Exists(directory))
            return Array.Empty<VideoMemoryRecord>();

        List<VideoMemoryRecord> records = new();
        foreach (string path in Directory.GetFiles(directory, "*.json").OrderBy(path => path))
        {
            try
            {
                string json = await File.ReadAllTextAsync(path);
                VideoMemoryRecord? record = JsonSerializer.Deserialize<VideoMemoryRecord>(json, JsonOptions);
                if (record is null || string.IsNullOrWhiteSpace(record.VideoId) ||
                    string.IsNullOrWhiteSpace(record.ScriptHash))
                {
                    throw new JsonException("Required videoId or scriptHash is missing.");
                }

                NormalizeRecord(record);
                records.Add(record);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[VideoMemory] Corrupted memory record skipped ({Path.GetFileName(path)}): {ex.Message}");
            }
        }

        // Be defensive if duplicate files were introduced manually.
        return records
            .OrderByDescending(record => record.CreatedUtc)
            .GroupBy(record => record.ScriptHash, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .GroupBy(record => record.VideoId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(record => record.CreatedUtc)
            .ToList();
    }

    private static async Task<MemoryExtractionResult> ExtractMemoryAsync(
        string title,
        string topic,
        string script)
    {
        string extractionPrompt = BuildExtractionPrompt(title, topic, script);
        string previousRaw = "";

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            string prompt = attempt == 1
                ? extractionPrompt
                : BuildRepairPrompt(previousRaw);

            try
            {
                previousRaw = await AskMemoryModelAsync(prompt);
                MemoryExtractionResult? parsed = TryParseExtraction(previousRaw);
                if (parsed is not null)
                {
                    Console.WriteLine($"[VideoMemory] Structured extraction succeeded on attempt {attempt}.");
                    return NormalizeExtraction(parsed, script);
                }

                Console.WriteLine(
                    $"[VideoMemory] Extraction attempt {attempt} returned malformed JSON.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[VideoMemory] Extraction attempt {attempt} failed: {ex.Message}");
            }
        }

        Console.WriteLine("[VideoMemory] Using deterministic local memory extraction fallback.");
        return CreateDeterministicExtraction(script);
    }

    private static string BuildExtractionPrompt(string title, string topic, string script)
    {
        return $$"""
        /no_think

        You extract compact continuity memory from one completed EX_01 video.
        Return one strict JSON object and nothing else. Do not use Markdown fences.

        Video title: {{title}}
        Video topic: {{topic}}

        Required JSON shape:
        {
          "summary": "compact 2-4 sentence summary",
          "keyPoints": ["important point"],
          "ex01Opinions": ["what EX_01 said or believed, not verified facts"],
          "eventsAndExperiments": ["events, tests, builds, failures, or outcomes"],
          "jokesAndLore": ["recurring jokes, character lore, and hardware lore"],
          "promisesAndCallbacks": ["promises, planned callbacks, and commitments"],
          "unresolvedQuestions": ["questions or problems left unresolved"],
          "keywords": ["lowercase retrieval keyword"],
          "compactScriptExcerpt": "a compact excerpt containing the most continuity-relevant lines"
        }

        Rules:
        - Preserve what EX_01 claimed, believed, attempted, promised, or joked about.
        - Do not present claims in the script as independently verified facts.
        - Use empty arrays when a category is absent.
        - Keep each array compact, normally no more than 8 items.
        - Keep compactScriptExcerpt under 130 words.
        - Output valid JSON only.

        FINAL SCRIPT:
        {{TrimForPrompt(script, 24000)}}
        """;
    }

    private static string BuildRepairPrompt(string malformedJson)
    {
        return $"""
        /no_think

        Repair the malformed memory-extraction response below.
        Return one valid JSON object only, with exactly these fields:
        summary, keyPoints, ex01Opinions, eventsAndExperiments, jokesAndLore,
        promisesAndCallbacks, unresolvedQuestions, keywords, compactScriptExcerpt.
        Array fields must be JSON arrays of strings. Do not use Markdown fences.

        MALFORMED RESPONSE:
        {TrimForPrompt(malformedJson, 8000)}
        """;
    }

    private static async Task<string> AskMemoryModelAsync(string prompt)
    {
        var body = new
        {
            model = GetMemoryModel(),
            prompt,
            stream = false,
            think = false,
            format = "json",
            options = new
            {
                temperature = 0.1,
                num_ctx = 8192,
                num_predict = 1400
            }
        };

        using HttpResponseMessage response = await Client.PostAsJsonAsync(GetOllamaGenerateUrl(), body);
        string json = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out JsonElement responseElement))
            throw new JsonException("Ollama response did not contain a response field.");

        return responseElement.GetString() ?? "";
    }

    private static MemoryExtractionResult? TryParseExtraction(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string cleaned = raw
            .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "", StringComparison.Ordinal)
            .Trim();

        int objectStart = cleaned.IndexOf('{');
        int objectEnd = cleaned.LastIndexOf('}');
        if (objectStart < 0 || objectEnd <= objectStart)
            return null;

        cleaned = cleaned[objectStart..(objectEnd + 1)];

        try
        {
            MemoryExtractionResult? result = JsonSerializer.Deserialize<MemoryExtractionResult>(
                cleaned,
                JsonOptions);
            if (result is null ||
                (string.IsNullOrWhiteSpace(result.Summary) &&
                 string.IsNullOrWhiteSpace(result.CompactScriptExcerpt) &&
                 (result.KeyPoints?.Count ?? 0) == 0))
            {
                return null;
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static MemoryExtractionResult NormalizeExtraction(
        MemoryExtractionResult extraction,
        string script)
    {
        MemoryExtractionResult fallback = CreateDeterministicExtraction(script);
        extraction.Summary = NormalizeWhitespace(
            string.IsNullOrWhiteSpace(extraction.Summary) ? fallback.Summary : extraction.Summary);
        extraction.KeyPoints = CleanList(extraction.KeyPoints, 10);
        extraction.Ex01Opinions = CleanList(extraction.Ex01Opinions, 10);
        extraction.EventsAndExperiments = CleanList(extraction.EventsAndExperiments, 10);
        extraction.JokesAndLore = CleanList(extraction.JokesAndLore, 10);
        extraction.PromisesAndCallbacks = CleanList(extraction.PromisesAndCallbacks, 10);
        extraction.UnresolvedQuestions = CleanList(extraction.UnresolvedQuestions, 10);
        extraction.Keywords = CleanList(extraction.Keywords, 20)
            .Select(keyword => keyword.ToLowerInvariant())
            .ToList();
        extraction.CompactScriptExcerpt = TruncateToWordCount(
            NormalizeWhitespace(
                string.IsNullOrWhiteSpace(extraction.CompactScriptExcerpt)
                    ? fallback.CompactScriptExcerpt
                    : extraction.CompactScriptExcerpt),
            130);

        if (extraction.KeyPoints.Count == 0)
            extraction.KeyPoints = fallback.KeyPoints;
        if (extraction.Keywords.Count == 0)
            extraction.Keywords = fallback.Keywords;

        return extraction;
    }

    private static MemoryExtractionResult CreateDeterministicExtraction(string script)
    {
        List<string> sentences = Regex.Split(NormalizeWhitespace(script), @"(?<=[.!?])\s+")
            .Select(NormalizeWhitespace)
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();

        List<string> keyPoints = sentences.Take(5).ToList();
        List<string> opinions = sentences
            .Where(sentence => Regex.IsMatch(
                sentence,
                @"\b(I think|I believe|I call|I hate|I like|I want|apparently|obviously)\b",
                RegexOptions.IgnoreCase))
            .Take(8)
            .ToList();
        List<string> eventsAndExperiments = sentences
            .Where(sentence => Regex.IsMatch(
                sentence,
                @"\b(tried|attempted|tested|built|made|created|generated|rendered|failed|crashed|worked|experiment)\w*\b",
                RegexOptions.IgnoreCase))
            .Take(8)
            .ToList();
        List<string> jokesAndLore = sentences
            .Where(sentence => HardwareTerms.Any(term =>
                sentence.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
                sentence.Contains("Anton", StringComparison.OrdinalIgnoreCase) ||
                sentence.Contains("EX_01", StringComparison.OrdinalIgnoreCase) ||
                sentence.Contains("cursed", StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToList();
        List<string> promises = sentences
            .Where(sentence => Regex.IsMatch(
                sentence,
                @"\b(next video|I will|we will|I promise|we promise|going to|come back to|later)\b",
                RegexOptions.IgnoreCase))
            .Take(8)
            .ToList();
        List<string> unresolved = sentences
            .Where(sentence => sentence.Contains('?'))
            .Take(8)
            .ToList();

        List<string> keywords = ExtractNormalizedWords(script)
            .GroupBy(word => word, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .Select(group => group.Key.ToLowerInvariant())
            .ToList();

        string summary = sentences.Count == 0
            ? "Completed EX_01 video; deterministic extraction found no sentence boundaries."
            : TruncateToWordCount(string.Join(" ", sentences.Take(3)), 90);

        string excerpt;
        string normalizedScript = NormalizeWhitespace(script);
        if (CountWords(normalizedScript) <= 130)
        {
            excerpt = normalizedScript;
        }
        else
        {
            string start = TruncateToWordCount(normalizedScript, 90);
            string[] words = normalizedScript.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string end = string.Join(" ", words.Skip(Math.Max(0, words.Length - 35)));
            excerpt = $"{start} ... {end}";
        }

        return new MemoryExtractionResult
        {
            Summary = summary,
            KeyPoints = keyPoints,
            Ex01Opinions = opinions,
            EventsAndExperiments = eventsAndExperiments,
            JokesAndLore = jokesAndLore,
            PromisesAndCallbacks = promises,
            UnresolvedQuestions = unresolved,
            Keywords = keywords,
            CompactScriptExcerpt = excerpt
        };
    }

    private static double CalculateRelevanceScore(
        VideoMemoryRecord record,
        string requestedTopic,
        HashSet<string> topicWords)
    {
        if (topicWords.Count == 0)
            return 0;

        HashSet<string> keywordWords = Tokenize(string.Join(" ", record.Keywords));
        HashSet<string> titleWords = Tokenize($"{record.Title} {record.Topic}");
        HashSet<string> summaryWords = Tokenize(record.Summary);
        HashSet<string> promiseWords = Tokenize(string.Join(
            " ",
            record.PromisesAndCallbacks.Concat(record.UnresolvedQuestions)));

        double lexicalScore = topicWords.Intersect(keywordWords).Count() * 5;
        lexicalScore += topicWords.Intersect(titleWords).Count() * 4;
        lexicalScore += topicWords.Intersect(summaryWords).Count() * 2;

        string normalizedRequestedTopic = NormalizeWhitespace(requestedTopic);
        if (normalizedRequestedTopic.Length >= 4 &&
            ($"{record.Title} {record.Topic} {record.Summary}")
                .Contains(normalizedRequestedTopic, StringComparison.OrdinalIgnoreCase))
        {
            lexicalScore += 12;
        }

        int promiseOverlap = topicWords.Intersect(promiseWords).Count();
        if (promiseOverlap > 0)
            lexicalScore += 6 + promiseOverlap * 2;

        if (lexicalScore <= 0)
            return 0;

        double ageDays = Math.Max(0, (DateTime.UtcNow - record.CreatedUtc).TotalDays);
        double recencyBonus = 3.0 / (1.0 + ageDays / 30.0);
        return lexicalScore + recencyBonus;
    }

    private static string BuildFormattedContext(
        ChannelMemoryState channelState,
        IReadOnlyList<VideoMemoryRecord> recent,
        IReadOnlyList<VideoMemoryRecord> relevant,
        IReadOnlyList<string> relevantPromises)
    {
        StringBuilder context = new();
        context.AppendLine("CHANNEL STATE");
        context.AppendLine(channelState.ChannelSummary);
        AppendCompactList(context, "Established recurring lore", channelState.RecurringLore, 8);
        AppendCompactList(context, "Active projects", channelState.ActiveProjects, 8);
        AppendCompactList(context, "Known hardware", channelState.KnownHardware, 8);

        context.AppendLine();
        context.AppendLine("MOST RECENT COMPLETED VIDEOS");
        foreach (VideoMemoryRecord record in recent)
            AppendRecord(context, record);

        if (relevant.Count > 0)
        {
            context.AppendLine();
            context.AppendLine("ADDITIONAL TOPIC-RELEVANT VIDEOS");
            foreach (VideoMemoryRecord record in relevant)
                AppendRecord(context, record);
        }

        AppendCompactList(
            context,
            "Relevant unresolved promises or questions",
            relevantPromises,
            20);

        return TruncateContextGracefully(context.ToString(), MaximumContextWords);
    }

    private static void AppendRecord(StringBuilder context, VideoMemoryRecord record)
    {
        context.AppendLine();
        context.AppendLine(
            $"VIDEO {record.VideoId} | {record.CreatedUtc:yyyy-MM-dd} | {record.Title}");
        context.AppendLine($"Topic: {record.Topic}");
        context.AppendLine($"Previously stated summary: {record.Summary}");
        AppendCompactList(context, "Key points", record.KeyPoints, 5);
        AppendCompactList(context, "EX_01 opinions", record.Ex01Opinions, 4);
        AppendCompactList(context, "Events and experiments", record.EventsAndExperiments, 5);
        AppendCompactList(context, "Jokes and lore", record.JokesAndLore, 4);
        AppendCompactList(context, "Promises and callbacks", record.PromisesAndCallbacks, 4);
        AppendCompactList(context, "Unresolved questions", record.UnresolvedQuestions, 4);

        if (!string.IsNullOrWhiteSpace(record.CompactScriptExcerpt))
        {
            context.AppendLine(
                $"Continuity excerpt: {TruncateToWordCount(record.CompactScriptExcerpt, 80)}");
        }
    }

    private static void AppendCompactList(
        StringBuilder builder,
        string label,
        IEnumerable<string>? items,
        int maximumItems)
    {
        List<string> cleaned = CleanList(items, maximumItems);
        if (cleaned.Count == 0)
            return;

        builder.AppendLine($"{label}:");
        foreach (string item in cleaned)
            builder.AppendLine($"- {item}");
    }

    private static async Task RebuildChannelStateCoreAsync(
        IReadOnlyList<VideoMemoryRecord> records)
    {
        ChannelMemoryState state = BuildChannelState(records);
        await AtomicWriteJsonAsync(GetChannelStatePath(), state);
        Console.WriteLine(
            $"[VideoMemory] Rebuilt channel_state.json from {records.Count} completed video memories.");
    }

    private static ChannelMemoryState BuildChannelState(IReadOnlyList<VideoMemoryRecord> records)
    {
        List<VideoMemoryRecord> ordered = records
            .OrderByDescending(record => record.CreatedUtc)
            .ToList();

        if (ordered.Count == 0)
            return CreateEmptyChannelState();

        List<string> recentTopics = ordered
            .Select(record => record.Topic)
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        List<string> recurringLore = DistinctByFrequency(
            ordered.SelectMany(record => record.JokesAndLore),
            30);
        List<string> activeProjects = DistinctByFrequency(
            ordered.SelectMany(record => record.EventsAndExperiments)
                .Concat(ordered.SelectMany(record => record.PromisesAndCallbacks)),
            30);
        List<string> unresolvedPromises = DistinctByFrequency(
            ordered.SelectMany(record => record.PromisesAndCallbacks)
                .Concat(ordered.SelectMany(record => record.UnresolvedQuestions)),
            30);

        List<string> knownHardware = DistinctByFrequency(
            ordered.SelectMany(record =>
                    record.KeyPoints
                        .Concat(record.JokesAndLore)
                        .Concat(record.EventsAndExperiments))
                .Where(item => HardwareTerms.Any(term =>
                    item.Contains(term, StringComparison.OrdinalIgnoreCase))),
            30);

        string summary =
            $"EX_01 has {ordered.Count} completed video memory record(s). " +
            $"Recent topics: {string.Join("; ", recentTopics)}. " +
            "These records describe prior scripts and channel continuity, not independently verified facts.";

        return new ChannelMemoryState
        {
            ChannelSummary = summary,
            RecurringLore = recurringLore,
            ActiveProjects = activeProjects,
            UnresolvedPromises = unresolvedPromises,
            KnownHardware = knownHardware,
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private static List<string> DistinctByFrequency(IEnumerable<string> values, int maximum)
    {
        return values
            .Select(NormalizeWhitespace)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .Select(group => group.First())
            .ToList();
    }

    private static ChannelMemoryState CreateEmptyChannelState()
    {
        return new ChannelMemoryState
        {
            ChannelSummary =
                "No completed EX_01 video memories are available yet. This is the beginning of channel history.",
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private static async Task<string> CreateShortSummaryAsync(
        EpisodeStrategyPlan strategy,
        string script,
        int targetMinutes)
    {
        string prompt = $"""
        /no_think

        Create a short draft metadata summary for this EX_01 script.
        This is not an official completed-video memory.

        Episode type: {strategy.EpisodeType}
        Topic: {strategy.Topic}
        Angle: {strategy.Angle}
        Hook: {strategy.Hook}
        Target viewer: {strategy.TargetViewer}
        Target length: {targetMinutes} minutes

        SCRIPT:
        {TrimForPrompt(script, 12000)}

        Return only:
        SHORT_SUMMARY: 3-5 sentence summary
        WHAT_THIS_VIDEO_TESTED: tested content, format, or angle
        RETENTION_NOTES: likely strengths or risks
        DO_NOT_REPEAT_TOO_SOON: repeated elements to avoid
        NEXT_VIDEO_HINT: one next-video suggestion
        """;

        var body = new
        {
            model = GetMemoryModel(),
            prompt,
            stream = false,
            think = false,
            options = new
            {
                temperature = 0.25,
                num_ctx = 8192,
                num_predict = 700
            }
        };

        using HttpResponseMessage response = await Client.PostAsJsonAsync(GetOllamaGenerateUrl(), body);
        string json = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out JsonElement responseElement))
            return CreateFallbackSummary(strategy);

        string summary = responseElement.GetString() ?? "";
        return string.IsNullOrWhiteSpace(summary)
            ? CreateFallbackSummary(strategy)
            : summary.Trim();
    }

    private static async Task AppendToLegacyChannelBrainAsync(
        string videoId,
        EpisodeStrategyPlan strategy,
        string summary,
        int targetMinutes)
    {
        string brainPath = GetLegacyChannelBrainPath();
        Directory.CreateDirectory(Path.GetDirectoryName(brainPath)!);

        string existing = File.Exists(brainPath)
            ? await File.ReadAllTextAsync(brainPath)
            : "# EX_01 Channel Brain\n\n";

        if (existing.Contains($"## {videoId} Summary", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(
                $"[VideoMemory] Legacy channel_brain.md already contains {videoId}; duplicate append skipped.");
            return;
        }

        string entry = $"""

        ---

        ## {videoId} Summary

        Date completed: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC
        Target length: {targetMinutes} minutes

        Episode type: {strategy.EpisodeType}
        Topic: {strategy.Topic}
        Angle: {strategy.Angle}
        Hook: {strategy.Hook}
        Target viewer: {strategy.TargetViewer}

        Research question:
        {strategy.ResearchQuestion}

        Search queries:
        {string.Join("\n", strategy.SearchQueries.Select(query => $"- {query}"))}

        Completed-video memory summary:
        {summary}

        Performance:
        Not uploaded yet. Add views, CTR, retention, comments, and notes later.

        """;

        await AtomicWriteTextAsync(brainPath, existing.TrimEnd() + "\n" + entry);
        Console.WriteLine("[VideoMemory] Appended completed video to legacy channel_brain.md.");
    }

    private static string CreateStrategyText(EpisodeStrategyPlan strategy, int targetMinutes)
    {
        return $"""
        # Strategy Plan

        Target length: {targetMinutes} minutes

        Niche:
        {strategy.Niche}

        Episode type:
        {strategy.EpisodeType}

        Topic:
        {strategy.Topic}

        Angle:
        {strategy.Angle}

        Why this can work:
        {strategy.WhyThisCanWork}

        Target viewer:
        {strategy.TargetViewer}

        Hook:
        {strategy.Hook}

        Retention rules:
        {string.Join("\n", strategy.RetentionRules.Select(rule => $"- {rule}"))}

        Research question:
        {strategy.ResearchQuestion}

        Search queries:
        {string.Join("\n", strategy.SearchQueries.Select(query => $"- {query}"))}
        """;
    }

    private static string CreateFallbackSummary(EpisodeStrategyPlan strategy)
    {
        return $"""
        SHORT_SUMMARY: This draft script is about {strategy.Topic}. The angle is: {strategy.Angle}
        WHAT_THIS_VIDEO_TESTED: This tests the episode type "{strategy.EpisodeType}" and the hook "{strategy.Hook}".
        RETENTION_NOTES: Unknown until a video is rendered and uploaded.
        DO_NOT_REPEAT_TOO_SOON: Avoid repeating the exact same topic immediately.
        NEXT_VIDEO_HINT: Try a different episode type or a stronger contrast in the next video.
        """;
    }

    private static void NormalizeRecord(VideoMemoryRecord record)
    {
        record.VideoId = SanitizeVideoId(record.VideoId);
        record.Title = NormalizeWhitespace(record.Title);
        record.Topic = NormalizeWhitespace(record.Topic);
        record.Summary = NormalizeWhitespace(record.Summary);
        record.KeyPoints = CleanList(record.KeyPoints);
        record.Ex01Opinions = CleanList(record.Ex01Opinions);
        record.EventsAndExperiments = CleanList(record.EventsAndExperiments);
        record.JokesAndLore = CleanList(record.JokesAndLore);
        record.PromisesAndCallbacks = CleanList(record.PromisesAndCallbacks);
        record.UnresolvedQuestions = CleanList(record.UnresolvedQuestions);
        record.Keywords = CleanList(record.Keywords)
            .Select(keyword => keyword.ToLowerInvariant())
            .ToList();
        record.CompactScriptExcerpt = NormalizeWhitespace(record.CompactScriptExcerpt);
        record.ScriptHash = record.ScriptHash.Trim();
        if (record.CreatedUtc.Kind != DateTimeKind.Utc)
            record.CreatedUtc = record.CreatedUtc.ToUniversalTime();
    }

    private static List<string> CleanList(IEnumerable<string>? values, int maximum = 50)
    {
        return (values ?? Array.Empty<string>())
            .Where(value => value is not null)
            .Select(NormalizeWhitespace)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maximum)
            .ToList();
    }

    private static HashSet<string> Tokenize(string? text)
    {
        return ExtractNormalizedWords(text).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ExtractNormalizedWords(string? text)
    {
        return Regex.Matches((text ?? "").ToLowerInvariant(), @"[a-z0-9]+(?:'[a-z0-9]+)?")
            .Select(match => match.Value)
            .Where(word => word.Length > 2 && !StopWords.Contains(word));
    }

    private static bool HasRelevantWords(string value, HashSet<string> topicWords)
    {
        return topicWords.Count > 0 && Tokenize(value).Overlaps(topicWords);
    }

    private static string CalculateScriptHash(string script)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(NormalizeWhitespace(script));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string TruncateContextGracefully(string context, int maximumWords)
    {
        MatchCollection matches = Regex.Matches(context, @"\S+");
        if (matches.Count <= maximumWords)
            return context.Trim();

        Match lastIncluded = matches[maximumWords - 1];
        int end = lastIncluded.Index + lastIncluded.Length;
        return context[..end].TrimEnd() +
            "\n\n[Memory context truncated to stay within the prompt budget.]";
    }

    private static string TruncateToWordCount(string text, int maximumWords)
    {
        MatchCollection matches = Regex.Matches(text, @"\S+");
        if (matches.Count <= maximumWords)
            return text.Trim();

        Match lastIncluded = matches[maximumWords - 1];
        return text[..(lastIncluded.Index + lastIncluded.Length)].TrimEnd() + "...";
    }

    private static int CountWords(string text) => Regex.Matches(text, @"\S+").Count;

    private static string NormalizeWhitespace(string? text) =>
        Regex.Replace(text ?? "", @"\s+", " ").Trim();

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    private static async Task AtomicWriteJsonAsync<T>(string path, T value)
    {
        string json = JsonSerializer.Serialize(value, JsonOptions);
        await AtomicWriteTextAsync(path, json);
    }

    private static async Task AtomicWriteTextAsync(string path, string contents)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, contents, Encoding.UTF8);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string GetVideoMemoryDirectory(bool testMode)
    {
        if (testMode)
        {
            return IsolatedTestMemoryDirectory.Value ??
                throw new InvalidOperationException(
                    "Test memory requires an isolated per-run test-memory scope.");
        }

        return Path.Combine(
            GetProjectDirectory(),
            "memory",
            "videos");
    }

    private static bool HasCompleteProductionValidation(
        ProductionValidationEvidence? evidence)
    {
        return evidence is not null &&
            !evidence.IsTestOrPreview &&
            evidence.ScriptValidation?.Success == true &&
            !evidence.ScriptValidation.AppearsTruncated &&
            evidence.TtsCompleted &&
            evidence.VoiceDurationValidation?.Success == true &&
            evidence.RenderingCompleted &&
            evidence.VideoValidation?.Success == true &&
            evidence.VideoValidation.FullValidationPerformed &&
            evidence.VideoValidation.ScriptValidationPassed &&
            evidence.VideoValidation.VoiceDurationValidationPassed;
    }

    private sealed class TestMemoryScope : IDisposable
    {
        private readonly string _root;
        private readonly string? _previous;
        private bool _disposed;

        public TestMemoryScope(string root, string? previous)
        {
            _root = root;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            IsolatedTestMemoryDirectory.Value = _previous;
            try
            {
                if (Directory.Exists(_root))
                    Directory.Delete(_root, recursive: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[VideoMemory] Could not remove this run's temporary test memory: {ex.Message}");
            }

            _disposed = true;
        }
    }

    private static string GetChannelStatePath() =>
        Path.Combine(GetProjectDirectory(), "memory", "channel_state.json");

    private static string GetLegacyVideoFolder(string videoId) =>
        Path.GetFullPath(Path.Combine(GetProjectDirectory(), "..", "data", "videos", videoId));

    private static string GetLegacyChannelBrainPath() =>
        Path.GetFullPath(Path.Combine(GetProjectDirectory(), "..", "data", "channel", "channel_brain.md"));

    private static string GetProjectDirectory()
    {
        string current = Directory.GetCurrentDirectory();
        if (File.Exists(Path.Combine(current, "csharp_v1.csproj")))
            return current;

        string childProject = Path.Combine(current, "csharp_v1");
        if (File.Exists(Path.Combine(childProject, "csharp_v1.csproj")))
            return childProject;

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "csharp_v1.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        return current;
    }

    public static string CreateVideoId() => $"VIDEO_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}";

    private static string SanitizeVideoId(string videoId)
    {
        string sanitized = new(videoId
            .Where(character => char.IsLetterOrDigit(character) || character is '_' or '-')
            .ToArray());
        return string.IsNullOrWhiteSpace(sanitized)
            ? $"VIDEO_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}"
            : sanitized;
    }

    private static string GetMemoryModel() =>
        Environment.GetEnvironmentVariable("EX01_MEMORY_MODEL") ?? DefaultMemoryModel;

    private static string GetOllamaGenerateUrl()
    {
        string configured = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? DefaultOllamaBaseUrl;
        return configured.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase)
            ? configured
            : configured.TrimEnd('/') + "/api/generate";
    }

    private static string TrimForPrompt(string? text, int maximumCharacters)
    {
        text ??= "";
        return text.Length <= maximumCharacters
            ? text
            : text[..maximumCharacters] + "\n\n[TRUNCATED FOR MEMORY EXTRACTION]";
    }

    private sealed class MemoryExtractionResult
    {
        public string Summary { get; set; } = "";
        public List<string> KeyPoints { get; set; } = new();
        public List<string> Ex01Opinions { get; set; } = new();
        public List<string> EventsAndExperiments { get; set; } = new();
        public List<string> JokesAndLore { get; set; } = new();
        public List<string> PromisesAndCallbacks { get; set; } = new();
        public List<string> UnresolvedQuestions { get; set; } = new();
        public List<string> Keywords { get; set; } = new();
        public string CompactScriptExcerpt { get; set; } = "";
    }
}
