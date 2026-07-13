using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Functions.ASKING;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Models;
using AI_YOUTUBER.Workflows;

namespace AI_YOUTUBER.ConsoleApp;

public sealed class Ex01ConsoleApplication
{
    private readonly ShortWorkflowService _shorts;
    private readonly LongFormWorkflowService _longForm;
    private readonly PreviewWorkflowService _previews;
    private readonly TestMenuService _tests;
    private readonly MemoryMenuService _memoryMenu;
    private readonly StatusService _status;
    private bool _exitRequested;

    public Ex01ConsoleApplication(Ex01Settings settings)
    {
        AskAI askAi = new(settings);
        VideoMemory memory = new(settings);
        _shorts = new ShortWorkflowService(settings, askAi, memory);
        _longForm = new LongFormWorkflowService(settings, askAi, memory);
        _previews = new PreviewWorkflowService(settings);
        _tests = new TestMenuService(settings, memory);
        _memoryMenu = new MemoryMenuService(settings, memory);
        _status = new StatusService(settings, memory);
        Directory.CreateDirectory(settings.OutputDirectory);
    }

    public async Task RunAsync()
    {
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            _exitRequested = true;
            Console.WriteLine();
            Console.WriteLine("Exit requested. Press Enter if EX_01 is waiting for input.");
        };
        Console.CancelKeyPress += handler;
        try
        {
            while (!_exitRequested)
            {
                ShowMainMenu();
                string? raw = Console.ReadLine();
                if (raw is null)
                    break;
                string choice = raw.Trim().ToLowerInvariant();
                if (_exitRequested)
                    break;

                try
                {
                    switch (choice)
                    {
                        case "1":
                        case "short":
                            await _shorts.RunAsync();
                            break;
                        case "2":
                        case "long":
                        case "long-form":
                            await _longForm.RunAsync();
                            break;
                        case "3":
                        case "short preview":
                        case "preview":
                            await _previews.RunAsync(VideoOrientation.Portrait);
                            break;
                        case "4":
                        case "landscape":
                        case "landscape preview":
                            await _previews.RunAsync(VideoOrientation.Landscape);
                            break;
                        case "5":
                        case "test":
                        case "tests":
                            await _tests.RunAsync();
                            break;
                        case "6":
                        case "memory":
                            await _memoryMenu.RunAsync();
                            break;
                        case "7":
                        case "status":
                        case "configuration":
                            await _status.ShowAsync();
                            break;
                        case "0":
                        case "exit":
                        case "quit":
                            _exitRequested = true;
                            break;
                        default:
                            Console.WriteLine("Invalid option. Choose a menu number or command.");
                            break;
                    }
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"Action failed: {exception.Message}");
                    Console.WriteLine("Returning to the main menu.");
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }

        Console.WriteLine("EX_01 console closed.");
    }

    private static void ShowMainMenu()
    {
        Console.WriteLine();
        Console.WriteLine("================================");
        Console.WriteLine("          EX_01 CONSOLE");
        Console.WriteLine("================================");
        Console.WriteLine();
        Console.WriteLine("1. Generate a Short");
        Console.WriteLine("2. Generate a long-form video");
        Console.WriteLine("3. Create a Short preview");
        Console.WriteLine("4. Create a landscape preview");
        Console.WriteLine("5. Test systems");
        Console.WriteLine("6. Memory tools");
        Console.WriteLine("7. Show configuration/status");
        Console.WriteLine("0. Exit");
        Console.WriteLine();
        Console.Write("Select an option: ");
    }
}
