using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Functions.ASKING;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Functions.RESEARCH;
using AI_YOUTUBER.Infrastructure;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Workflows;

public sealed class LongFormWorkflowService
{
    private readonly Ex01Settings _settings;
    private readonly AskAI _askAi;
    private readonly VideoMemory _memory;
    private readonly ProductionPipelineService _production;

    public LongFormWorkflowService(Ex01Settings settings, AskAI askAi, VideoMemory memory)
    {
        _settings = settings;
        _askAi = askAi;
        _memory = memory;
        _production = new ProductionPipelineService(settings);
    }

    public async Task RunAsync()
    {
        Console.Write("Topic [the EX_01 local AI experiment]: ");
        string topic = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(topic))
            topic = "the EX_01 local AI experiment";
        int targetMinutes = WorkflowConsole.ReadInteger("Target duration in minutes [5]: ", 5, 1, 30);
        bool useResearch = WorkflowConsole.AskYesNoDefaultNo("Research this topic first? [y/N]: ");
        bool polish = WorkflowConsole.AskYesNoDefaultNo("Polish the generated script with the Short model? [y/N]: ");
        Console.Write("Extra instruction [none]: ");
        string extraInstruction = Console.ReadLine()?.Trim() ?? "";

        string videoId = $"LONG_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}";
        string outputDirectory = Path.Combine(_settings.LongFormOutputDirectory, videoId);
        Directory.CreateDirectory(outputDirectory);
        DateTime startedUtc = DateTime.UtcNow;
        MemoryContext context = await _memory.BuildContextForTopicAsync(topic);
        string research = "";
        if (useResearch)
        {
            Console.WriteLine("Researching through the configured SearXNG and local summarizer...");
            try
            {
                research = await ResearchAI.DeepResearchAsync(topic);
                await File.WriteAllTextAsync(Path.Combine(outputDirectory, "research.txt"), research);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Research was unavailable: {exception.Message}");
                Console.WriteLine("The script prompt will be told that no research is available.");
                research = "";
            }
        }

        ScriptGenerationResult result;
        while (true)
        {
            try
            {
                result = await _askAi.GenerateLongFormScriptAsync(
                    topic,
                    targetMinutes,
                    extraInstruction,
                    context.FormattedContext,
                    research);
                if (polish && result.Validation.Success)
                {
                    ScriptGenerationResult polished = await _askAi.PolishLongFormScriptAsync(
                        result.Script,
                        targetMinutes);
                    if (polished.Validation.Success)
                        result = polished;
                    else
                        Console.WriteLine("Polish output failed validation; keeping the validated original script.");
                }
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
            WorkflowConsole.ShowScript("GENERATED LONG-FORM SCRIPT", result, allowEdit: false);
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
            if (choice is "c" or "cancel" or "0" || Console.IsInputRedirected && choice.Length == 0)
            {
                Console.WriteLine("Long-form generation cancelled. No production memory was written.");
                return;
            }
            Console.WriteLine("Invalid choice. The script has not been approved.");
        }

        ProductionResult production = await _production.CompleteAsync(
            videoId,
            topic,
            targetMinutes * 60,
            VideoOrientation.Landscape,
            outputDirectory,
            result.Script,
            result.Validation,
            polish ? $"{_settings.LongScriptModel} + {_settings.ShortScriptModel}" : _settings.LongScriptModel,
            AskAI.LongPromptVersion,
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
            new ProductionValidationEvidence
            {
                ScriptValidation = result.Validation,
                TtsCompleted = File.Exists(production.VoicePath),
                VoiceDurationValidation = production.VoiceValidation,
                RenderingCompleted = File.Exists(production.VideoPath),
                VideoValidation = production.VideoValidation,
                IsTestOrPreview = false,
                UserApprovedOfficialMemory = true
            });
        Console.WriteLine(saved is null
            ? "The video was kept, but the defensive memory save was refused or failed."
            : $"Official memory saved: {saved.VideoId}");
    }
}
