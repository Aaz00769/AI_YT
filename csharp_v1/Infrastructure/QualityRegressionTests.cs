using System.Text.Json;
using AI_YOUTUBER.Functions.ASKING;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Infrastructure;

public static class QualityRegressionTests
{
    private const string TruncatedResponse =
        "Hey, I tried to run a 4K video on this ancient Dell and it crashed. Again. Running on an i7-";

    private const string ValidScript =
        "Anton gave me a thirty-second deadline, which is brave when the Quadro T1000 measures time in thermal warnings. " +
        "I am testing whether one focused local script can survive without inventing a benchmark, a crash, or a heroic ending. " +
        "The plan is simple: write complete sentences, let Piper speak naturally, and reject any tiny audio file pretending to be a finished Short. " +
        "If every gate agrees, the workstation earns one quiet victory. If not, nothing enters memory.";

    public static async Task RunAsync()
    {
        TestOllamaRequest();
        await TestRetriesAsync();
        ScriptValidationResult valid = TestScriptValidation();
        TestVoiceDuration();

        if (await ProcessRunner.IsAvailableAsync("ffmpeg") && await ProcessRunner.IsAvailableAsync("ffprobe"))
            await TestTargetAwareVideoValidationAsync(valid);
        else
            Console.WriteLine("Video-duration regression: UNAVAILABLE - FFmpeg or ffprobe missing");

        Console.WriteLine("Short quality regression tests: PASS");
    }

    private static void TestOllamaRequest()
    {
        int budget = AskAI.CalculateShortOutputTokenBudget(81);
        Assert(budget is >= 512 and <= 1024, "Short output-token budget is unsafe.");
        OllamaGenerateRequest request = new()
        {
            Model = "qwen3:14b",
            Prompt = "/no_think Return narration only.",
            Stream = true,
            Think = false,
            Options = new OllamaGenerateOptions { MaximumOutputTokens = budget }
        };
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        Assert(document.RootElement.GetProperty("think").ValueKind == JsonValueKind.False,
            "Ollama request is missing think:false.");
        Assert(document.RootElement.GetProperty("options").GetProperty("num_predict").GetInt32() >= 512,
            "Serialized token budget is too small.");
    }

    private static async Task TestRetriesAsync()
    {
        int calls = 0;
        ScriptGenerationResult result = await AskAI.GenerateValidatedScriptCoreAsync(
            "Write a Short.",
            66,
            81,
            3,
            (_, _) =>
            {
                calls++;
                return Task.FromResult(new OllamaGenerationResult
                {
                    Text = TruncatedResponse,
                    Completed = true,
                    DoneReason = "length",
                    OutputTokenCount = 512,
                    MaximumOutputTokens = 512,
                    ReachedOutputTokenLimit = true
                });
            });
        Assert(calls == 3 && result.AttemptCount == 3, "Invalid scripts did not exhaust three attempts.");
        Assert(!result.Validation.Success && result.Validation.AppearsTruncated,
            "Truncated narration unexpectedly passed.");
    }

    private static ScriptValidationResult TestScriptValidation()
    {
        ScriptValidationResult valid = ShortScriptValidator.Validate(
            ValidScript,
            66,
            81,
            new OllamaGenerationResult
            {
                Completed = true,
                DoneReason = "stop",
                OutputTokenCount = 120,
                MaximumOutputTokens = 512
            });
        Assert(valid.Success, "Known-valid narration did not pass.");
        Assert(!ShortScriptValidator.Validate($"<think>secret</think> {ValidScript}", 66, 90).Success,
            "Thinking tags unexpectedly passed.");
        ScriptValidationResult noEnding = ShortScriptValidator.Validate(ValidScript.TrimEnd('.'), 66, 81);
        Assert(!noEnding.Success && noEnding.AppearsTruncated,
            "Missing terminal punctuation unexpectedly passed.");
        Assert(!ShortScriptValidator.Validate("```text\nplaceholder\n```", 1, 20).Success,
            "Markdown or placeholder content unexpectedly passed.");
        return valid;
    }

    private static void TestVoiceDuration()
    {
        Assert(VoiceDurationValidator.Validate(30, 30).Success, "Matching voice duration did not pass.");
        Assert(!VoiceDurationValidator.Validate(5.85, 30).Success,
            "A 5.85-second voice passed a 30-second target.");
    }

    private static async Task TestTargetAwareVideoValidationAsync(ScriptValidationResult scriptValidation)
    {
        string root = Path.Combine(Path.GetTempPath(), $"ex01-quality-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string scriptPath = Path.Combine(root, "script.txt");
            string videoPath = Path.Combine(root, "too-short.mp4");
            await File.WriteAllTextAsync(scriptPath, ValidScript);
            await ProcessRunner.EnsureSuccessAsync(
                "ffmpeg",
                new[]
                {
                    "-y", "-f", "lavfi", "-i", "color=c=0x102a52:s=360x640:r=12:d=5.85",
                    "-f", "lavfi", "-i", "sine=frequency=440:duration=5.85", "-t", "5.85",
                    "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", videoPath
                },
                timeout: TimeSpan.FromMinutes(2));
            VideoValidationResult validation = await new VideoValidationService().ValidateAsync(
                new VideoValidationRequest
                {
                    VideoPath = videoPath,
                    ScriptPath = scriptPath,
                    RequestedDurationSeconds = 30,
                    MinimumFileSizeBytes = 100,
                    Orientation = VideoOrientation.Portrait,
                    ScriptValidation = scriptValidation,
                    VoiceDurationValidation = VoiceDurationValidator.Validate(5.85, 30)
                });
            Assert(validation.FullValidationPerformed && validation.HasVideo && validation.HasAudio,
                "Synthetic media could not be probed.");
            Assert(!validation.Success && validation.DurationSeconds < 22.5,
                "Technically valid but far-too-short media unexpectedly passed.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
