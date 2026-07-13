using System.Security.Cryptography;
using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Infrastructure;

public static class MemoryIsolationTests
{
    public static async Task RunAsync(Ex01Settings settings, VideoMemory officialMemory)
    {
        Dictionary<string, string> before = Snapshot(settings.OfficialVideoMemoryDirectory);

        VideoMemoryRecord? refused = await officialMemory.SaveCompletedVideoAsync(
            $"refused-{Guid.NewGuid():N}",
            "Refused test",
            "Memory boundary",
            Path.Combine(Path.GetTempPath(), "missing-video.mp4"),
            Path.Combine(Path.GetTempPath(), "missing-script.txt"),
            "This deliberately incomplete test record must never become official memory.",
            evidence: null,
            deterministicExtraction: true);
        Assert(refused is null, "Official memory accepted a record without approval and validation.");

        string root = Path.Combine(Path.GetTempPath(), $"ex01-memory-test-{Guid.NewGuid():N}");
        try
        {
            VideoMemory isolated = new(settings, Path.Combine(root, "memory"), isolatedTestMode: true);
            VideoMemoryRecord? saved = await isolated.SaveCompletedVideoAsync(
                "isolated-test-record",
                "Isolated test",
                "Memory isolation",
                Path.Combine(root, "fake-video.mp4"),
                Path.Combine(root, "fake-script.txt"),
                "This is an isolated memory test. It is not a production video.",
                evidence: null,
                deterministicExtraction: true);
            Assert(saved is not null, "Isolated memory record was not saved.");
            Assert((await isolated.LoadAllAsync()).Count == 1, "Isolated memory count is incorrect.");
            MemoryContext context = await isolated.BuildContextForTopicAsync("memory isolation");
            Assert(context.FormattedContext.Contains("isolated-test-record", StringComparison.Ordinal),
                "Isolated retrieval did not return its test record.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        Dictionary<string, string> after = Snapshot(settings.OfficialVideoMemoryDirectory);
        Assert(before.Count == after.Count && before.All(item => after.TryGetValue(item.Key, out string? hash) && hash == item.Value),
            "A memory test changed official production memory.");
        Console.WriteLine("Memory isolation and defensive approval tests: PASS");
    }

    private static Dictionary<string, string> Snapshot(string directory)
    {
        if (!Directory.Exists(directory))
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        return Directory.EnumerateFiles(directory, "*.json")
            .ToDictionary(
                path => Path.GetFileName(path)!,
                path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                StringComparer.OrdinalIgnoreCase);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
