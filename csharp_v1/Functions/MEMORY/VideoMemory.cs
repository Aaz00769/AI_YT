using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Infrastructure;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Functions.MEMORY;

public sealed class VideoMemory
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "about", "after", "again", "all", "also", "am", "an", "and", "are", "as", "at",
        "be", "because", "been", "but", "by", "can", "did", "do", "does", "for", "from", "had",
        "has", "have", "how", "i", "if", "in", "into", "is", "it", "its", "just", "me", "more",
        "my", "no", "not", "of", "on", "or", "our", "out", "so", "some", "that", "the", "their",
        "them", "then", "there", "these", "they", "this", "those", "to", "too", "up", "very", "was",
        "we", "were", "what", "when", "where", "which", "who", "why", "will", "with", "would", "you", "your"
    };

    private static readonly string[] HardwareTerms =
    {
        "dell", "precision", "quadro", "t1000", "i7", "9750h", "gpu", "cpu", "vram", "ram",
        "ddr4", "hardware", "thermal", "cooling", "fan"
    };

    private readonly Ex01Settings _settings;
    private readonly string _memoryDirectory;
    private readonly bool _isolatedTestMode;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromMinutes(20) };
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public VideoMemory(
        Ex01Settings settings,
        string? memoryDirectoryOverride = null,
        bool isolatedTestMode = false)
    {
        _settings = settings;
        _memoryDirectory = Path.GetFullPath(memoryDirectoryOverride ?? settings.MemoryDirectory);
        _isolatedTestMode = isolatedTestMode;
    }

    public string VideoDirectory => Path.Combine(_memoryDirectory, "videos");
    public string ChannelStatePath => Path.Combine(_memoryDirectory, "channel_state.json");

    public async Task<IReadOnlyList<VideoMemoryRecord>> LoadAllAsync()
    {
        if (!Directory.Exists(VideoDirectory))
            return Array.Empty<VideoMemoryRecord>();

        List<VideoMemoryRecord> records = new();
        foreach (string path in Directory.EnumerateFiles(VideoDirectory, "*.json").OrderBy(path => path))
        {
            try
            {
                await using FileStream stream = File.OpenRead(path);
                VideoMemoryRecord? record = await JsonSerializer.DeserializeAsync<VideoMemoryRecord>(
                    stream,
                    JsonFile.Options);
                if (record is null || string.IsNullOrWhiteSpace(record.VideoId))
                {
                    Console.WriteLine($"[Memory] Ignoring incomplete record: {Path.GetFileName(path)}");
                    continue;
                }

                Normalize(record);
                records.Add(record);
            }
            catch (Exception exception) when (exception is JsonException or IOException)
            {
                Console.WriteLine($"[Memory] Ignoring malformed record {Path.GetFileName(path)}: {exception.Message}");
            }
        }

        return records
            .OrderByDescending(record => record.CreatedUtc)
            .ThenBy(record => record.VideoId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<MemoryContext> BuildContextForTopicAsync(
        string topic,
        int recentCount = 3,
        int relevantCount = 4)
    {
        IReadOnlyList<VideoMemoryRecord> all = await LoadAllAsync();
        List<VideoMemoryRecord> recent = all.Take(Math.Clamp(recentCount, 0, 10)).ToList();
        HashSet<string> recentIds = recent.Select(record => record.VideoId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> topicWords = Tokenize(topic);
        List<VideoMemoryRecord> relevant = all
            .Where(record => !recentIds.Contains(record.VideoId))
            .Select(record => (Record: record, Score: Score(record, topicWords)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenByDescending(item => item.Record.CreatedUtc)
            .Take(Math.Clamp(relevantCount, 0, 10))
            .Select(item => item.Record)
            .ToList();
        ChannelMemoryState state = await LoadChannelStateAsync();

        return new MemoryContext
        {
            RecentVideos = recent,
            RelevantVideos = relevant,
            ChannelState = state,
            FormattedContext = FormatContext(recent, relevant, state)
        };
    }

    public async Task<ChannelMemoryState> LoadChannelStateAsync()
    {
        if (!File.Exists(ChannelStatePath))
            return EmptyState();
        try
        {
            await using FileStream stream = File.OpenRead(ChannelStatePath);
            return await JsonSerializer.DeserializeAsync<ChannelMemoryState>(stream, JsonFile.Options)
                ?? EmptyState();
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            Console.WriteLine($"[Memory] Channel state is unavailable: {exception.Message}");
            return EmptyState();
        }
    }

    public async Task RebuildChannelStateAsync()
    {
        IReadOnlyList<VideoMemoryRecord> records = await LoadAllAsync();
        ChannelMemoryState state = BuildChannelState(records);
        await JsonFile.WriteAtomicAsync(ChannelStatePath, state);
    }

    public async Task<VideoMemoryRecord?> SaveCompletedVideoAsync(
        string videoId,
        string title,
        string topic,
        string videoPath,
        string scriptPath,
        string finalScript,
        ProductionValidationEvidence? evidence,
        bool deterministicExtraction = false)
    {
        if (!_isolatedTestMode && !HasCompleteProductionEvidence(evidence))
        {
            Console.WriteLine(
                "[Memory] Refusing official memory save: validation or explicit user approval is missing.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(finalScript))
        {
            Console.WriteLine("[Memory] Refusing memory save: the final script is empty.");
            return null;
        }

        if (!_isolatedTestMode &&
            (!File.Exists(videoPath) || !File.Exists(scriptPath) || new FileInfo(videoPath).Length == 0))
        {
            Console.WriteLine("[Memory] Refusing official memory save: completed artifacts are missing.");
            return null;
        }

        videoId = SanitizeVideoId(videoId);
        string scriptHash = CalculateHash(finalScript);

        await _writeLock.WaitAsync();
        try
        {
            Directory.CreateDirectory(VideoDirectory);
            IReadOnlyList<VideoMemoryRecord> existing = await LoadAllAsync();
            VideoMemoryRecord? duplicate = existing.FirstOrDefault(record =>
                record.VideoId.Equals(videoId, StringComparison.OrdinalIgnoreCase) ||
                record.ScriptHash.Equals(scriptHash, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
            {
                Console.WriteLine($"[Memory] Duplicate record prevented; using {duplicate.VideoId}.");
                return duplicate;
            }

            MemoryExtractionResult extraction;
            if (deterministicExtraction || _isolatedTestMode)
            {
                extraction = CreateDeterministicExtraction(finalScript);
            }
            else
            {
                try
                {
                    extraction = await ExtractAsync(title, topic, finalScript);
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"[Memory] Extraction model unavailable; using conservative fallback: {exception.Message}");
                    extraction = CreateDeterministicExtraction(finalScript);
                }
            }

            VideoMemoryRecord record = new()
            {
                VideoId = videoId,
                Title = NormalizeText(string.IsNullOrWhiteSpace(title) ? topic : title),
                Topic = NormalizeText(string.IsNullOrWhiteSpace(topic) ? title : topic),
                CreatedUtc = DateTime.UtcNow,
                VideoPath = Path.GetFullPath(videoPath),
                ScriptPath = Path.GetFullPath(scriptPath),
                Summary = extraction.Summary,
                KeyPoints = CleanList(extraction.KeyPoints),
                Ex01Opinions = CleanList(extraction.Ex01Opinions),
                EventsAndExperiments = CleanList(extraction.EventsAndExperiments),
                JokesAndLore = CleanList(extraction.JokesAndLore),
                PromisesAndCallbacks = CleanList(extraction.PromisesAndCallbacks),
                UnresolvedQuestions = CleanList(extraction.UnresolvedQuestions),
                Keywords = CleanList(extraction.Keywords, 20),
                CompactScriptExcerpt = TruncateWords(extraction.CompactScriptExcerpt, 120),
                ScriptHash = scriptHash
            };

            await JsonFile.WriteAtomicAsync(Path.Combine(VideoDirectory, $"{videoId}.json"), record);
            if (!_isolatedTestMode)
                await JsonFile.WriteAtomicAsync(ChannelStatePath, BuildChannelState(existing.Append(record).ToList()));
            Console.WriteLine($"[Memory] Saved: {record.VideoId}");
            return record;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[Memory] Save failed; completed media was kept: {exception.Message}");
            return null;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public static string CalculateHash(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash);
    }

    private async Task<MemoryExtractionResult> ExtractAsync(string title, string topic, string script)
    {
        string prompt = $"""
        /no_think

        Extract compact continuity memory from an approved, completed EX_01 video.
        Use only statements present in the supplied title, topic, or script.
        Do not turn proposed experiments, hypotheticals, jokes, or predictions into completed events.
        Empty arrays are correct when a category has no supported item.

        TITLE: {title}
        TOPIC: {topic}
        SCRIPT:
        {script}

        Return only one JSON object with these camelCase fields:
        summary, keyPoints, ex01Opinions, eventsAndExperiments, jokesAndLore,
        promisesAndCallbacks, unresolvedQuestions, keywords, compactScriptExcerpt.
        summary and compactScriptExcerpt are strings. Every other field is an array of short strings.
        The excerpt must be copied from the script and stay under 120 words.
        """;
        var body = new
        {
            model = _settings.MemoryModel,
            prompt,
            stream = false,
            think = false,
            format = "json",
            options = new { temperature = 0.1, num_ctx = 8192, num_predict = 1400 }
        };

        using HttpResponseMessage response = await _client.PostAsJsonAsync(
            $"{_settings.OllamaEndpoint}/api/generate",
            body);
        response.EnsureSuccessStatusCode();
        using JsonDocument envelope = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        string raw = envelope.RootElement.GetProperty("response").GetString() ?? "";
        MemoryExtractionResult? extraction = JsonSerializer.Deserialize<MemoryExtractionResult>(raw, JsonFile.Options);
        if (extraction is null || string.IsNullOrWhiteSpace(extraction.Summary))
            throw new JsonException("Memory extraction did not return usable JSON.");
        return extraction;
    }

    private static MemoryExtractionResult CreateDeterministicExtraction(string script)
    {
        string normalized = NormalizeText(script);
        string excerpt = TruncateWords(normalized, 120);
        string summary = string.Join(" ", Regex.Split(normalized, @"(?<=[.!?])\s+").Take(2));
        if (string.IsNullOrWhiteSpace(summary))
            summary = excerpt;
        List<string> keywords = Tokenize(normalized).Take(12).ToList();
        return new MemoryExtractionResult
        {
            Summary = TruncateWords(summary, 80),
            CompactScriptExcerpt = excerpt,
            Keywords = keywords
        };
    }

    private static bool HasCompleteProductionEvidence(ProductionValidationEvidence? evidence) =>
        evidence is not null &&
        evidence.UserApprovedOfficialMemory &&
        !evidence.IsTestOrPreview &&
        evidence.ScriptValidation?.Success == true &&
        evidence.TtsCompleted &&
        evidence.VoiceDurationValidation?.Success == true &&
        evidence.RenderingCompleted &&
        evidence.VideoValidation?.Success == true &&
        evidence.VideoValidation.FullValidationPerformed &&
        evidence.VideoValidation.ScriptValidationPassed &&
        evidence.VideoValidation.VoiceDurationValidationPassed;

    private static double Score(VideoMemoryRecord record, HashSet<string> topicWords)
    {
        if (topicWords.Count == 0)
            return 0;
        HashSet<string> topic = Tokenize(record.Topic);
        HashSet<string> keywords = record.Keywords.SelectMany(word => Tokenize(word)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HashSet<string> summary = Tokenize(record.Summary);
        return topicWords.Count(word => topic.Contains(word)) * 4 +
               topicWords.Count(word => keywords.Contains(word)) * 3 +
               topicWords.Count(word => summary.Contains(word));
    }

    private static string FormatContext(
        IReadOnlyList<VideoMemoryRecord> recent,
        IReadOnlyList<VideoMemoryRecord> relevant,
        ChannelMemoryState state)
    {
        if (recent.Count == 0 && relevant.Count == 0)
            return "No approved previous-video memories are available.";

        StringBuilder context = new();
        context.AppendLine(
            "These are approved channel memories. They describe what approved scripts said; they are not independent proof of real-world events.");
        if (!string.IsNullOrWhiteSpace(state.ChannelSummary))
            context.AppendLine($"Channel state: {state.ChannelSummary}");
        AppendList(context, "Unresolved promises", state.UnresolvedPromises, 6);
        AppendRecords(context, "Recent approved videos", recent);
        AppendRecords(context, "Topic-relevant approved videos", relevant);
        return TruncateWords(context.ToString(), 1600);
    }

    private static void AppendRecords(StringBuilder context, string heading, IEnumerable<VideoMemoryRecord> records)
    {
        List<VideoMemoryRecord> list = records.ToList();
        if (list.Count == 0)
            return;
        context.AppendLine($"{heading}:");
        foreach (VideoMemoryRecord record in list)
        {
            context.AppendLine($"- {record.VideoId} | {record.Title} | {record.Topic}");
            if (!string.IsNullOrWhiteSpace(record.Summary))
                context.AppendLine($"  Summary: {record.Summary}");
            AppendList(context, "  Promises", record.PromisesAndCallbacks, 3);
            AppendList(context, "  Unresolved", record.UnresolvedQuestions, 3);
            AppendList(context, "  Lore", record.JokesAndLore, 3);
        }
    }

    private static void AppendList(StringBuilder context, string label, IEnumerable<string> values, int maximum)
    {
        string joined = string.Join("; ", values.Where(value => !string.IsNullOrWhiteSpace(value)).Take(maximum));
        if (!string.IsNullOrWhiteSpace(joined))
            context.AppendLine($"{label}: {joined}");
    }

    private static ChannelMemoryState BuildChannelState(IReadOnlyList<VideoMemoryRecord> records)
    {
        List<VideoMemoryRecord> ordered = records.OrderByDescending(record => record.CreatedUtc).ToList();
        string recentTopics = string.Join("; ", ordered.Take(6).Select(record => record.Topic));
        IEnumerable<string> hardwareCandidates = ordered.SelectMany(record =>
            record.KeyPoints.Concat(record.EventsAndExperiments).Concat(record.JokesAndLore));
        return new ChannelMemoryState
        {
            ChannelSummary = ordered.Count == 0
                ? "No approved completed-video memories exist yet."
                : $"EX_01 has {ordered.Count} approved completed-video memory record(s). Recent topics: {recentTopics}.",
            RecurringLore = MostFrequent(ordered.SelectMany(record => record.JokesAndLore), 20),
            ActiveProjects = MostFrequent(ordered.SelectMany(record => record.PromisesAndCallbacks), 20),
            UnresolvedPromises = MostFrequent(
                ordered.SelectMany(record => record.PromisesAndCallbacks.Concat(record.UnresolvedQuestions)), 25),
            KnownHardware = MostFrequent(
                hardwareCandidates.Where(value => HardwareTerms.Any(term =>
                    value.Contains(term, StringComparison.OrdinalIgnoreCase))), 20),
            UpdatedUtc = DateTime.UtcNow
        };
    }

    private static List<string> MostFrequent(IEnumerable<string> values, int maximum) => values
        .Select(NormalizeText)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
        .OrderByDescending(group => group.Count())
        .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
        .Take(maximum)
        .Select(group => group.First())
        .ToList();

    private static ChannelMemoryState EmptyState() => new()
    {
        ChannelSummary = "No channel state has been built from approved memory yet."
    };

    private static HashSet<string> Tokenize(string? value) => Regex
        .Matches(value ?? "", @"[\p{L}\p{N}]+", RegexOptions.CultureInvariant)
        .Select(match => match.Value.ToLowerInvariant())
        .Where(word => word.Length > 2 && !StopWords.Contains(word))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static List<string> CleanList(IEnumerable<string>? values, int maximum = 30) =>
        (values ?? Array.Empty<string>())
        .Select(NormalizeText)
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(maximum)
        .ToList();

    private static void Normalize(VideoMemoryRecord record)
    {
        record.Title = NormalizeText(record.Title);
        record.Topic = NormalizeText(record.Topic);
        record.Summary = NormalizeText(record.Summary);
        record.KeyPoints = CleanList(record.KeyPoints);
        record.Ex01Opinions = CleanList(record.Ex01Opinions);
        record.EventsAndExperiments = CleanList(record.EventsAndExperiments);
        record.JokesAndLore = CleanList(record.JokesAndLore);
        record.PromisesAndCallbacks = CleanList(record.PromisesAndCallbacks);
        record.UnresolvedQuestions = CleanList(record.UnresolvedQuestions);
        record.Keywords = CleanList(record.Keywords);
    }

    private static string NormalizeText(string? value) =>
        string.Join(" ", (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string TruncateWords(string value, int maximumWords)
    {
        string[] words = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= maximumWords
            ? string.Join(" ", words)
            : string.Join(" ", words.Take(maximumWords)) + " …";
    }

    private static string SanitizeVideoId(string videoId)
    {
        string safe = Regex.Replace(videoId ?? "", @"[^A-Za-z0-9_-]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(safe)
            ? $"VIDEO_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}"
            : safe;
    }
}
