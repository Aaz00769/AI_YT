using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Models;
using AI_YOUTUBER.Rendering;

namespace AI_YOUTUBER.Workflows;

public sealed class PreviewWorkflowService(Ex01Settings settings)
{
    private readonly AvatarVideoRenderer _renderer = new();

    public async Task RunAsync(VideoOrientation orientation)
    {
        Console.Write("Preview topic [EX_01 local AI console]: ");
        string topic = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(topic))
            topic = "EX_01 local AI console";

        string id = $"PREVIEW_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}";
        string directory = Path.Combine(settings.PreviewOutputDirectory, id);
        string path = Path.Combine(directory, "preview.png");
        await _renderer.CreateVisualAsync(topic, path, orientation);
        Console.WriteLine($"Preview created: {path}");
        Console.WriteLine("No production memory was written.");
    }
}
