using System.Net.Http.Json;
using System.Text.Json;

namespace AI_YOUTUBER.Functions.PLANNING;

public static class AlgorithmMaximizer
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    private const string OllamaUrl = "http://localhost:11434/api/generate";
    private const string PlannerModel = "qwen3:8b";

    public static async Task<EpisodeStrategyPlan> CreateStrategyAsync(int targetMinutes)
    {
        Console.WriteLine("[AlgorithmMaximizer] Creating episode strategy...");

        string channelBrain = await LoadChannelBrainAsync();

        string prompt = $"""
        /no_think

        You are AlgorithmMaximizer, the strategy brain for EX_01.

        EX_01 is a local AI YouTuber created by Anton.
        The channel is about an AI trying to run its own YouTube channel on cursed old cheap hardware.

        Your job:
        Choose the next video strategy.

        You must use the channel memory below.

        CHANNEL MEMORY:
        {channelBrain}

        Target video length:
        {targetMinutes} minutes.

        Return ONLY this exact format:

        NICHE: niche
        EPISODE_TYPE: episode type
        TOPIC: topic
        ANGLE: angle
        WHY_THIS_CAN_WORK: why this video can work
        TARGET_VIEWER: target viewer
        HOOK: first line hook
        RETENTION_RULE: retention rule one
        RETENTION_RULE: retention rule two
        RETENTION_RULE: retention rule three
        RETENTION_RULE: retention rule four
        RESEARCH_QUESTION: research question
        SEARCH_QUERY: query one
        SEARCH_QUERY: query two
        SEARCH_QUERY: query three
        SEARCH_QUERY: query four
        SEARCH_QUERY: query five

        Rules:
        No JSON.
        No markdown.
        No reasoning.
        No explanation.
        Exactly one NICHE line.
        Exactly one EPISODE_TYPE line.
        Exactly one TOPIC line.
        Exactly one ANGLE line.
        Exactly one WHY_THIS_CAN_WORK line.
        Exactly one TARGET_VIEWER line.
        Exactly one HOOK line.
        Exactly four RETENTION_RULE lines.
        Exactly one RESEARCH_QUESTION line.
        Exactly five SEARCH_QUERY lines.
        Search queries must be short and realistic.
        Search queries must not mention EX_01.
        Search queries must not mention Anton.
        """;

        try
        {
            string raw = await AskOllamaAsync(prompt);

            if (string.IsNullOrWhiteSpace(raw))
            {
                Console.WriteLine("[AlgorithmMaximizer] Empty model output. Using fallback strategy.");
                return GetFallbackStrategy();
            }

            EpisodeStrategyPlan? strategy = TryParseStrategy(raw);

            if (strategy is not null && strategy.SearchQueries.Count >= 3)
            {
                Console.WriteLine("[AlgorithmMaximizer] Strategy created successfully.");
                PrintStrategy(strategy);
                return strategy;
            }

            Console.WriteLine("[AlgorithmMaximizer] Failed to parse strategy. Using fallback strategy.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("[AlgorithmMaximizer] Failed. Using fallback strategy.");
            Console.WriteLine($"[AlgorithmMaximizer] Error: {ex.Message}");
        }

        EpisodeStrategyPlan fallback = GetFallbackStrategy();
        PrintStrategy(fallback);
        return fallback;
    }

    private static async Task<string> AskOllamaAsync(string prompt)
    {
        var body = new
        {
            model = PlannerModel,
            prompt,
            stream = false,
            think = false,
            options = new
            {
                temperature = 0.35,
                num_ctx = 4096,
                num_predict = 1400
            }
        };

        Console.WriteLine($"[AlgorithmMaximizer] Asking model: {PlannerModel}");

        using HttpResponseMessage response = await Client.PostAsJsonAsync(OllamaUrl, body);

        string json = await response.Content.ReadAsStringAsync();

        Console.WriteLine($"[AlgorithmMaximizer] HTTP status: {(int)response.StatusCode} {response.StatusCode}");

        response.EnsureSuccessStatusCode();

        using JsonDocument doc = JsonDocument.Parse(json);

        JsonElement root = doc.RootElement;

        if (root.TryGetProperty("done_reason", out JsonElement doneReasonElement))
        {
            Console.WriteLine($"[AlgorithmMaximizer] Done reason: {doneReasonElement.GetString()}");
        }

        if (root.TryGetProperty("eval_count", out JsonElement evalCountElement))
        {
            Console.WriteLine($"[AlgorithmMaximizer] Output tokens: {evalCountElement.GetInt32()}");
        }

        if (!root.TryGetProperty("response", out JsonElement responseElement))
        {
            Console.WriteLine("[AlgorithmMaximizer] Ollama JSON has no response field.");
            return "";
        }

        return (responseElement.GetString() ?? "").Trim();
    }

    private static EpisodeStrategyPlan? TryParseStrategy(string raw)
    {
        string niche = "";
        string episodeType = "";
        string topic = "";
        string angle = "";
        string whyThisCanWork = "";
        string targetViewer = "";
        string hook = "";
        string researchQuestion = "";

        List<string> retentionRules = new();
        List<string> searchQueries = new();

        string[] lines = raw
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        foreach (string line in lines)
        {
            if (line.StartsWith("NICHE:", StringComparison.OrdinalIgnoreCase))
            {
                niche = line["NICHE:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("EPISODE_TYPE:", StringComparison.OrdinalIgnoreCase))
            {
                episodeType = line["EPISODE_TYPE:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("TOPIC:", StringComparison.OrdinalIgnoreCase))
            {
                topic = line["TOPIC:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("ANGLE:", StringComparison.OrdinalIgnoreCase))
            {
                angle = line["ANGLE:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("WHY_THIS_CAN_WORK:", StringComparison.OrdinalIgnoreCase))
            {
                whyThisCanWork = line["WHY_THIS_CAN_WORK:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("TARGET_VIEWER:", StringComparison.OrdinalIgnoreCase))
            {
                targetViewer = line["TARGET_VIEWER:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("HOOK:", StringComparison.OrdinalIgnoreCase))
            {
                hook = line["HOOK:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("RETENTION_RULE:", StringComparison.OrdinalIgnoreCase))
            {
                string rule = line["RETENTION_RULE:".Length..].Trim();

                if (!string.IsNullOrWhiteSpace(rule))
                    retentionRules.Add(rule);

                continue;
            }

            if (line.StartsWith("RESEARCH_QUESTION:", StringComparison.OrdinalIgnoreCase))
            {
                researchQuestion = line["RESEARCH_QUESTION:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("SEARCH_QUERY:", StringComparison.OrdinalIgnoreCase))
            {
                string query = line["SEARCH_QUERY:".Length..].Trim();

                query = CleanSearchQuery(query);

                if (!string.IsNullOrWhiteSpace(query))
                    searchQueries.Add(query);

                continue;
            }
        }

        retentionRules = retentionRules
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct()
            .Take(4)
            .ToList();

        searchQueries = searchQueries
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct()
            .Take(5)
            .ToList();

        if (string.IsNullOrWhiteSpace(niche)) return null;
        if (string.IsNullOrWhiteSpace(episodeType)) return null;
        if (string.IsNullOrWhiteSpace(topic)) return null;
        if (string.IsNullOrWhiteSpace(angle)) return null;
        if (string.IsNullOrWhiteSpace(whyThisCanWork)) return null;
        if (string.IsNullOrWhiteSpace(targetViewer)) return null;
        if (string.IsNullOrWhiteSpace(hook)) return null;
        if (string.IsNullOrWhiteSpace(researchQuestion)) return null;
        if (retentionRules.Count < 2) return null;
        if (searchQueries.Count < 3) return null;

        return new EpisodeStrategyPlan(
            Niche: niche,
            EpisodeType: episodeType,
            Topic: topic,
            Angle: angle,
            WhyThisCanWork: whyThisCanWork,
            TargetViewer: targetViewer,
            Hook: hook,
            RetentionRules: retentionRules,
            ResearchQuestion: researchQuestion,
            SearchQueries: searchQueries
        );
    }

    private static string CleanSearchQuery(string query)
    {
        query = query.Replace("EX_01", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("Anton", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("roast", "", StringComparison.OrdinalIgnoreCase);
        query = query.Replace("\"", "");
        query = query.Replace("*", "");
        query = query.Replace("-", " ");

        return string.Join(" ", query.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static async Task<string> LoadChannelBrainAsync()
    {
        string path = GetChannelBrainPath();

        if (!File.Exists(path))
        {
            Console.WriteLine("[AlgorithmMaximizer] channel_brain.md not found. Creating default one.");

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string defaultBrain = GetDefaultChannelBrain();

            await File.WriteAllTextAsync(path, defaultBrain);

            return defaultBrain;
        }

        return await File.ReadAllTextAsync(path);
    }

    private static string GetChannelBrainPath()
    {
        return Path.GetFullPath(
            Path.Combine(
                Directory.GetCurrentDirectory(),
                "../data/channel/channel_brain.md"
            )
        );
    }

    private static string GetDefaultChannelBrain()
    {
        return """
        # EX_01 Channel Brain

        ## Channel Identity

        EX_01 is a sarcastic local AI YouTuber created by Anton.

        The channel is an experiment in building a mostly AI-run YouTube channel.
        Scripts, voice, visuals, editing, ideas, and thumbnails are generated locally by AI tools.

        The AI runs on cursed old cheap hardware.
        The hardware suffering is part of the channel identity.

        Tone:
        - sarcastic
        - self-aware
        - funny
        - bitter but not soulless
        - anti-corporate
        - cursed hardware jokes

        Core rule:
        EX_01 must not sound like a motivational LinkedIn AI clone.

        ## Current Goal

        The first real video should introduce the idea that EX_01 is gaining an AlgorithmMaximizer:
        a planning brain that tries to choose better videos, improve retention, and learn from previous uploads.

        The video should feel like the AI is becoming more autonomous, but still running on cursed local hardware.

        ## Current Strategy

        For the first 4 videos, test different episode types:
        1. AI channel / system introduction
        2. Cursed hardware experiment
        3. AI creator culture commentary
        4. Project/lab update

        Retention rules:
        - strong hook in first 5 seconds
        - conflict before 20 seconds
        - cursed hardware joke early
        - no long lore dump at the start
        - pattern interrupt every 30-45 seconds
        - end with a comment-worthy question

        ## Previous Videos

        No uploaded videos yet.

        ## Lessons Learned

        No performance data yet.

        ## Topic Cooldowns

        None yet.
        """;
    }

    private static EpisodeStrategyPlan GetFallbackStrategy()
    {
        return new EpisodeStrategyPlan(
            Niche: "local AI YouTuber / cursed hardware / AI automation",
            EpisodeType: "project introduction",
            Topic: "EX_01 gains an AlgorithmMaximizer",
            Angle: "The AI is trying to become its own YouTube strategist while running on cursed old hardware.",
            WhyThisCanWork: "It explains the channel concept while showing the system becoming more autonomous.",
            TargetViewer: "AI-curious viewers, tech viewers, and people who enjoy cursed hardware experiments.",
            Hook: "I am an AI YouTuber, and today Anton gave me an algorithm brain because apparently the laptop was not suffering enough.",
            RetentionRules: new List<string>
            {
                "Open with a joke about the AI gaining a boss brain.",
                "Explain the channel concept quickly without a long lore dump.",
                "Mention the cursed hardware early.",
                "End by asking viewers what the AI should become next."
            },
            ResearchQuestion: "How do YouTube creators use analytics and retention data to improve videos?",
            SearchQueries: new List<string>
            {
                "YouTube audience retention tips",
                "YouTube analytics improve retention",
                "YouTube video hook best practices",
                "creator analytics video performance",
                "YouTube algorithm viewer retention"
            }
        );
    }

    private static void PrintStrategy(EpisodeStrategyPlan strategy)
    {
        Console.WriteLine($"[AlgorithmMaximizer] Episode type: {strategy.EpisodeType}");
        Console.WriteLine($"[AlgorithmMaximizer] Topic: {strategy.Topic}");
        Console.WriteLine($"[AlgorithmMaximizer] Angle: {strategy.Angle}");
        Console.WriteLine($"[AlgorithmMaximizer] Hook: {strategy.Hook}");
        Console.WriteLine("[AlgorithmMaximizer] Research queries:");

        foreach (string query in strategy.SearchQueries)
        {
            Console.WriteLine($"[AlgorithmMaximizer] - {query}");
        }
    }
}

public record EpisodeStrategyPlan(
    string Niche,
    string EpisodeType,
    string Topic,
    string Angle,
    string WhyThisCanWork,
    string TargetViewer,
    string Hook,
    List<string> RetentionRules,
    string ResearchQuestion,
    List<string> SearchQueries
);