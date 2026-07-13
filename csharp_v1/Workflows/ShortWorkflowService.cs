using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Functions.ASKING;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Infrastructure;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Workflows;

public sealed class ShortWorkflowService
{
    private readonly Ex01Settings _settings;
    private readonly AskAI _askAi;
    private readonly VideoMemory _memory;
    private readonly ProductionPipelineService _production;

    public ShortWorkflowService(Ex01Settings settings, AskAI askAi, VideoMemory memory)
    {
        _settings = settings;
        _askAi = askAi;
        _memory = memory;
        _production = new ProductionPipelineService(settings);
    }

    public async Task RunAsync()
    {
        Console.Write("Topic [EX_01 surviving local AI on old hardware]: ");
        string topic = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(topic))
            topic = "EX_01 surviving local AI on old hardware";
        int targetSeconds = WorkflowConsole.ReadInteger("Target duration [30]: ", 30, 15, 60);
        Console.Write("Extra instruction [none]: ");
        string extraInstruction = Console.ReadLine()?.Trim() ?? "";

        string videoId = $"SHORT_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}";
        string outputDirectory = Path.Combine(_settings.ShortsOutputDirectory, videoId);
        Directory.CreateDirectory(outputDirectory);
        DateTime startedUtc = DateTime.UtcNow;
        MemoryContext context = await _memory.BuildContextForTopicAsync(topic);
        ScriptGenerationResult result;

        while (true)
        {
            try
            {
                result = await _askAi.GenerateShortScriptAsync(
                    topic,
                    targetSeconds,
                    extraInstruction,
                    context.FormattedContext);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Script generation failed: {exception.Message}");
                Console.WriteLine("Returning to the main menu. No production memory was written.");
                return;
            }

            await File.WriteAllTextAsync(Path.Combine(outputDirectory, "script.txt"), result.Script);
            await JsonFile.WriteAtomicAsync(
                Path.Combine(outputDirectory, "script_validation.json"),
                result.Validation);
            WorkflowConsole.ShowScript("GENERATED SHORT SCRIPT", result, allowEdit: true);
            Console.Write("Select an option: ");
            string choice = WorkflowConsole.Normalize(Console.ReadLine());

            if (choice is "a" or "approve")
            {
                if (!result.Validation.Success)
                {
                    Console.WriteLine("This script cannot be approved until it passes validation.");
                    continue;
                }
                break;
            }
            if (choice is "r" or "regenerate")
                continue;
            if (choice is "e" or "edit")
            {
                Console.Write("Extra instruction [none]: ");
                extraInstruction = Console.ReadLine()?.Trim() ?? "";
                continue;
            }
            if (choice is "c" or "cancel" or "0" || Console.IsInputRedirected && choice.Length == 0)
            {
                Console.WriteLine("Short cancelled. No production memory was written.");
                return;
            }
            Console.WriteLine("Invalid choice. The script has not been approved.");
        }

        ProductionResult production = await _production.CompleteAsync(
            videoId,
            topic,
            targetSeconds,
            VideoOrientation.Portrait,
            outputDirectory,
            result.Script,
            result.Validation,
            _settings.ShortScriptModel,
            AskAI.ShortPromptVersion,
            result.AttemptCount,
            result.Elapsed,
            startedUtc);
        if (!production.Success)
        {
            Console.WriteLine($"Files kept in: {outputDirectory}");
            Console.WriteLine("Official memory was not written.");
            return;
        }

        bool approveMemory = WorkflowConsole.AskYesNoDefaultNo(
            "Save this completed video into official EX_01 memory? [y/N]: ");
        production.Metrics.UserApprovedOfficialMemory = approveMemory;
        await JsonFile.WriteAtomicAsync(production.MetricsPath, production.Metrics);
        if (!approveMemory)
        {
            Console.WriteLine("Video kept. Official memory was not changed.");
            return;
        }

        VideoMemoryRecord? saved = await _memory.SaveCompletedVideoAsync(
            videoId,
            topic,
            topic,
            production.VideoPath,
            production.ScriptPath,
            result.Script,
            CreateEvidence(result.Validation, production, userApproved: true));
        Console.WriteLine(saved is null
            ? "The video was kept, but the defensive memory save was refused or failed."
            : $"Official memory saved: {saved.VideoId}");
    }

    private static ProductionValidationEvidence CreateEvidence(
        ScriptValidationResult scriptValidation,
        ProductionResult production,
        bool userApproved) => new()
    {
        ScriptValidation = scriptValidation,
        TtsCompleted = File.Exists(production.VoicePath),
        VoiceDurationValidation = production.VoiceValidation,
        RenderingCompleted = File.Exists(production.VideoPath),
        VideoValidation = production.VideoValidation,
        IsTestOrPreview = false,
        UserApprovedOfficialMemory = userApproved
    };
}
