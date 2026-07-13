using System.Text.Json.Serialization;

namespace AI_YOUTUBER.Models;

public sealed record VisualBeatTimelineEntry(
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    VisualBeatType BeatType,
    double StartTime,
    double EndTime,
    double Intensity,
    string Reason
);
