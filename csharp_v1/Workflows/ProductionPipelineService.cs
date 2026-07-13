using System.Diagnostics;
using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Infrastructure;
using AI_YOUTUBER.Models;
using AI_YOUTUBER.Rendering;

namespace AI_YOUTUBER.Workflows;

public sealed class ProductionPipelineService
{
    private readonly PiperVoiceService _voice;
    private readonly AvatarVideoRenderer _renderer = new();
    private readonly VideoValidationService _videoValidator = new();

    public ProductionPipelineService(Ex01Settings settings)
    {
        _voice = new PiperVoiceService(settings);
    }

    public async Task<ProductionResult> CompleteAsync(
        string videoId,
        string topic,
        int targetSeconds,
        VideoOrientation orientation,
        string outputDirectory,
        string script,
        ScriptValidationResult scriptValidation,
        string scriptModel,
        string promptVersion,
        int generationAttempts,
        TimeSpan generationElapsed,
        DateTime startedUtc)
    {
        Directory.CreateDirectory(outputDirectory);
        string scriptPath = Path.Combine(outputDirectory, "script.txt");
        string voicePath = Path.Combine(outputDirectory, "voice.wav");
        string visualPath = Path.Combine(outputDirectory, "visual.png");
        string videoPath = Path.Combine(outputDirectory, "video.mp4");
        string metricsPath = Path.Combine(outputDirectory, "run_metrics.json");
        RunMetrics metrics = new()
        {
            VideoId = videoId,
            PromptVersion = promptVersion,
            ScriptModel = scriptModel,
            TargetSeconds = targetSeconds,
            WordCount = scriptValidation.WordCount,
            ScriptGenerationAttempts = generationAttempts,
            ScriptGenerationSeconds = generationElapsed.TotalSeconds,
            ScriptHash = Functions.MEMORY.VideoMemory.CalculateHash(script),
            UserApprovedScript = true,
            StartedUtc = startedUtc
        };
        ProductionResult production = new()
        {
            OutputDirectory = outputDirectory,
            ScriptPath = scriptPath,
            VoicePath = voicePath,
            VideoPath = videoPath,
            MetricsPath = metricsPath,
            Metrics = metrics
        };

        try
        {
            await File.WriteAllTextAsync(scriptPath, script);
            await JsonFile.WriteAtomicAsync(
                Path.Combine(outputDirectory, "script_validation.json"),
                scriptValidation);

            Console.WriteLine("Generating narration with Piper...");
            Stopwatch timer = Stopwatch.StartNew();
            await _voice.GenerateAsync(script, voicePath);
            timer.Stop();
            metrics.TtsSeconds = timer.Elapsed.TotalSeconds;

            double voiceDuration = await VideoValidationService.GetDurationAsync(voicePath);
            double minimumRatio = orientation == VideoOrientation.Portrait ? 0.75 : 0.70;
            double maximumRatio = orientation == VideoOrientation.Portrait ? 1.25 : 1.30;
            VoiceDurationValidationResult voiceValidation = VoiceDurationValidator.Validate(
                voiceDuration,
                targetSeconds,
                minimumRatio,
                maximumRatio);
            production.VoiceValidation = voiceValidation;
            metrics.VoiceDurationSeconds = voiceDuration;
            await JsonFile.WriteAtomicAsync(
                Path.Combine(outputDirectory, "voice_validation.json"),
                voiceValidation);
            if (!voiceValidation.Success)
            {
                metrics.FailureStage = "voice_validation";
                Console.WriteLine($"Voice validation failed: {string.Join(" ", voiceValidation.Errors)}");
                return production;
            }

            Console.WriteLine("Generating the local EX_01 visual...");
            timer.Restart();
            await _renderer.CreateVisualAsync(topic, visualPath, orientation);
            timer.Stop();
            metrics.VisualRenderSeconds = timer.Elapsed.TotalSeconds;

            Console.WriteLine("Rendering the video with FFmpeg...");
            timer.Restart();
            await _renderer.RenderVideoAsync(visualPath, voicePath, videoPath, voiceDuration);
            timer.Stop();
            metrics.VideoRenderSeconds = timer.Elapsed.TotalSeconds;

            VideoValidationResult videoValidation = await _videoValidator.ValidateAsync(
                new VideoValidationRequest
                {
                    VideoPath = videoPath,
                    ScriptPath = scriptPath,
                    RequestedDurationSeconds = targetSeconds,
                    Orientation = orientation,
                    ScriptValidation = scriptValidation,
                    VoiceDurationValidation = voiceValidation
                });
            production.VideoValidation = videoValidation;
            metrics.VideoDurationSeconds = videoValidation.DurationSeconds;
            metrics.VideoHash = videoValidation.FileHash;
            await JsonFile.WriteAtomicAsync(
                Path.Combine(outputDirectory, "video_validation.json"),
                videoValidation);
            if (!videoValidation.Success)
            {
                metrics.FailureStage = "video_validation";
                Console.WriteLine($"Video validation failed: {string.Join(" ", videoValidation.Errors)}");
                return production;
            }

            production.Success = true;
            Console.WriteLine();
            Console.WriteLine($"Completed video: {videoPath}");
            Console.WriteLine(
                $"Validation: passed | {videoValidation.DurationSeconds:F2}s | " +
                $"{videoValidation.Width}x{videoValidation.Height} | audio and video present");
            return production;
        }
        catch (Exception exception)
        {
            metrics.FailureStage = string.IsNullOrWhiteSpace(metrics.FailureStage)
                ? "production"
                : metrics.FailureStage;
            Console.WriteLine($"Production stopped: {exception.Message}");
            return production;
        }
        finally
        {
            metrics.CompletedUtc = DateTime.UtcNow;
            try
            {
                await JsonFile.WriteAtomicAsync(metricsPath, metrics);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"Could not write run metrics: {exception.Message}");
            }
        }
    }
}
