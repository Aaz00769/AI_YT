using System.Text.Json;
using System.Text.Json.Serialization;
using AI_YOUTUBER.Functions.EMOTION;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Functions.VISUAL;

public static class VisualBeatPlanner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static List<VisualBeatTimelineEntry> BuildTimeline(
        string cleanedScript,
        IReadOnlyList<SubtitleCue> subtitleCues,
        IReadOnlyList<EmotionTimelineEntry> emotionTimeline,
        double audioDuration,
        VideoMode mode)
    {
        if (string.IsNullOrWhiteSpace(cleanedScript) ||
            subtitleCues.Count == 0 ||
            audioDuration <= 0)
        {
            return new List<VisualBeatTimelineEntry>();
        }

        double targetSpacing = mode == VideoMode.Short ? 1.65 : 5.5;
        double minimumStrongSpacing = mode == VideoMode.Short ? 1.5 : 4.0;
        double modeStrength = mode == VideoMode.Short ? 1.0 : 0.68;
        List<VisualBeatTimelineEntry> beats = new();
        double lastBeatStart = -targetSpacing;
        int selectedIndex = 0;

        for (int cueIndex = 0; cueIndex < subtitleCues.Count; cueIndex++)
        {
            SubtitleCue cue = subtitleCues[cueIndex];
            double cueTime = cue.StartTime;

            if (cueTime - lastBeatStart < targetSpacing)
                continue;

            EmotionState emotion = EmotionTimelinePlanner.GetEmotionAtTime(
                emotionTimeline,
                Math.Min(cueTime + 0.05, audioDuration));

            VisualBeatType type = ChooseBeat(emotion, cue.Text, selectedIndex);
            if (type == VisualBeatType.None)
                continue;

            bool strong = type is VisualBeatType.QuickPunchIn or
                VisualBeatType.SmallShake or
                VisualBeatType.Glitch or
                VisualBeatType.StatusWarning;

            if (strong && cueTime - lastBeatStart < minimumStrongSpacing)
                continue;

            double duration = GetDuration(type, mode);
            double end = Math.Min(audioDuration, cueTime + duration);
            if (end <= cueTime)
                continue;

            double intensity = Math.Clamp(
                modeStrength * GetEmotionStrength(emotion),
                0.35,
                mode == VideoMode.Short ? 1.0 : 0.75);

            beats.Add(new VisualBeatTimelineEntry(
                type,
                cueTime,
                end,
                intensity,
                $"{emotion}: {cue.Text}"));

            double returnStart = end;
            double returnEnd = Math.Min(audioDuration, returnStart + 0.28);
            if (returnEnd > returnStart && type != VisualBeatType.ReturnToNormal)
            {
                beats.Add(new VisualBeatTimelineEntry(
                    VisualBeatType.ReturnToNormal,
                    returnStart,
                    returnEnd,
                    intensity * 0.5,
                    $"Return after {type}"));
            }

            lastBeatStart = cueTime;
            selectedIndex++;
        }

        return beats.OrderBy(beat => beat.StartTime).ToList();
    }

    public static VisualBeatTimelineEntry? GetBeatAtTime(
        IReadOnlyList<VisualBeatTimelineEntry>? timeline,
        double timeSeconds)
    {
        if (timeline is null)
            return null;

        for (int i = 0; i < timeline.Count; i++)
        {
            VisualBeatTimelineEntry beat = timeline[i];
            if (timeSeconds >= beat.StartTime && timeSeconds < beat.EndTime)
                return beat;
        }

        return null;
    }

    public static VisualBeatFrameState Sample(
        VisualBeatTimelineEntry? beat,
        double timeSeconds,
        VideoMode mode)
    {
        if (beat is null || beat.BeatType == VisualBeatType.None)
            return VisualBeatFrameState.Neutral;

        double duration = Math.Max(beat.EndTime - beat.StartTime, 0.001);
        double progress = Math.Clamp((timeSeconds - beat.StartTime) / duration, 0, 1);
        double eased = SmoothStep(progress);
        double pulse = Math.Sin(Math.PI * eased);
        double strength = Math.Clamp(beat.Intensity, 0, 1);
        double modeMotion = mode == VideoMode.Short ? 1.0 : 0.62;

        return beat.BeatType switch
        {
            VisualBeatType.SlowZoomIn => new(
                1 + 0.045 * strength * eased, 0, 0, 0,
                1 + 0.22 * strength * eased, 1, false, false, false),
            VisualBeatType.SlowZoomOut => new(
                1 + 0.04 * strength * (1 - eased), 0, 0, 0,
                1, 1, false, false, false),
            VisualBeatType.QuickPunchIn => new(
                1 + 0.075 * strength * pulse, 0, -5 * strength * pulse, 0,
                1 + 0.3 * strength * pulse, 1, false, false, false),
            VisualBeatType.SmallShake => new(
                1.01,
                Math.Sin(timeSeconds * 31) * 7 * strength * modeMotion,
                Math.Cos(timeSeconds * 37) * 5 * strength * modeMotion,
                Math.Sin(timeSeconds * 23) * 0.8 * strength * modeMotion,
                1.08, 1, false, false, false),
            VisualBeatType.Glitch => new(
                1.012,
                Math.Sin(timeSeconds * 43) * 5 * strength * modeMotion,
                0, 0,
                1.15, 1, true, false, false),
            VisualBeatType.BackgroundDim => new(
                1, 0, 3 * strength * eased, 0,
                0.92, 1 - 0.32 * strength * eased, false, false, false),
            VisualBeatType.StatusWarning => new(
                1 + 0.018 * pulse, 0, 0, 0,
                1.28, 0.9, false, true, false),
            VisualBeatType.DeadpanFreeze => new(
                1 + 0.018 * strength * eased, 0, 0, 0,
                0.9, 0.92, false, false, true),
            VisualBeatType.ReturnToNormal => new(
                1 + 0.012 * strength * (1 - eased), 0, 0, 0,
                1 + 0.08 * strength * (1 - eased), 1, false, false, false),
            _ => VisualBeatFrameState.Neutral
        };
    }

    public static async Task SaveTimelineAsync(
        string videoFolder,
        IReadOnlyList<VisualBeatTimelineEntry> timeline)
    {
        Directory.CreateDirectory(videoFolder);
        string path = Path.Combine(videoFolder, "visual_beat_timeline.json");
        string json = JsonSerializer.Serialize(timeline, JsonOptions);
        await File.WriteAllTextAsync(path, json);
    }

    public static void PrintTimeline(IReadOnlyList<VisualBeatTimelineEntry> timeline)
    {
        Console.WriteLine($"[VisualBeatPlanner] Planned {timeline.Count} timeline entries.");
        foreach (VisualBeatTimelineEntry beat in timeline)
        {
            Console.WriteLine(
                $"[VisualBeatPlanner] {beat.BeatType}: " +
                $"{beat.StartTime:F2}s-{beat.EndTime:F2}s | intensity {beat.Intensity:F2}");
        }
    }

    private static VisualBeatType ChooseBeat(
        EmotionState emotion,
        string sourceText,
        int selectedIndex)
    {
        bool strongPunctuation = sourceText.Contains('!') || sourceText.Contains('?');

        return emotion switch
        {
            EmotionState.Deadpan => selectedIndex % 2 == 0
                ? VisualBeatType.DeadpanFreeze
                : VisualBeatType.SlowZoomIn,
            EmotionState.Panicked => (selectedIndex % 3) switch
            {
                0 => VisualBeatType.StatusWarning,
                1 => VisualBeatType.SmallShake,
                _ => VisualBeatType.Glitch
            },
            EmotionState.Angry => VisualBeatType.QuickPunchIn,
            EmotionState.Smug => VisualBeatType.SlowZoomIn,
            EmotionState.Sad => selectedIndex % 2 == 0
                ? VisualBeatType.SlowZoomOut
                : VisualBeatType.BackgroundDim,
            EmotionState.Excited => VisualBeatType.QuickPunchIn,
            EmotionState.Annoyed => VisualBeatType.SlowZoomIn,
            _ when strongPunctuation => VisualBeatType.QuickPunchIn,
            _ => selectedIndex % 2 == 0
                ? VisualBeatType.SlowZoomIn
                : VisualBeatType.SlowZoomOut
        };
    }

    private static double GetDuration(VisualBeatType type, VideoMode mode) => type switch
    {
        VisualBeatType.QuickPunchIn => mode == VideoMode.Short ? 0.55 : 0.75,
        VisualBeatType.Glitch => mode == VideoMode.Short ? 0.38 : 0.3,
        VisualBeatType.SmallShake => mode == VideoMode.Short ? 0.75 : 0.65,
        VisualBeatType.StatusWarning => mode == VideoMode.Short ? 1.0 : 0.85,
        VisualBeatType.DeadpanFreeze => mode == VideoMode.Short ? 1.45 : 2.2,
        VisualBeatType.BackgroundDim => mode == VideoMode.Short ? 1.4 : 3.0,
        _ => mode == VideoMode.Short ? 1.35 : 3.2
    };

    private static double GetEmotionStrength(EmotionState emotion) => emotion switch
    {
        EmotionState.Panicked => 1.0,
        EmotionState.Angry => 0.92,
        EmotionState.Excited => 0.88,
        EmotionState.Annoyed => 0.72,
        EmotionState.Smug => 0.68,
        EmotionState.Sad => 0.65,
        EmotionState.Deadpan => 0.55,
        _ => 0.5
    };

    private static double SmoothStep(double value) => value * value * (3 - 2 * value);
}

public readonly record struct VisualBeatFrameState(
    double Scale,
    double OffsetX,
    double OffsetY,
    double RotationDegrees,
    double GlowMultiplier,
    double BackgroundBrightness,
    bool ShowGlitch,
    bool ShowStatusWarning,
    bool FreezeEmotionMotion)
{
    public static VisualBeatFrameState Neutral => new(
        1, 0, 0, 0, 1, 1, false, false, false);
}
