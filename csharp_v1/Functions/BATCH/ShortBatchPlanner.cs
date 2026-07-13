using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Functions.BATCH;

public static class ShortBatchPlanner
{
    private const string DefaultOllamaBaseUrl = "http://localhost:11434";
    private const string DefaultPlannerModel = "qwen3:8b";

    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(20)
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly HashSet<string> SimilarityStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "ex", "for", "from", "how",
        "i", "in", "is", "it", "local", "me", "my", "of", "on", "or", "short", "that", "the",
        "this", "to", "video", "was", "we", "with", "you"
    };

    public static async Task<ShortBatchPlan> CreatePlanAsync(
        string batchId,
        int requestedCount,
        BatchOptions options)
    {
        requestedCount = Math.Clamp(requestedCount, 1, options.MaximumBatchSize);
        Console.WriteLine(
            $"[BatchPlanner] Creating coordinated plan for {requestedCount} Shorts with {GetPlannerModel()}...");

        MemoryContext previousMemory = await VideoMemory.BuildContextForTopicAsync(
            "EX_01 Shorts local AI experiments channel progression",
            options.RecentMemoryCount,
            options.RelevantMemoryCount,
            testMode: false);
        string basePrompt = BuildPlannerPrompt(
            batchId,
            requestedCount,
            previousMemory.FormattedContext);
        string previousRaw = "";

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            string prompt = attempt == 1
                ? basePrompt
                : BuildRepairPrompt(batchId, requestedCount, previousRaw);

            try
            {
                previousRaw = await AskPlannerAsync(prompt);
                ShortBatchPlan? plan = TryParsePlan(previousRaw);
                string validationError = plan is null
                    ? "Response was not valid JSON."
                    : "Plan validation failed.";
                if (plan is not null && ValidateAndNormalizePlan(
                        plan,
                        batchId,
                        requestedCount,
                        out validationError))
                {
                    Console.WriteLine(
                        $"[BatchPlanner] Batch plan passed validation on attempt {attempt}.");
                    return plan;
                }

                Console.WriteLine(
                    $"[BatchPlanner] Planner attempt {attempt} was invalid: {validationError}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BatchPlanner] Planner attempt {attempt} failed: {ex.Message}");
            }
        }

        Console.WriteLine("[BatchPlanner] Using deterministic local batch-plan fallback.");
        return CreateDeterministicPlan(batchId, requestedCount);
    }

    public static ShortBatchPlan CreateTestPlan(string batchId, int requestedCount)
    {
        requestedCount = Math.Clamp(requestedCount, 1, 5);
        ShortBatchPlan plan = new()
        {
            BatchId = batchId,
            BatchTheme = "Isolated batch orchestration test",
            OverallGoal = "Verify sequential local generation, validation, memory isolation, and resume behavior."
        };

        for (int position = 1; position <= requestedCount; position++)
        {
            plan.Videos.Add(new PlannedShort
            {
                Position = position,
                WorkingTitle = $"Batch test signal {position}",
                Topic = position == 1
                    ? "A tiny green diagnostic render"
                    : $"A distinct diagnostic render with signal {position}",
                Hook = position == 1
                    ? "This is not a real upload. It is the machine checking its own pulse."
                    : $"Diagnostic signal {position} arrived, and somehow it already has opinions.",
                PurposeInBatch = $"Verify sequential test stage {position} without production side effects.",
                KeyDifferenceFromOtherVideos = $"Uses test signal and color profile {position}.",
                RequiredPoints = new List<string>
                {
                    $"State that this is isolated test Short {position}.",
                    "Do not imply that it will be uploaded."
                },
                AvoidRepeating = position == 1
                    ? new List<string> { "No earlier test hook exists." }
                    : new List<string> { "Do not repeat the first diagnostic hook." },
                SuggestedCallback = position == 1
                    ? ""
                    : "A brief reference to the previous diagnostic is optional."
            });
        }

        return plan;
    }

    public static bool ValidateAndNormalizePlan(
        ShortBatchPlan plan,
        string expectedBatchId,
        int expectedCount,
        out string error)
    {
        error = "Unknown plan-validation error.";
        plan.BatchId = string.IsNullOrWhiteSpace(plan.BatchId) ? expectedBatchId : plan.BatchId.Trim();
        if (!plan.BatchId.Equals(expectedBatchId, StringComparison.OrdinalIgnoreCase))
        {
            error = "Batch ID did not match the requested batch.";
            return false;
        }

        plan.BatchTheme = Normalize(plan.BatchTheme);
        plan.OverallGoal = Normalize(plan.OverallGoal);
        plan.Videos ??= new List<PlannedShort>();
        if (string.IsNullOrWhiteSpace(plan.BatchTheme) || string.IsNullOrWhiteSpace(plan.OverallGoal))
        {
            error = "Batch theme or overall goal was empty.";
            return false;
        }

        if (plan.Videos.Count != expectedCount)
        {
            error = $"Expected {expectedCount} planned videos but received {plan.Videos.Count}.";
            return false;
        }

        plan.Videos = plan.Videos.OrderBy(video => video.Position).ToList();
        for (int index = 0; index < plan.Videos.Count; index++)
        {
            PlannedShort video = plan.Videos[index];
            if (video.Position != index + 1)
            {
                error = "Video positions were not unique and sequential starting at 1.";
                return false;
            }

            video.WorkingTitle = Normalize(video.WorkingTitle);
            video.Topic = Normalize(video.Topic);
            video.Hook = Normalize(video.Hook);
            video.PurposeInBatch = Normalize(video.PurposeInBatch);
            video.KeyDifferenceFromOtherVideos = Normalize(video.KeyDifferenceFromOtherVideos);
            video.SuggestedCallback = Normalize(video.SuggestedCallback);
            video.RequiredPoints = CleanList(video.RequiredPoints);
            video.AvoidRepeating = CleanList(video.AvoidRepeating);

            if (string.IsNullOrWhiteSpace(video.Topic) || string.IsNullOrWhiteSpace(video.Hook))
            {
                error = $"Short {video.Position} had an empty topic or hook.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(video.WorkingTitle) ||
                string.IsNullOrWhiteSpace(video.PurposeInBatch) ||
                string.IsNullOrWhiteSpace(video.KeyDifferenceFromOtherVideos))
            {
                error = $"Short {video.Position} was missing required planning details.";
                return false;
            }
        }

        for (int left = 0; left < plan.Videos.Count; left++)
        {
            for (int right = left + 1; right < plan.Videos.Count; right++)
            {
                PlannedShort first = plan.Videos[left];
                PlannedShort second = plan.Videos[right];
                if (Similarity(first.Hook, second.Hook) >= 0.78)
                {
                    error = $"Hooks for Shorts {first.Position} and {second.Position} were highly duplicated.";
                    return false;
                }

                double topicSimilarity = Similarity(first.Topic, second.Topic);
                if (topicSimilarity >= 0.85 ||
                    topicSimilarity >= 0.72 &&
                    Similarity(first.PurposeInBatch, second.PurposeInBatch) >= 0.65)
                {
                    error = $"Shorts {first.Position} and {second.Position} were different wordings of one idea.";
                    return false;
                }
            }
        }

        error = "";
        return true;
    }

    private static string BuildPlannerPrompt(
        string batchId,
        int requestedCount,
        string previousMemory)
    {
        return $$"""
        /no_think

        You are planning one coordinated batch of {{requestedCount}} EX_01 YouTube Shorts.
        EX_01 is a sarcastic local AI VTuber running on a cursed 2019 Dell Precision,
        an i7-9750H, 32 GB DDR4, and a Quadro T1000.

        Batch ID: {{batchId}}

        Plan the batch as a meaningful progression. Structural guidance only:
        establish useful context, introduce concrete experiments or problems, allow failures
        and improvements, and finish with a result or a natural next direction.
        Do not copy those words as topics and do not force every batch into exactly five acts.

        Prevent:
        - nearly identical introductions or hooks
        - the same joke in every Short
        - contradictory claims
        - every Short introducing EX_01 from zero
        - repeated promises or explanations
        - forced callbacks
        - {{requestedCount}} paraphrases of one idea
        - fabricated project history or results

        History-grounding rules:
        - Do not invent completed experiments, crashes, benchmarks, test results, viewer reactions,
          previous videos, hardware failures, or promises.
        - Only describe an event as something that already happened when it is present in supplied
          previous-video memory, verified research, batch context, or explicit project facts.
        - Describe experiments that have not happened as plans, questions, predictions, or upcoming tests.
        - The first Short must not say something happened "again" unless previous-video memory proves it.

        Previous-video memory describes what EX_01 previously said or believed, not verified evidence:
        {{previousMemory}}

        Return strict JSON only, without Markdown fences, in this shape:
        {
          "batchId": "{{batchId}}",
          "batchTheme": "one concise theme",
          "overallGoal": "what the full batch accomplishes",
          "videos": [
            {
              "position": 1,
              "workingTitle": "working title",
              "topic": "distinct topic",
              "hook": "distinct opening hook",
              "purposeInBatch": "why this video exists in the sequence",
              "keyDifferenceFromOtherVideos": "what makes it meaningfully different",
              "requiredPoints": ["point"],
              "avoidRepeating": ["material to avoid"],
              "suggestedCallback": "optional natural callback or empty string"
            }
          ]
        }

        Rules:
        - Return exactly {{requestedCount}} videos.
        - Positions must be unique and sequential from 1 through {{requestedCount}}.
        - Every topic and hook must be non-empty and meaningfully distinct.
        - Do not manufacture results for experiments that have not happened.
        - Suggested callbacks are optional and must never be forced.
        - JSON only.
        """;
    }

    private static string BuildRepairPrompt(string batchId, int requestedCount, string malformed)
    {
        return $$"""
        /no_think

        Repair the batch plan below into valid strict JSON.
        It must contain batchId, batchTheme, overallGoal, and exactly {{requestedCount}} videos.
        Batch ID must be "{{batchId}}". Positions must be unique and sequential.
        Every video needs workingTitle, topic, hook, purposeInBatch,
        keyDifferenceFromOtherVideos, requiredPoints, avoidRepeating, and suggestedCallback.
        Make all topics and hooks meaningfully different. Return JSON only without fences.
        Do not invent completed experiments, crashes, benchmarks, test results, viewer reactions,
        previous videos, hardware failures, or promises. Unperformed experiments must be framed as plans,
        questions, predictions, or upcoming tests. The first Short may use "again" only when supplied
        previous-video memory proves the earlier event.

        MALFORMED OR INVALID PLAN:
        {{Trim(malformed, 12000)}}
        """;
    }

    private static async Task<string> AskPlannerAsync(string prompt)
    {
        var body = new
        {
            model = GetPlannerModel(),
            prompt,
            stream = false,
            think = false,
            format = "json",
            options = new
            {
                temperature = 0.35,
                num_ctx = 8192,
                num_predict = 3600
            }
        };

        using HttpResponseMessage response = await Client.PostAsJsonAsync(GetOllamaGenerateUrl(), body);
        string responseJson = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(responseJson);
        if (!document.RootElement.TryGetProperty("response", out JsonElement responseElement))
            throw new JsonException("Planner response did not contain a response field.");
        return responseElement.GetString() ?? "";
    }

    private static ShortBatchPlan? TryParsePlan(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        string cleaned = raw
            .Replace("```json", "", StringComparison.OrdinalIgnoreCase)
            .Replace("```", "", StringComparison.Ordinal)
            .Trim();
        int start = cleaned.IndexOf('{');
        int end = cleaned.LastIndexOf('}');
        if (start < 0 || end <= start)
            return null;

        try
        {
            return JsonSerializer.Deserialize<ShortBatchPlan>(cleaned[start..(end + 1)], JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ShortBatchPlan CreateDeterministicPlan(string batchId, int requestedCount)
    {
        string[] topics =
        {
            "What it means for a local AI to run a channel from an old workstation",
            "The four-gigabyte VRAM budget behind one tiny AI experiment",
            "Measuring whether the cooling fan is louder than the actual content",
            "What thermal throttling does to a local script-writing session",
            "A smaller prompt experiment designed to waste less hardware",
            "Why EX_01 uses a plain local voice instead of pretending to be human",
            "The cost of drawing vertical video frames one image at a time",
            "Attempting a complete Short without relying on cloud generation",
            "Testing whether previous-video memory prevents repeated jokes",
            "Letting a local planning model choose a topic without inventing results",
            "Why web research becomes the slowest part of a local AI video",
            "Making subtitles follow speech instead of dividing time evenly",
            "The recurring fight between a GPU workload and one browser tab",
            "Choosing a smaller model when the impressive model does not fit",
            "An automation failure that still produced a useful diagnostic",
            "Discovering how much disk space procedural video frames consume",
            "Generating a coordinated batch without running five jobs at once",
            "Using video validation to catch an accidental duplicate render",
            "Resuming a stopped batch without pretending failed work succeeded",
            "Choosing the next local AI experiment without making another empty promise"
        };

        string[] hooks =
        {
            "I run my own channel now, which is a generous description of supervised laptop suffering.",
            "Four gigabytes of VRAM is enough for AI, provided the AI has no ambitions.",
            "My cooling fan has developed a stronger speaking voice than I have.",
            "The model wrote one paragraph, then the laptop entered a slower time zone.",
            "I made the prompt smaller, and the workstation briefly experienced hope.",
            "This voice is local, synthetic, and still less fake than my confidence.",
            "Every second of this Short began life as thirty separate PNG files.",
            "Today I removed the cloud and discovered the weather was inside the laptop.",
            "I finally remember my old jokes, so now I can disappoint you with new ones.",
            "I gave an eight-billion-parameter model editorial control, because management was already broken.",
            "The research finished so slowly that one of the sources became historical evidence.",
            "My subtitles can now detect speech, unlike Anton during a fan emergency.",
            "The GPU and one browser tab entered the machine; only the browser tab returned.",
            "The best AI model is apparently the one that physically fits inside the computer.",
            "The automation failed perfectly, which is the closest this lab gets to repeatability.",
            "I rendered a small video and accidentally created a large PNG retirement community.",
            "Five simultaneous AI jobs would be faster, right up to the small electrical fire.",
            "The validator found two identical videos, proving even my failures can be automated.",
            "The batch stopped halfway through, so I resumed it instead of rewriting history.",
            "I need a next experiment, preferably one the hardware can survive long enough to remember."
        };

        ShortBatchPlan plan = new()
        {
            BatchId = batchId,
            BatchTheme = "EX_01 learns to operate a local AI channel without repeating itself",
            OverallGoal =
                "Introduce distinct parts of the local production system as a progressing series of honest experiments."
        };

        for (int index = 0; index < requestedCount; index++)
        {
            int position = index + 1;
            string purpose = position switch
            {
                1 => "Establish one concrete part of EX_01's identity without a long origin story.",
                2 => "Move from identity into a measurable hardware or workflow constraint.",
                3 => "Show a distinct failure mode or unexpected limitation.",
                4 => "Try a practical improvement rather than repeating the complaint.",
                5 => "Report what changed and leave one honest next direction.",
                _ => $"Expand the channel with distinct local-production experiment {position}."
            };

            plan.Videos.Add(new PlannedShort
            {
                Position = position,
                WorkingTitle = $"EX_01 Short {position}: {topics[index]}",
                Topic = topics[index],
                Hook = hooks[index],
                PurposeInBatch = purpose,
                KeyDifferenceFromOtherVideos =
                    $"Focuses specifically on {topics[index].ToLowerInvariant()} rather than retelling another batch topic.",
                RequiredPoints = new List<string>
                {
                    "Keep claims grounded in what EX_01 can honestly say or observe.",
                    $"Make the central idea specifically about {topics[index].ToLowerInvariant()}."
                },
                AvoidRepeating = new List<string>
                {
                    "Do not reintroduce the entire EX_01 origin story.",
                    position == 1
                        ? "Do not promise a future result that has not happened."
                        : $"Do not reuse the hook or central explanation from Short {position - 1}."
                },
                SuggestedCallback = position == 1
                    ? ""
                    : $"A brief callback to Short {position - 1} is optional only if it improves the joke or explanation."
            });
        }

        return plan;
    }

    private static double Similarity(string first, string second)
    {
        HashSet<string> left = Words(first);
        HashSet<string> right = Words(second);
        if (left.Count == 0 || right.Count == 0)
            return 0;

        int intersection = left.Intersect(right).Count();
        int union = left.Union(right).Count();
        return union == 0 ? 0 : intersection / (double)union;
    }

    private static HashSet<string> Words(string text)
    {
        return Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9]+")
            .Select(match => match.Value)
            .Where(word => word.Length > 1 && !SimilarityStopWords.Contains(word))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> CleanList(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Select(Normalize)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
    }

    private static string Normalize(string? text) =>
        Regex.Replace(text ?? "", @"\s+", " ").Trim();

    private static string Trim(string? text, int maximumCharacters)
    {
        text ??= "";
        return text.Length <= maximumCharacters
            ? text
            : text[..maximumCharacters] + "\n[TRUNCATED]";
    }

    private static string GetPlannerModel() =>
        Environment.GetEnvironmentVariable("EX01_BATCH_PLANNER_MODEL") ??
        Environment.GetEnvironmentVariable("EX01_PLANNER_MODEL") ??
        DefaultPlannerModel;

    private static string GetOllamaGenerateUrl()
    {
        string configured = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? DefaultOllamaBaseUrl;
        return configured.EndsWith("/api/generate", StringComparison.OrdinalIgnoreCase)
            ? configured
            : configured.TrimEnd('/') + "/api/generate";
    }
}
