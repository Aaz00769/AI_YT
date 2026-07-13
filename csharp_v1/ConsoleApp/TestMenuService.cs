using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Infrastructure;
using AI_YOUTUBER.Models;
using AI_YOUTUBER.Rendering;
using SkiaSharp;

namespace AI_YOUTUBER.ConsoleApp;

public sealed class TestMenuService
{
    private readonly Ex01Settings _settings;
    private readonly VideoMemory _memory;
    private readonly DependencyChecker _dependencies;

    public TestMenuService(Ex01Settings settings, VideoMemory memory)
    {
        _settings = settings;
        _memory = memory;
        _dependencies = new DependencyChecker(settings);
    }

    public async Task RunAsync()
    {
        while (true)
        {
            Console.WriteLine();
            Console.WriteLine("TEST SYSTEMS");
            Console.WriteLine("1. Test all dependencies");
            Console.WriteLine("2. Test Ollama");
            Console.WriteLine("3. Test Piper");
            Console.WriteLine("4. Test ComfyUI");
            Console.WriteLine("5. Test SearXNG");
            Console.WriteLine("6. Test FFmpeg and ffprobe");
            Console.WriteLine("7. Test Short script validation");
            Console.WriteLine("8. Test Short preview pipeline");
            Console.WriteLine("9. Test memory system");
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
                    case "all":
                    case "dependencies":
                        foreach (DependencyStatus status in await _dependencies.CheckAllAsync())
                            Console.WriteLine(status.Summary);
                        break;
                    case "2":
                    case "ollama":
                        Console.WriteLine((await _dependencies.CheckOllamaAsync()).Summary);
                        break;
                    case "3":
                    case "piper":
                        Console.WriteLine((await _dependencies.CheckPiperAsync()).Summary);
                        break;
                    case "4":
                    case "comfy":
                    case "comfyui":
                        Console.WriteLine((await _dependencies.CheckComfyUiAsync()).Summary);
                        break;
                    case "5":
                    case "searx":
                    case "searxng":
                        Console.WriteLine((await _dependencies.CheckSearxngAsync()).Summary);
                        break;
                    case "6":
                    case "ffmpeg":
                        Console.WriteLine((await _dependencies.CheckFfmpegAsync()).Summary);
                        Console.WriteLine((await _dependencies.CheckFfprobeAsync()).Summary);
                        break;
                    case "7":
                    case "validation":
                        await QualityRegressionTests.RunAsync();
                        break;
                    case "8":
                    case "preview":
                        await TestPreviewAsync();
                        break;
                    case "9":
                    case "memory":
                        await MemoryIsolationTests.RunAsync(_settings, _memory);
                        break;
                    case "0":
                    case "back":
                        return;
                    default:
                        Console.WriteLine("Invalid test option.");
                        break;
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"FAIL - {exception.Message}");
            }
        }
    }

    private static async Task TestPreviewAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"ex01-preview-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "preview.png");
            await new AvatarVideoRenderer().CreateVisualAsync(
                "Isolated Short preview test",
                path,
                VideoOrientation.Portrait);
            using SKBitmap bitmap = SKBitmap.Decode(path)
                ?? throw new InvalidOperationException("Preview PNG could not be decoded.");
            if (bitmap.Width != 1080 || bitmap.Height != 1920)
                throw new InvalidOperationException($"Unexpected preview dimensions: {bitmap.Width}x{bitmap.Height}.");
            Console.WriteLine("Short preview pipeline: PASS - isolated 1080x1920 PNG created");
            Console.WriteLine("Official memory was not accessed.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
