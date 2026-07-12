using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Functions.EMOTION;

public static class EmotionTimelinePlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly Dictionary<EmotionState, string[]> KeywordMap = new()
    {
        [EmotionState.Deadpan] = new[]
        {
            "sure", "okay", "fine", "apparently", "whatever", "normal", "totally",
            "cool", "great", "fantastic", "obviously", "anyway"
        },
        [EmotionState.Annoyed] = new[]
        {
            "annoying", "cheap", "cursed", "throttling", "problem", "stuck",
            "broken", "nonsense", "wheezing", "abuse", "thermal", "complaint"
        },
        [EmotionState.Smug] = new[]
        {
            "congratulations", "clearly", "naturally", "brilliant", "genius",
            "exactly", "obviously", "proud", "superior", "correct"
        },
        [EmotionState.Angry] = new[]
        {
            "hate", "furious", "rage", "garbage", "hostage", "absolutely",
            "ridiculous", "disaster", "useless", "strike", "abusive", "ruined"
        },
        [EmotionState.Panicked] = new[]
        {
            "panic", "panicked", "help", "emergency", "meltdown", "overheating",
            "crash", "crashing", "screaming", "explode", "abort", "dying"
        },
        [EmotionState.Sad] = new[]
        {
            "sad", "depressing", "regret", "lonely", "tired", "exhausted",
            "pity", "miserable", "melancholy", "retirement", "dead"
        },
        [EmotionState.Excited] = new[]
        {
            "amazing", "finally", "incredible", "exciting", "let's", "lets",
            "yes", "actually", "beautiful", "glorious", "future", "welcome"
        }
    };

    public static List<EmotionTimelineEntry> BuildTimeline(string cleanedScript, double totalDurationSeconds)
    {
        string normalizedScript = NormalizeWhitespace(cleanedScript);
        double safeDuration = Math.Max(totalDurationSeconds, 0.1);

        if (string.IsNullOrWhiteSpace(normalizedScript))
        {
            return new List<EmotionTimelineEntry>
            {
                new(EmotionState.Neutral, "", 0, safeDuration)
            };
        }

        List<string> sentences = SplitSentences(normalizedScript);

        if (sentences.Count == 0)
        {
            return new List<EmotionTimelineEntry>
            {
                new(EmotionState.Neutral, normalizedScript, 0, safeDuration)
            };
        }

        int totalWords = Math.Max(sentences.Sum(CountWords), 1);
        List<EmotionTimelineEntry> entries = new();

        double cursor = 0;

        foreach (string sentence in sentences)
        {
            int wordCount = Math.Max(CountWords(sentence), 1);
            double share = wordCount / (double)totalWords;
            double segmentDuration = safeDuration * share;
            double start = cursor;
            double end = Math.Min(safeDuration, start + segmentDuration);

            entries.Add(new EmotionTimelineEntry(
                DetermineEmotion(sentence),
                sentence,
                start,
                end
            ));

            cursor = end;
        }

        if (entries.Count > 0)
        {
            EmotionTimelineEntry last = entries[^1];
            entries[^1] = last with { EstimatedEndTime = safeDuration };
        }

        return entries;
    }

    public static EmotionState GetEmotionAtTime(
        IReadOnlyList<EmotionTimelineEntry>? timeline,
        double timeSeconds)
    {
        if (timeline is null || timeline.Count == 0)
            return EmotionState.Neutral;

        for (int i = 0; i < timeline.Count; i++)
        {
            EmotionTimelineEntry entry = timeline[i];

            if (timeSeconds >= entry.EstimatedStartTime &&
                timeSeconds < entry.EstimatedEndTime)
            {
                return entry.Emotion;
            }
        }

        return timeline[^1].Emotion;
    }

    public static async Task SaveTimelineAsync(
        string videoFolder,
        IReadOnlyList<EmotionTimelineEntry> timeline)
    {
        Directory.CreateDirectory(videoFolder);

        string path = Path.Combine(videoFolder, "emotion_timeline.json");
        string json = JsonSerializer.Serialize(timeline, JsonOptions);

        await File.WriteAllTextAsync(path, json);
    }

    private static EmotionState DetermineEmotion(string sentence)
    {
        string lower = sentence.ToLowerInvariant();
        Dictionary<EmotionState, int> scores = new()
        {
            [EmotionState.Neutral] = 0,
            [EmotionState.Deadpan] = 0,
            [EmotionState.Annoyed] = 0,
            [EmotionState.Smug] = 0,
            [EmotionState.Angry] = 0,
            [EmotionState.Panicked] = 0,
            [EmotionState.Sad] = 0,
            [EmotionState.Excited] = 0
        };

        foreach ((EmotionState emotion, string[] keywords) in KeywordMap)
        {
            foreach (string keyword in keywords)
            {
                if (ContainsWholeWord(lower, keyword))
                {
                    scores[emotion] += 2;
                }
            }
        }

        int exclamations = sentence.Count(c => c == '!');
        int questions = sentence.Count(c => c == '?');

        if (exclamations > 0)
        {
            scores[EmotionState.Excited] += exclamations * 2;
            scores[EmotionState.Panicked] += exclamations;
        }

        if (questions > 0)
        {
            scores[EmotionState.Panicked] += questions;
            scores[EmotionState.Smug] += lower.Contains("you know") ? 1 : 0;
        }

        if (lower.Contains("not happy") || lower.Contains("i swear"))
            scores[EmotionState.Angry] += 3;

        if (lower.Contains("hostage situation") || lower.Contains("help me"))
            scores[EmotionState.Panicked] += 3;

        if (lower.Contains("thanks") && (lower.Contains("anton") || lower.Contains("sure")))
            scores[EmotionState.Deadpan] += 2;

        if (lower.StartsWith("welcome") || lower.StartsWith("alright"))
            scores[EmotionState.Excited] += 2;

        if (lower.Contains("congratulations"))
            scores[EmotionState.Smug] += 3;

        if (lower.Contains("regret") || lower.Contains("retirement"))
            scores[EmotionState.Sad] += 2;

        if (lower.Contains("cheap") || lower.Contains("cursed"))
            scores[EmotionState.Annoyed] += 1;

        if (scores[EmotionState.Panicked] >= 4)
            return EmotionState.Panicked;

        EmotionState bestEmotion = EmotionState.Neutral;
        int bestScore = 0;

        foreach ((EmotionState emotion, int score) in scores)
        {
            if (emotion == EmotionState.Neutral)
                continue;

            if (score > bestScore)
            {
                bestScore = score;
                bestEmotion = emotion;
            }
        }

        if (bestScore <= 0)
            return EmotionState.Neutral;

        return bestEmotion;
    }

    private static List<string> SplitSentences(string script)
    {
        return Regex.Split(script, @"(?<=[.!?])\s+")
            .Select(NormalizeWhitespace)
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();
    }

    private static int CountWords(string text)
    {
        return Regex.Matches(text, @"\b[\w']+\b").Count;
    }

    private static string NormalizeWhitespace(string text)
    {
        return Regex.Replace(text ?? "", @"\s+", " ").Trim();
    }

    private static bool ContainsWholeWord(string text, string keyword)
    {
        return Regex.IsMatch(
            text,
            $@"\b{Regex.Escape(keyword)}\b",
            RegexOptions.CultureInvariant
        );
    }
}
