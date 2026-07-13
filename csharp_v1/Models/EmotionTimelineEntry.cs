using System.Text.Json.Serialization;

namespace AI_YOUTUBER.Models;

public sealed record EmotionTimelineEntry(
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    EmotionState Emotion,
    string SourceText,
    double EstimatedStartTime,
    double EstimatedEndTime
);
