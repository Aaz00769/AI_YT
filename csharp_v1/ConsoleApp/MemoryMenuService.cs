using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Infrastructure;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.ConsoleApp;

public sealed class MemoryMenuService(Ex01Settings settings, VideoMemory memory)
{
    public async Task RunAsync()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("MEMORY TOOLS");
            Console.WriteLine("1. List official video memories");
            Console.WriteLine("2. Show memory context for a topic");
            Console.WriteLine("3. Rebuild channel state");
            Console.WriteLine("4. Test memory in isolation");
            Console.WriteLine("0. Back");
            Console.Write("Select an option: ");
            string? raw = Console.ReadLine();
            if (raw is null)
                return;
            string choice = raw.Trim().ToLowerInvariant();

            try
            {
                switch (choice)
                {
                    case "1":
                    case "list":
                        await ListAsync();
                        break;
                    case "2":
                    case "context":
                        await ShowContextAsync();
                        break;
                    case "3":
                    case "rebuild":
                        await RebuildAsync();
                        break;
                    case "4":
                    case "test":
                        await MemoryIsolationTests.RunAsync(settings, memory);
                        break;
                    case "0":
                    case "back":
                        return;
                    default:
                        Console.WriteLine("Invalid memory option.");
                        break;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Memory operation failed: {exception.Message}");
            }
        }
    }

    private async Task ListAsync()
    {
        IReadOnlyList<VideoMemoryRecord> records = await memory.LoadAllAsync();
        if (records.Count == 0)
        {
            Console.WriteLine("No official video memories found.");
            return;
        }

        foreach (VideoMemoryRecord record in records)
        {
            Console.WriteLine();
            Console.WriteLine($"ID: {record.VideoId}");
            Console.WriteLine($"Date: {record.CreatedUtc:yyyy-MM-dd HH:mm:ss} UTC");
            Console.WriteLine($"Title: {record.Title}");
            Console.WriteLine($"Topic: {record.Topic}");
            Console.WriteLine($"Script hash: {record.ScriptHash}");
            Console.WriteLine($"Video path: {record.VideoPath}");
        }
    }

    private async Task ShowContextAsync()
    {
        Console.Write("Topic: ");
        string topic = Console.ReadLine()?.Trim() ?? "";
        MemoryContext context = await memory.BuildContextForTopicAsync(topic);
        Console.WriteLine();
        Console.WriteLine("EXACT SCRIPT-GENERATION MEMORY CONTEXT");
        Console.WriteLine("--------------------------------------");
        Console.WriteLine(context.FormattedContext);
    }

    private async Task RebuildAsync()
    {
        Console.Write("Rebuild channel state from official video memories? [y/N]: ");
        string answer = Console.ReadLine()?.Trim() ?? "";
        if (!answer.Equals("y", StringComparison.OrdinalIgnoreCase) &&
            !answer.Equals("yes", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Rebuild cancelled.");
            return;
        }

        await memory.RebuildChannelStateAsync();
        Console.WriteLine($"Channel state rebuilt: {memory.ChannelStatePath}");
    }
}
