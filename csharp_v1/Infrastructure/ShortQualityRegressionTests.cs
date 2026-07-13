using System.Diagnostics;
using System.Text.Json;
using AI_YOUTUBER.Functions.ASKING;
using AI_YOUTUBER.Functions.BATCH;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Infrastructure;

public static class ShortQualityRegressionTests
{
    private const string MalformedProductionResponse =
        "Hey, I tried to run a 4K video on this ancient Dell and it crashed. Again. Running on an i7-";

    private const string ValidShortScript =
        "Anton gave me a thirty-second deadline, which is brave when the Quadro T1000 measures time in thermal warnings. " +
        "I am testing whether one focused local script can survive without inventing a benchmark, a crash, or a heroic ending. " +
        "The plan is simple: write complete sentences, let Piper speak naturally, and reject any tiny audio file pretending to be a finished Short. " +
        "If every gate agrees, the workstation earns one quiet victory. If not, nothing enters memory.";

    public static async Task RunAsync()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"ex01-short-quality-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        using IDisposable testMemoryScope = VideoMemory.BeginIsolatedTestMemoryScope("short-quality");

        try
        {
            TestOllamaRequestSerialization();
            ShortScriptGenerationResult exhausted = await TestMalformedResponseAndRetriesAsync();
            ShortScriptValidationResult validScriptValidation = TestScriptValidationCases();
            TestVoiceDurationValidation();
            await TestTechnicallyValidButShortVideoAsync(root, validScriptValidation);
            await TestBatchStopsBeforeSideEffectsAsync(root, exhausted);
            await TestOfficialMemoryDefenseAsync(root, exhausted);
            Console.WriteLine("[ShortQualityTests] All Short quality-control regression tests passed.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void TestOllamaRequestSerialization()
    {
        int outputBudget = AskAI.CalculateShortOutputTokenBudget(81);
        Assert(outputBudget >= 512, "Short output budget must be at least 512 tokens.");
        OllamaGenerateRequest request = new()
        {
            Model = "qwen3:14b",
            Prompt = "Return narration only.",
            Think = false,
            Options = new OllamaGenerateOptions
            {
                Temperature = 0.7,
                NumContextTokens = 4096,
                MaximumOutputTokens = outputBudget
            }
        };

        string json = JsonSerializer.Serialize(request);
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        Assert(root.TryGetProperty("think", out JsonElement think),
            "Ollama request did not serialize the expected 'think' property.");
        Assert(think.ValueKind == JsonValueKind.False,
            "Ollama script request did not serialize think:false.");
        Assert(root.GetProperty("options").GetProperty("num_predict").GetInt32() >= 512,
            "Ollama num_predict serialization used an unsafe Short output budget.");
        Console.WriteLine("[ShortQualityTests] Ollama think:false and token-budget serialization passed.");
    }

    private static async Task<ShortScriptGenerationResult> TestMalformedResponseAndRetriesAsync()
    {
        int generationCalls = 0;
        ShortScriptGenerationResult result = await AskAI.GenerateValidatedShortScriptCoreAsync(
            "Write a 30-second Short.",
            minimumWords: 66,
            maximumWords: 81,
            maximumAttempts: 3,
            initialFailureReason: null,
            generateAsync: (_, _) =>
            {
                generationCalls++;
                return Task.FromResult(new OllamaGenerationResult
                {
                    Text = MalformedProductionResponse,
                    Completed = true,
                    DoneReason = "length",
                    OutputTokenCount = 243,
                    MaximumOutputTokens = 243,
                    ReachedOutputTokenLimit = true
                });
            });

        Assert(generationCalls == 3 && result.AttemptCount == 3,
            "Malformed narration was not retried exactly twice after the initial attempt.");
        Assert(!result.Validation.Success, "Malformed production narration unexpectedly passed.");
        Assert(result.Validation.WordCount < result.Validation.MinimumWords,
            "Malformed narration was not detected as too short.");
        Assert(result.Validation.AppearsTruncated,
            "Dangling-hyphen narration was not detected as apparently truncated.");
        Assert(result.Validation.ReachedOutputTokenLimit,
            "Exact output-token-limit completion was not rejected.");
        Console.WriteLine("[ShortQualityTests] Malformed-response retry regression passed.");
        return result;
    }

    private static ShortScriptValidationResult TestScriptValidationCases()
    {
        ShortScriptValidationResult valid = ShortScriptValidator.Validate(
            ValidShortScript,
            66,
            81,
            new OllamaGenerationResult
            {
                Completed = true,
                DoneReason = "stop",
                OutputTokenCount = 120,
                MaximumOutputTokens = 512
            });
        Assert(valid.Success, "Known-valid 66-81-word narration did not pass.");

        ShortScriptValidationResult thinking = ShortScriptValidator.Validate(
            $"<think>Do not expose this.</think> {ValidShortScript}",
            66,
            90);
        Assert(!thinking.Success && thinking.Errors.Any(error =>
                error.Contains("thinking", StringComparison.OrdinalIgnoreCase)),
            "Thinking tags were not rejected.");

        string noTerminalPunctuation = ValidShortScript.TrimEnd('.');
        ShortScriptValidationResult missingTerminal = ShortScriptValidator.Validate(
            noTerminalPunctuation,
            66,
            81);
        Assert(!missingTerminal.Success && missingTerminal.AppearsTruncated,
            "Narration without terminal punctuation was not rejected as truncated.");

        ShortScriptValidationResult tokenLimited = ShortScriptValidator.Validate(
            ValidShortScript,
            66,
            81,
            new OllamaGenerationResult
            {
                Text = ValidShortScript,
                Completed = true,
                DoneReason = "length",
                OutputTokenCount = 512,
                MaximumOutputTokens = 512,
                ReachedOutputTokenLimit = true
            });
        Assert(!tokenLimited.Success && tokenLimited.AppearsTruncated,
            "A generation result at its exact output-token limit unexpectedly passed.");
        Console.WriteLine("[ShortQualityTests] Script validation cases passed.");
        return valid;
    }

    private static void TestVoiceDurationValidation()
    {
        VoiceDurationValidationResult valid = VoiceDurationValidator.Validate(30.0, 30.0);
        Assert(valid.Success, "A 30-second voice for a 30-second target did not pass.");

        VoiceDurationValidationResult invalid = VoiceDurationValidator.Validate(5.85, 30.0);
        Assert(!invalid.Success && invalid.MinimumAcceptedDurationSeconds == 22.5,
            "A 5.85-second voice unexpectedly passed a 30-second target.");
        Console.WriteLine("[ShortQualityTests] Voice-duration validation cases passed.");
    }

    private static async Task TestTechnicallyValidButShortVideoAsync(
        string root,
        ShortScriptValidationResult scriptValidation)
    {
        string scriptPath = Path.Combine(root, "valid-script.txt");
        string videoPath = Path.Combine(root, "technically-valid-too-short.mp4");
        await File.WriteAllTextAsync(scriptPath, ValidShortScript);
        RunProcess("ffmpeg", new[]
        {
            "-y",
            "-f", "lavfi",
            "-i", "color=c=0x102a52:s=360x640:r=12:d=5.85",
            "-f", "lavfi",
            "-i", "sine=frequency=440:duration=5.85",
            "-t", "5.85",
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-shortest",
            videoPath
        });

        VoiceDurationValidationResult voiceValidation = VoiceDurationValidator.Validate(5.85, 30.0);
        VideoValidationResult validation = await new VideoValidationService().ValidateAsync(
            new VideoValidationRequest
            {
                VideoPath = videoPath,
                ScriptPath = scriptPath,
                RequestedDurationSeconds = 30,
                MinimumDurationRatio = 0.75,
                MaximumDurationRatio = 1.25,
                MaximumAudioVideoDifferenceSeconds = 1.0,
                MinimumFileSizeBytes = 100,
                ScriptValidation = scriptValidation,
                VoiceDurationValidation = voiceValidation
            });

        Assert(validation.FullValidationPerformed && validation.HasVideo && validation.HasAudio,
            "Synthetic MP4 was not recognized as technically readable media.");
        Assert(!validation.Success && validation.DurationSeconds < 22.5,
            "Technically valid 5.8-second MP4 unexpectedly passed a 30-second job.");
        Assert(validation.Errors.Any(error =>
                error.Contains("requested", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("accepted", StringComparison.OrdinalIgnoreCase)),
            "Too-short MP4 did not record the requested-duration failure.");
        Console.WriteLine("[ShortQualityTests] Requested-job video-duration regression passed.");
    }

    private static async Task TestBatchStopsBeforeSideEffectsAsync(
        string root,
        ShortScriptGenerationResult exhausted)
    {
        int piperCalls = 0;
        int normalizationCalls = 0;
        int durationCalls = 0;
        int renderingProcessCalls = 0;
        BatchOptions options = new()
        {
            ShortDurationSeconds = 30,
            MaximumScriptGenerationAttempts = 3,
            StopOnVideoFailure = true
        };
        ShortBatchCoordinator coordinator = new(
            Path.Combine(root, "batch-output"),
            options,
            (_, _) => piperCalls++,
            (_, _) => normalizationCalls++,
            _ =>
            {
                durationCalls++;
                return 30;
            },
            (_, _) => renderingProcessCalls++,
            (_, _, _, _, _) => Task.FromResult(exhausted));

        ShortBatchPlan plan = new()
        {
            BatchId = "regression-malformed-short",
            BatchTheme = "Malformed script must stop production",
            OverallGoal = "Prove invalid narration cannot cross the TTS boundary.",
            Videos = new List<PlannedShort>
            {
                new()
                {
                    Position = 1,
                    WorkingTitle = "Malformed narration gate",
                    Topic = "Rejecting one truncated Short",
                    Hook = "This malformed response must never reach a voice model.",
                    PurposeInBatch = "Exercise the production script gate.",
                    KeyDifferenceFromOtherVideos = "This is a deterministic regression fixture.",
                    RequiredPoints = new List<string> { "Reject the malformed response." },
                    AvoidRepeating = new List<string> { "No production claims." }
                }
            }
        };

        string batchDirectory = Path.Combine(root, "regression-batch");
        int memoryBefore = (await VideoMemory.LoadAllAsync(testMode: true)).Count;
        BatchManifest manifest = await coordinator.RunRegressionTestBatchAsync(plan, batchDirectory);
        int memoryAfter = (await VideoMemory.LoadAllAsync(testMode: true)).Count;
        BatchVideoEntry entry = manifest.Videos.Single();

        Assert(entry.Status == BatchVideoStatuses.Failed,
            "Batch entry did not become failed after script retries were exhausted.");
        Assert(entry.ScriptValidation?.Success == false && entry.ScriptGenerationAttempts == 3,
            "Batch manifest did not retain failed script-validation evidence and retry count.");
        Assert(piperCalls == 0 && normalizationCalls == 0 && durationCalls == 0,
            "Piper or downstream audio handling was called for invalid narration.");
        Assert(renderingProcessCalls == 0,
            "Rendering was called for invalid narration.");
        Assert(!entry.MemorySaved && memoryAfter == memoryBefore,
            "Invalid narration was saved into test or official video memory.");
        Assert(!entry.StageHistory.Contains(BatchVideoStatuses.GeneratingVoice) &&
               !entry.StageHistory.Contains(BatchVideoStatuses.Rendering),
            "Invalid narration crossed a forbidden batch stage boundary.");
        Console.WriteLine("[ShortQualityTests] Batch side-effect and memory-protection regression passed.");
    }

    private static async Task TestOfficialMemoryDefenseAsync(
        string root,
        ShortScriptGenerationResult exhausted)
    {
        int productionCountBefore = (await VideoMemory.LoadAllAsync()).Count;
        VideoMemoryRecord? refused = await VideoMemory.SaveCompletedVideoAsync(
            $"quality-regression-{Guid.NewGuid():N}",
            "Rejected malformed Short",
            "Short script validation regression",
            Path.Combine(root, "technically-valid-too-short.mp4"),
            Path.Combine(root, "must-not-be-written.txt"),
            MalformedProductionResponse,
            validationEvidence: new ProductionValidationEvidence
            {
                ScriptValidation = exhausted.Validation,
                TtsCompleted = true,
                VoiceDurationValidation = VoiceDurationValidator.Validate(30, 30),
                RenderingCompleted = true,
                VideoValidation = new VideoValidationResult
                {
                    Success = true,
                    FullValidationPerformed = true,
                    ScriptValidationPassed = false,
                    VoiceDurationValidationPassed = true
                }
            });
        int productionCountAfter = (await VideoMemory.LoadAllAsync()).Count;

        Assert(refused is null && productionCountAfter == productionCountBefore,
            "Memory layer accepted truncated narration without complete production evidence.");
        Assert(!File.Exists(Path.Combine(root, "must-not-be-written.txt")),
            "Refused production memory unexpectedly wrote the script artifact.");
        Console.WriteLine("[ShortQualityTests] Defensive official-memory gate passed.");
    }

    private static void RunProcess(string fileName, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} failed with exit code {process.ExitCode}: {stderr}\n{stdout}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
