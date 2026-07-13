using System.Text.Json.Serialization;

namespace AI_YOUTUBER.Models;

public sealed class OllamaGenerateRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = "";

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("think")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Think { get; set; }

    [JsonPropertyName("options")]
    public OllamaGenerateOptions Options { get; set; } = new();
}

public sealed class OllamaGenerateOptions
{
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("num_ctx")]
    public int NumContextTokens { get; set; }

    [JsonPropertyName("num_predict")]
    public int MaximumOutputTokens { get; set; }
}

public sealed class OllamaGenerationResult
{
    public string Text { get; set; } = "";
    public bool Completed { get; set; }
    public string DoneReason { get; set; } = "";
    public int OutputTokenCount { get; set; }
    public int MaximumOutputTokens { get; set; }
    public bool ReachedOutputTokenLimit { get; set; }
}
