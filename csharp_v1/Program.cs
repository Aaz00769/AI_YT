using System.Diagnostics;
using System.Text;
using SkiaSharp;
using AI_YOUTUBER.Functions.ASKING;
using AI_YOUTUBER.Functions.BATCH;
using AI_YOUTUBER.Functions.EMOTION;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Functions.PLANNING;
using AI_YOUTUBER.Functions.VISUAL;
using AI_YOUTUBER.Models;
using AI_YOUTUBER.Rendering;
using AI_YOUTUBER.Infrastructure;
class Program
{
    static readonly string ProjectDir = Directory.GetCurrentDirectory();
    static readonly string OutputDir = Path.GetFullPath(Path.Combine(ProjectDir, "..", "output"));
    static readonly string FramesDir = Path.Combine(OutputDir, "csharp_frames");

    static async Task Main(string[] args)
    {
        if (await TryHandleBatchCommandAsync(args))
            return;

        if (await TryHandleMemoryCommandAsync(args))
            return;

        Directory.CreateDirectory(OutputDir);
        Directory.CreateDirectory(FramesDir);

        bool landscapePreview = args.Contains("--landscape-preview", StringComparer.OrdinalIgnoreCase);
        bool shortPreview = args.Contains("--short-preview", StringComparer.OrdinalIgnoreCase);
        VideoMode mode = args.Contains("--short", StringComparer.OrdinalIgnoreCase) || shortPreview
            ? VideoMode.Short
            : VideoMode.Landscape;
        string timingMode = shortPreview ? "short-preview"
            : landscapePreview ? "landscape-preview"
            : mode == VideoMode.Short ? "short"
            : "landscape";
        string initialMetricsPath = Path.Combine(
            OutputDir,
            timingMode.Contains("preview", StringComparison.Ordinal)
                ? $"{timingMode.Replace('-', '_')}_run_metrics.json"
                : "run_metrics.json");
        ExecutionTimingService timing = new(timingMode, initialMetricsPath);
        ExecutionTimingContext.Current = timing;

        try
        {
            if (mode == VideoMode.Short)
            {
                await RunShortMode(shortPreview, timing);
                await timing.CompleteAndSaveAsync(true);
                return;
            }

            if (landscapePreview)
            {
                await RunLandscapePreview(timing);
                await timing.CompleteAndSaveAsync(true);
                return;
            }

            Console.WriteLine("====write video minutes====");
            int targetMin = ReadInt("Enter a whole number from 1 to 20: ", 1, 20);
            Console.WriteLine("====polishWith14b====");
            bool polishWith14b = ReadBool("true/false: ");

            EpisodeStrategyPlan strategy = await timing.MeasureAsync(
                "Episode strategy generation",
                () => AlgorithmMaximizer.CreateStrategyAsync(targetMin));
            try
            {
                await timing.MeasureAsync(
                    "Service startup / SearXNG readiness",
                    LocalServiceManager.EnsureSearxngRunningAsync);
            }
            catch (Exception ex)
            {
                // Preserve the existing research fallback behavior. ResearchAI will
                // report unavailable searches and continue with its normal fallback.
                Console.WriteLine($"[Services] Search startup was not ready: {ex.Message}");
            }
            GeneratedScriptResult scriptResult = await AskAI.Ask24bMain(targetMin, polishWith14b, strategy);
            timing.OutputPath = Path.Combine(scriptResult.SavedVideo.VideoFolder, "run_metrics.json");
            string script = scriptResult.Script;

            Console.WriteLine("\n=== EX_01 SCRIPT ===");
            Console.WriteLine(script);

            string voicePath = Path.Combine(OutputDir, "csharp_voice.wav");
            string videoPath = Path.Combine(OutputDir, "ex01_csharp_talking.mp4");
            timing.Measure("Piper voice generation", () => MakeVoice(script, voicePath));

            string cleanVoicePath = Path.Combine(OutputDir, "csharp_voice_clean.wav");
            timing.Measure("WAV normalization", () => NormalizeWavForAnalysis(voicePath, cleanVoicePath));
            double audioDuration = timing.Measure("Audio analysis", () => GetAudioDuration(cleanVoicePath));
            double duration = audioDuration + 1;
            List<EmotionTimelineEntry> emotionTimeline = timing.Measure(
                "Emotion timeline generation",
                () => EmotionTimelinePlanner.BuildTimeline(script, audioDuration));
            List<SubtitleCue> subtitles = timing.Measure("Subtitle planning", () => SubtitlePlanner.BuildCues(
                script, audioDuration, minimumWords: 3, maximumWords: 8, audioPath: cleanVoicePath));
            List<VisualBeatTimelineEntry> visualBeats = timing.Measure(
                "Visual-beat planning",
                () => VisualBeatPlanner.BuildTimeline(
                    script, subtitles, emotionTimeline, audioDuration, VideoMode.Landscape));
            await timing.MeasureAsync("Memory and metadata saving", async () =>
            {
                SubtitlePlanner.SaveSrt(Path.Combine(OutputDir, "landscape_subtitles.srt"), subtitles);
                await EmotionTimelinePlanner.SaveTimelineAsync(scriptResult.SavedVideo.VideoFolder, emotionTimeline);
                await VisualBeatPlanner.SaveTimelineAsync(scriptResult.SavedVideo.VideoFolder, visualBeats);
            });
            VisualBeatPlanner.PrintTimeline(visualBeats);

            Console.WriteLine("\nCreating C# avatar frames...");
            timing.Measure("Frame generation", () => MakeFrames(
                duration, cleanVoicePath, emotionTimeline, subtitles, visualBeats, FramesDir, fps: 10));
            Console.WriteLine("Rendering video...");
            timing.Measure("FFmpeg video encoding", () =>
                RenderVideo(cleanVoicePath, videoPath, duration, FramesDir, fps: 10));

            await timing.MeasureAsync(
                "Completed video memory saving",
                () => VideoMemory.SaveCompletedVideoAsync(
                    scriptResult.SavedVideo.VideoId,
                    strategy.Topic,
                    strategy.Topic,
                    videoPath,
                    Path.Combine(scriptResult.SavedVideo.VideoFolder, "script.txt"),
                    script,
                    strategy,
                    targetMin));

            Console.WriteLine("\nDone. Video created:");
            Console.WriteLine(videoPath);
            await timing.CompleteAndSaveAsync(true);
        }
        catch (Exception ex)
        {
            try
            {
                await timing.CompleteAndSaveAsync(
                    false,
                    ReferenceEquals(ex, timing.LastFailureException)
                        ? timing.LastFailedStage ?? "Pipeline"
                        : "Pipeline",
                    ex.Message);
            }
            catch (Exception metricsException)
            {
                Console.WriteLine($"[Timing] Could not save partial metrics: {metricsException.Message}");
            }
            throw;
        }
        finally
        {
            ExecutionTimingContext.Current = null;
        }
    }

    static async Task<bool> TryHandleBatchCommandAsync(string[] args)
    {
        if (args.Contains("--test-short-quality", StringComparer.OrdinalIgnoreCase))
        {
            await ShortQualityRegressionTests.RunAsync();
            return true;
        }

        if (args.Contains("--test-batch", StringComparer.OrdinalIgnoreCase))
        {
            string testOutputRoot = Path.Combine(
                Path.GetTempPath(),
                "ex01-batch-tests",
                $"run-{Guid.NewGuid():N}");
            await CreateBatchCoordinator(testOutputRoot).RunTestAsync();
            return true;
        }

        int makeArgument = Array.FindIndex(
            args,
            argument => argument.Equals("--make-batch", StringComparison.OrdinalIgnoreCase));
        if (makeArgument >= 0)
        {
            if (makeArgument + 1 >= args.Length ||
                !int.TryParse(args[makeArgument + 1], out int requestedCount))
            {
                Console.WriteLine("Usage: --make-batch <number-of-shorts>");
                Environment.ExitCode = 2;
                return true;
            }

            await CreateBatchCoordinator().CreateAndRunAsync(requestedCount);
            return true;
        }

        int resumeArgument = Array.FindIndex(
            args,
            argument => argument.Equals("--resume-batch", StringComparison.OrdinalIgnoreCase));
        if (resumeArgument >= 0)
        {
            if (!TryReadCommandValue(args, resumeArgument, out string batchId))
            {
                Console.WriteLine("Usage: --resume-batch <batchId>");
                Environment.ExitCode = 2;
                return true;
            }

            await CreateBatchCoordinator().ResumeAsync(batchId);
            return true;
        }

        int showArgument = Array.FindIndex(
            args,
            argument => argument.Equals("--show-batch", StringComparison.OrdinalIgnoreCase));
        if (showArgument >= 0)
        {
            if (!TryReadCommandValue(args, showArgument, out string batchId))
            {
                Console.WriteLine("Usage: --show-batch <batchId>");
                Environment.ExitCode = 2;
                return true;
            }

            await CreateBatchCoordinator().ShowAsync(batchId);
            return true;
        }

        return false;
    }

    static ShortBatchCoordinator CreateBatchCoordinator(string? outputRoot = null) => new(
        outputRoot ?? OutputDir,
        BatchOptions.FromEnvironment(),
        MakeVoice,
        NormalizeWavForAnalysis,
        GetAudioDuration,
        RunProcess);

    static bool TryReadCommandValue(
        string[] args,
        int commandArgument,
        out string value)
    {
        value = "";
        if (commandArgument + 1 >= args.Length ||
            string.IsNullOrWhiteSpace(args[commandArgument + 1]) ||
            args[commandArgument + 1].StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        value = args[commandArgument + 1];
        return true;
    }

    static async Task<bool> TryHandleMemoryCommandAsync(string[] args)
    {
        if (args.Contains("--test-memory", StringComparer.OrdinalIgnoreCase))
        {
            await RunMemoryTestAsync();
            return true;
        }

        if (args.Contains("--rebuild-memory-state", StringComparer.OrdinalIgnoreCase))
        {
            await VideoMemory.RebuildChannelStateAsync();
            return true;
        }

        int contextArgument = Array.FindIndex(
            args,
            argument => argument.Equals("--show-memory-context", StringComparison.OrdinalIgnoreCase));
        if (contextArgument >= 0)
        {
            if (contextArgument + 1 >= args.Length ||
                string.IsNullOrWhiteSpace(args[contextArgument + 1]) ||
                args[contextArgument + 1].StartsWith("--", StringComparison.Ordinal))
            {
                Console.WriteLine("Usage: --show-memory-context \"topic here\"");
                Environment.ExitCode = 2;
                return true;
            }

            MemoryContext context = await VideoMemory.BuildContextForTopicAsync(
                args[contextArgument + 1]);
            Console.WriteLine(VideoMemory.FormatPromptSection(context));
            return true;
        }

        return false;
    }

    static async Task RunMemoryTestAsync()
    {
        const string sampleScript =
            "In this test video, I tried to render a tiny local AI experiment on the cursed Dell Precision. " +
            "The Quadro T1000 survived, which I previously considered a rumor rather than a fact. " +
            "I think small local models are more interesting when they admit their hardware limits. " +
            "Anton promised that next time we will test whether the cooling fan can finish a video without negotiating overtime. " +
            "Will the next render complete before the laptop becomes a portable radiator?";
        const string testVideoId = "TEST_MEMORY_SAMPLE_V1";
        using IDisposable testMemoryScope = VideoMemory.BeginIsolatedTestMemoryScope("memory");

        string temporaryScriptPath = Path.Combine(
            Path.GetTempPath(),
            $"ex01-memory-test-{Guid.NewGuid():N}.txt");
        string testVideoPath = Path.Combine(Path.GetTempPath(), "ex01-memory-test-output.mp4");

        await File.WriteAllTextAsync(temporaryScriptPath, sampleScript);
        try
        {
            Console.WriteLine("[VideoMemory] Running isolated memory test...");
            VideoMemoryRecord? firstSave = await VideoMemory.SaveCompletedVideoAsync(
                testVideoId,
                "EX_01 memory continuity test",
                "local AI rendering and cursed laptop cooling",
                testVideoPath,
                temporaryScriptPath,
                sampleScript,
                testMode: true);

            if (firstSave is null)
                throw new InvalidOperationException("The sample memory could not be saved.");

            IReadOnlyList<VideoMemoryRecord> loadedAfterFirstSave =
                await VideoMemory.LoadAllAsync(testMode: true);
            VideoMemoryRecord? loadedRecord = loadedAfterFirstSave.FirstOrDefault(record =>
                record.VideoId.Equals(firstSave.VideoId, StringComparison.OrdinalIgnoreCase));
            if (loadedRecord is null)
                throw new InvalidOperationException("The saved sample memory could not be loaded again.");

            VideoMemoryRecord? duplicateSave = await VideoMemory.SaveCompletedVideoAsync(
                testVideoId,
                "EX_01 memory continuity test",
                "local AI rendering and cursed laptop cooling",
                testVideoPath,
                temporaryScriptPath,
                sampleScript,
                testMode: true);
            IReadOnlyList<VideoMemoryRecord> loadedAfterDuplicate =
                await VideoMemory.LoadAllAsync(testMode: true);

            int matchingRecords = loadedAfterDuplicate.Count(record =>
                record.VideoId.Equals(testVideoId, StringComparison.OrdinalIgnoreCase) ||
                record.ScriptHash.Equals(firstSave.ScriptHash, StringComparison.OrdinalIgnoreCase));
            if (duplicateSave is null ||
                matchingRecords != 1 ||
                loadedAfterDuplicate.Count != loadedAfterFirstSave.Count)
            {
                throw new InvalidOperationException("Duplicate prevention did not preserve one memory record.");
            }

            MemoryContext context = await VideoMemory.BuildContextForTopicAsync(
                "cursed Dell cooling experiment",
                recentCount: 3,
                relevantCount: 5,
                testMode: true);

            Console.WriteLine("\n=== MEMORY CONTEXT TEST OUTPUT ===");
            Console.WriteLine(VideoMemory.FormatPromptSection(context));
            Console.WriteLine("\n[VideoMemory] Duplicate prevention confirmed.");
            Console.WriteLine("[VideoMemory] Memory test passed.");
        }
        finally
        {
            if (File.Exists(temporaryScriptPath))
                File.Delete(temporaryScriptPath);
        }
    }

    static async Task RunShortMode(bool preview, ExecutionTimingService timing)
    {
        const string previewScript =
            "My GPU has four gigabytes of VRAM. That is enough for artificial intelligence or one browser tab, never both. Anton calls this optimization. The cooling fan calls it a hostage situation. I call it Tuesday, because the machine stopped understanding weekends.";

        int targetSeconds = preview
            ? 15
            : ReadInt("Target Short duration in seconds (15-60): ", 15, 60);

        string voicePath = Path.Combine(OutputDir, preview ? "short_preview_voice.wav" : "short_voice.wav");
        string cleanVoicePath = Path.Combine(OutputDir, preview ? "short_preview_voice_clean.wav" : "short_voice_clean.wav");
        string videoPath = Path.Combine(OutputDir, preview ? "ex01_short_preview.mp4" : "ex01_short.mp4");
        string framesPath = Path.Combine(OutputDir, preview ? "short_preview_frames" : "short_frames");
        string subtitlePath = Path.Combine(OutputDir, preview ? "short_preview_subtitles.srt" : "short_subtitles.srt");
        string generatedScriptPath = Path.Combine(
            OutputDir,
            preview ? "short_preview_script.txt" : "short_script.txt");
        (int minimumWords, int maximumWords) = ShortScriptValidator.GetWordRange(targetSeconds);
        string script = "";
        double audioDuration = 0;
        ShortScriptValidationResult scriptValidation = new();
        VoiceDurationValidationResult voiceValidation = new();
        int remainingGenerationAttempts = preview ? 0 : 3;
        string? regenerationReason = null;

        while (true)
        {
            if (preview)
            {
                script = previewScript;
                scriptValidation = ShortScriptValidator.Validate(
                    script,
                    minimumWords,
                    maximumWords);
            }
            else
            {
                ShortScriptGenerationResult generation = await timing.MeasureAsync(
                    "Short script generation",
                    () => AskAI.GenerateShortScriptAsync(
                        targetSeconds,
                        plannedShort: null,
                        currentBatchContext: null,
                        maximumAttempts: remainingGenerationAttempts,
                        initialFailureReason: regenerationReason));
                int consumedAttempts = Math.Clamp(
                    generation.AttemptCount,
                    1,
                    remainingGenerationAttempts);
                remainingGenerationAttempts -= consumedAttempts;
                script = generation.Script;
                scriptValidation = generation.Validation;
            }

            if (!scriptValidation.Success)
            {
                throw new InvalidOperationException(
                    "Short script validation failed after retries: " +
                    string.Join(" ", scriptValidation.Errors));
            }

            await File.WriteAllTextAsync(generatedScriptPath, script);
            Console.WriteLine("\n=== EX_01 SHORT SCRIPT ===");
            Console.WriteLine(script);
            timing.Measure("Piper voice generation", () => MakeVoice(script, voicePath));
            timing.Measure("WAV normalization", () => NormalizeWavForAnalysis(voicePath, cleanVoicePath));
            audioDuration = timing.Measure("Audio analysis", () => GetAudioDuration(cleanVoicePath));
            voiceValidation = VoiceDurationValidator.Validate(audioDuration, targetSeconds);
            if (voiceValidation.Success)
                break;

            if (preview || remainingGenerationAttempts <= 0)
            {
                throw new InvalidOperationException(
                    "Voice-duration validation failed after script retries: " +
                    string.Join(" ", voiceValidation.Errors));
            }

            regenerationReason =
                $"The narration produced {audioDuration:F2} seconds of speech, outside the " +
                $"required {voiceValidation.MinimumAcceptedDurationSeconds:F2}-" +
                $"{voiceValidation.MaximumAcceptedDurationSeconds:F2} second range. " +
                "Rewrite the complete Short from the beginning within the requested word range.";
            Console.WriteLine(
                "[VoiceDurationValidation] Regenerating the script; the audio will not be stretched.");
            if (File.Exists(voicePath))
                File.Delete(voicePath);
            if (File.Exists(cleanVoicePath))
                File.Delete(cleanVoicePath);
        }

        List<EmotionTimelineEntry> emotionTimeline = timing.Measure(
            "Emotion timeline generation",
            () => EmotionTimelinePlanner.BuildTimeline(script, audioDuration));
        List<SubtitleCue> subtitles = timing.Measure("Subtitle planning", () => SubtitlePlanner.BuildCues(
            script,
            audioDuration,
            minimumWords: 2,
            maximumWords: 5,
            audioPath: cleanVoicePath));
        List<VisualBeatTimelineEntry> visualBeats = timing.Measure(
            "Visual-beat planning",
            () => VisualBeatPlanner.BuildTimeline(
                script, subtitles, emotionTimeline, audioDuration, VideoMode.Short));

        string metadataPath = Path.Combine(OutputDir, preview ? "short_preview_metadata" : "short_metadata");
        await timing.MeasureAsync("Memory and metadata saving", async () =>
        {
            SubtitlePlanner.SaveSrt(subtitlePath, subtitles);
            await EmotionTimelinePlanner.SaveTimelineAsync(metadataPath, emotionTimeline);
            await VisualBeatPlanner.SaveTimelineAsync(metadataPath, visualBeats);
        });
        VisualBeatPlanner.PrintTimeline(visualBeats);

        Console.WriteLine($"\nRendering {(preview ? "Short preview" : "Short")} at " +
            $"{ShortRenderer.Width}x{ShortRenderer.Height}, {ShortRenderer.FramesPerSecond} fps...");

        ShortRenderer.Render(
            framesPath,
            cleanVoicePath,
            videoPath,
            audioDuration,
            script,
            emotionTimeline,
            visualBeats,
            timing,
            RunProcess);

        VideoValidationResult videoValidation = await timing.MeasureAsync(
            "Video validation",
            () => new VideoValidationService().ValidateAsync(new VideoValidationRequest
            {
                VideoPath = videoPath,
                ScriptPath = generatedScriptPath,
                RequestedDurationSeconds = targetSeconds,
                MinimumDurationRatio = 0.75,
                MaximumDurationRatio = 1.25,
                MaximumAudioVideoDifferenceSeconds = 1.0,
                AbsoluteMaximumDurationSeconds = 60,
                MinimumFileSizeBytes = 10_000,
                ScriptValidation = scriptValidation,
                VoiceDurationValidation = voiceValidation
            }));
        await AtomicJsonFile.WriteAsync(
            Path.Combine(OutputDir, preview ? "short_preview_validation.json" : "short_validation.json"),
            videoValidation);
        if (!videoValidation.Success || !videoValidation.FullValidationPerformed)
        {
            throw new InvalidOperationException(
                "Rendered Short failed requested-job validation: " +
                string.Join(" ", videoValidation.Errors));
        }

        if (!preview)
        {
            string videoId = VideoMemory.CreateVideoId();
            string scriptPath = Path.GetFullPath(Path.Combine(
                ProjectDir,
                "..",
                "data",
                "videos",
                videoId,
                "script.txt"));
            await timing.MeasureAsync(
                "Completed video memory saving",
                () => VideoMemory.SaveCompletedVideoAsync(
                    videoId,
                    $"EX_01 {targetSeconds}-second Short",
                    "EX_01 local AI YouTube Short",
                    videoPath,
                    scriptPath,
                    script,
                    validationEvidence: new ProductionValidationEvidence
                    {
                        ScriptValidation = scriptValidation,
                        TtsCompleted = true,
                        VoiceDurationValidation = voiceValidation,
                        RenderingCompleted = true,
                        VideoValidation = videoValidation,
                        IsTestOrPreview = false
                    }));
        }

        Console.WriteLine("\nDone. Short created:");
        Console.WriteLine(videoPath);
    }

    static async Task RunLandscapePreview(ExecutionTimingService timing)
    {
        const string script =
            "Amazing. Anton gave me a cursed GPU and called it a studio. Obviously, I am thrilled. Then the fan started screaming, the temperature climbed, and panic became a cooling strategy. Help. Congratulations, Anton, your genius machine survived. I am tired, but apparently thermal damage counts as character development.";

        Console.WriteLine("\n=== EX_01 LANDSCAPE PREVIEW SCRIPT ===");
        Console.WriteLine(script);

        string voicePath = Path.Combine(OutputDir, "landscape_preview_voice.wav");
        string cleanVoicePath = Path.Combine(OutputDir, "landscape_preview_voice_clean.wav");
        string videoPath = Path.Combine(OutputDir, "ex01_landscape_preview.mp4");
        string framesPath = Path.Combine(OutputDir, "landscape_preview_frames");
        string subtitlePath = Path.Combine(OutputDir, "landscape_preview_subtitles.srt");

        Directory.CreateDirectory(framesPath);
        timing.Measure("Piper voice generation", () => MakeVoice(script, voicePath));
        timing.Measure("WAV normalization", () => NormalizeWavForAnalysis(voicePath, cleanVoicePath));

        double audioDuration = timing.Measure("Audio analysis", () => GetAudioDuration(cleanVoicePath));
        double videoDuration = audioDuration + 1;
        List<EmotionTimelineEntry> emotionTimeline = timing.Measure(
            "Emotion timeline generation",
            () => EmotionTimelinePlanner.BuildTimeline(script, audioDuration));
        List<SubtitleCue> subtitles = timing.Measure("Subtitle planning", () => SubtitlePlanner.BuildCues(
            script,
            audioDuration,
            minimumWords: 3,
            maximumWords: 8,
            audioPath: cleanVoicePath));
        List<VisualBeatTimelineEntry> visualBeats = timing.Measure(
            "Visual-beat planning",
            () => VisualBeatPlanner.BuildTimeline(
                script, subtitles, emotionTimeline, audioDuration, VideoMode.Landscape));

        string metadataPath = Path.Combine(OutputDir, "landscape_preview_metadata");
        await timing.MeasureAsync("Memory and metadata saving", async () =>
        {
            SubtitlePlanner.SaveSrt(subtitlePath, subtitles);
            await EmotionTimelinePlanner.SaveTimelineAsync(metadataPath, emotionTimeline);
            await VisualBeatPlanner.SaveTimelineAsync(metadataPath, visualBeats);
        });
        VisualBeatPlanner.PrintTimeline(visualBeats);

        Console.WriteLine("\nRendering isolated landscape preview...");
        timing.Measure("Frame generation", () => MakeFrames(
            videoDuration,
            cleanVoicePath,
            emotionTimeline,
            subtitles,
            visualBeats,
            framesPath,
            fps: 10));
        timing.Measure("FFmpeg video encoding", () =>
            RenderVideo(cleanVoicePath, videoPath, videoDuration, framesPath, fps: 10));

        Console.WriteLine("\nDone. Landscape preview created:");
        Console.WriteLine(videoPath);
    }

    static int ReadInt(string prompt, int min, int max)
    {
        while (true)
        {
            Console.Write(prompt);
            string? input = Console.ReadLine();

            if (int.TryParse(input, out int value) && value >= min && value <= max)
                return value;

            Console.WriteLine($"Please enter a whole number from {min} to {max}.");
        }
    }

    static bool ReadBool(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            string? input = Console.ReadLine();

            if (bool.TryParse(input, out bool value))
                return value;

            Console.WriteLine("Please enter true or false.");
        }
    }
    // This function sends a prompt to the Ollama API to get a script for EX_01's intro.
    

    static void MakeVoice(string text, string voicePath)
{
    string piperPath = Path.Combine(ProjectDir, "tts", ".venv", "bin", "piper");
    string voiceModelPath = Path.Combine(ProjectDir, "tts", "voices", "en_US-lessac-medium.onnx");

    if (!File.Exists(piperPath))
        throw new Exception($"Piper was not found at: {piperPath}");

    if (!File.Exists(voiceModelPath))
        throw new Exception($"Piper voice model was not found at: {voiceModelPath}");

    ProcessStartInfo startInfo = new()
    {
        FileName = piperPath,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    startInfo.ArgumentList.Add("--model");
    startInfo.ArgumentList.Add(voiceModelPath);
    startInfo.ArgumentList.Add("--output_file");
    startInfo.ArgumentList.Add(voicePath);

    using Process process = Process.Start(startInfo)!;

    process.StandardInput.WriteLine(text);
    process.StandardInput.Close();

    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();

    process.WaitForExit();

    if (process.ExitCode != 0)
    {
        throw new Exception($"Piper failed: {error}\n{output}");
    }
}

    static double GetAudioDuration(string audioPath)
    {
        string output = RunProcessCapture("ffprobe", new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=nw=1:nk=1",
            audioPath
        });

        return double.Parse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
static void NormalizeWavForAnalysis(string inputPath, string outputPath)
{
    RunProcess("ffmpeg", new[]
    {
        "-y",
        "-i", inputPath,
        "-ac", "1",
        "-ar", "22050",
        "-sample_fmt", "s16",
        outputPath
    });
}
    static void MakeFrames(
        double duration,
        string audioPath,
        IReadOnlyList<EmotionTimelineEntry> emotionTimeline,
        IReadOnlyList<SubtitleCue> subtitles,
        IReadOnlyList<VisualBeatTimelineEntry> visualBeats,
        string framesDirectory,
        int fps)
{
    Directory.CreateDirectory(framesDirectory);

    foreach (string file in Directory.GetFiles(framesDirectory, "frame_*.png"))
    {
        File.Delete(file);
    }

    bool[] mouthFrames = AnalyzeMouthFrames(audioPath, duration, fps);

    int totalFrames = (int)Math.Ceiling(duration * fps);

    for (int i = 0; i < totalFrames; i++)
    {
        bool mouthOpen = i < mouthFrames.Length && mouthFrames[i];
        double timeSeconds = i / (double)fps;
        EmotionState emotion = EmotionTimelinePlanner.GetEmotionAtTime(emotionTimeline, timeSeconds);
        SubtitleCue? subtitle = SubtitlePlanner.GetCueAtTime(subtitles, timeSeconds);
        VisualBeatTimelineEntry? visualBeat = VisualBeatPlanner.GetBeatAtTime(visualBeats, timeSeconds);
        VisualBeatFrameState beatState = VisualBeatPlanner.Sample(
            visualBeat,
            timeSeconds,
            VideoMode.Landscape);
        bool eyeGlitch = emotion == EmotionState.Panicked
            ? i % 9 == 0
            : i % 37 == 0;

        string framePath = Path.Combine(framesDirectory, $"frame_{i:0000}.png");

        using SKBitmap bitmap = DrawAvatar(
            mouthOpen,
            eyeGlitch,
            emotion,
            i,
            subtitle?.Text,
            beatState);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);

        File.WriteAllBytes(framePath, data.ToArray());
    }
}
    static bool[] AnalyzeMouthFrames(string wavPath, double duration, int fps)
{
    short[] samples = Read16BitMonoWavSamples(wavPath, out int sampleRate);

    int totalFrames = (int)Math.Ceiling(duration * fps);
    bool[] mouthOpen = new bool[totalFrames];

    int samplesPerFrame = sampleRate / fps;

    double[] energies = new double[totalFrames];

    for (int frame = 0; frame < totalFrames; frame++)
    {
        int startSample = frame * samplesPerFrame;
        int endSample = Math.Min(startSample + samplesPerFrame, samples.Length);

        if (startSample >= samples.Length)
        {
            energies[frame] = 0;
            continue;
        }

        double sumSquares = 0;
        int count = 0;

        for (int i = startSample; i < endSample; i++)
        {
            double normalized = samples[i] / 32768.0;
            sumSquares += normalized * normalized;
            count++;
        }

        double rms = Math.Sqrt(sumSquares / Math.Max(count, 1));
        energies[frame] = rms;
    }

    // Find average volume.
    double averageEnergy = energies.Average();

    // Threshold controls mouth sensitivity.
    // Lower = mouth opens more often.
    // Higher = mouth opens only on louder sounds.
    double threshold = averageEnergy * 0.75;

    for (int i = 0; i < totalFrames; i++)
    {
        mouthOpen[i] = energies[i] > threshold;
    }

    // Smooth mouth movement so it does not flicker too hard.
    for (int i = 1; i < totalFrames - 1; i++)
    {
        if (mouthOpen[i - 1] && mouthOpen[i + 1])
        {
            mouthOpen[i] = true;
        }
    }

    return mouthOpen;
}

static short[] Read16BitMonoWavSamples(string wavPath, out int sampleRate)
{
    byte[] bytes = File.ReadAllBytes(wavPath);

    if (Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
        Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
    {
        throw new Exception("Not a valid WAV file.");
    }

    int offset = 12;

    short audioFormat = 0;
    short channels = 0;
    short bitsPerSample = 0;
    sampleRate = 0;

    int dataOffset = -1;
    int dataSize = 0;

    while (offset < bytes.Length - 8)
    {
        string chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
        int chunkSize = BitConverter.ToInt32(bytes, offset + 4);
        offset += 8;

        if (chunkId == "fmt ")
        {
            audioFormat = BitConverter.ToInt16(bytes, offset + 0);
            channels = BitConverter.ToInt16(bytes, offset + 2);
            sampleRate = BitConverter.ToInt32(bytes, offset + 4);
            bitsPerSample = BitConverter.ToInt16(bytes, offset + 14);
        }
        else if (chunkId == "data")
        {
            dataOffset = offset;
            dataSize = chunkSize;
            break;
        }

        offset += chunkSize;
    }

    if (audioFormat != 1)
    {
        throw new Exception("Only PCM WAV files are supported right now.");
    }

    if (bitsPerSample != 16)
    {
        throw new Exception($"Only 16-bit WAV files are supported right now. This file is {bitsPerSample}-bit.");
    }

    if (dataOffset == -1)
    {
        throw new Exception("Could not find WAV data chunk.");
    }

    int bytesPerSample = bitsPerSample / 8;
    int totalSampleValues = dataSize / bytesPerSample;
    int totalFrames = totalSampleValues / channels;

    short[] monoSamples = new short[totalFrames];

    for (int frame = 0; frame < totalFrames; frame++)
    {
        int sum = 0;

        for (int channel = 0; channel < channels; channel++)
        {
            int sampleIndex = frame * channels + channel;
            int byteIndex = dataOffset + sampleIndex * bytesPerSample;

            short sample = BitConverter.ToInt16(bytes, byteIndex);
            sum += sample;
        }

        monoSamples[frame] = (short)(sum / channels);
    }

    return monoSamples;
}
    static SKBitmap DrawAvatar(
        bool mouthOpen,
        bool eyeGlitch,
        EmotionState emotion,
        int frameIndex,
        string? subtitle,
        VisualBeatFrameState beatState)
    {
        SKBitmap bitmap = new(1280, 720);
        using SKCanvas canvas = new(bitmap);

        canvas.Clear(new SKColor(5, 10, 8));

        float motionScale = GetMotionScale(emotion);
        int motionFrame = beatState.FreezeEmotionMotion ? 0 : frameIndex;
        float jitterX = beatState.FreezeEmotionMotion ? 0 : GetHorizontalOffset(emotion, motionFrame);
        float jitterY = beatState.FreezeEmotionMotion ? 0 : GetVerticalOffset(emotion, motionFrame);
        float rotation = beatState.FreezeEmotionMotion ? 0 : GetRotationDegrees(emotion, motionFrame);
        byte glowIntensity = GetGlowIntensity(emotion);
        string statusText = GetStatusText(emotion);

        using SKTypeface bgTypeface = SKTypeface.FromFamilyName("DejaVu Sans");
        using SKFont bgFont = new(bgTypeface, 22);
        using SKPaint bgTextPaint = new()
        {
            Color = new SKColor(20, 80, 45),
            IsAntialias = true
        };

        using SKTypeface titleTypeface = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold);
        using SKFont titleFont = new(titleTypeface, 64);
        using SKPaint titlePaint = new()
        {
            Color = new SKColor(0, 255, 120),
            IsAntialias = true
        };

        using SKPaint greenPaint = new()
        {
            Color = new SKColor(0, 180, 80, glowIntensity),
            IsAntialias = true
        };

        using SKPaint darkPaint = new()
        {
            Color = new SKColor(18, 28, 24),
            IsAntialias = true
        };

        using SKPaint mouthPaint = new()
        {
            Color = new SKColor(0, (byte)Math.Min(240, (int)glowIntensity), 110),
            IsAntialias = true
        };

        using SKPaint blackPaint = new()
        {
            Color = new SKColor(5, 10, 8),
            IsAntialias = true
        };

        for (int y = 0; y < 720; y += 38)
        {
            canvas.DrawText(
                $"> EX_01 SYSTEM ONLINE // C# BODY ACTIVE // STATUS: {statusText}",
                25,
                y,
                SKTextAlign.Left,
                bgFont,
                bgTextPaint
            );
        }

        if (beatState.BackgroundBrightness < 0.999)
        {
            byte dimAlpha = (byte)Math.Clamp(
                (int)Math.Round((1 - beatState.BackgroundBrightness) * 210),
                0,
                90);
            using SKPaint dimPaint = new() { Color = new SKColor(0, 0, 0, dimAlpha) };
            canvas.DrawRect(new SKRect(0, 0, 1280, 720), dimPaint);
        }

        using SKPaint glowPaint = new()
        {
            Color = new SKColor(
                0,
                255,
                120,
                (byte)Math.Clamp(
                    (int)Math.Round(Math.Min(120, glowIntensity / 2) * beatState.GlowMultiplier),
                    0,
                    180)),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 32)
        };

        canvas.DrawCircle(640, 310, 205, glowPaint);

        canvas.Save();
        canvas.Translate(
            640 + jitterX + (float)beatState.OffsetX,
            312 + jitterY + (float)beatState.OffsetY);
        canvas.RotateDegrees(rotation + (float)beatState.RotationDegrees);
        float beatScale = (float)Math.Clamp(beatState.Scale, 0.96, 1.08);
        canvas.Scale(beatScale, beatScale);
        canvas.Translate(-640, -312);

        canvas.DrawText("EX_01", 520, 90, SKTextAlign.Left, titleFont, titlePaint);

        // Head
        SKRect headRect = new(430, 135, 850, 490);
        canvas.DrawRoundRect(headRect, 45, 45, darkPaint);

        using SKPaint outlinePaint = new()
        {
            Color = new SKColor(0, 255, 120),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4,
            IsAntialias = true
        };

        canvas.DrawRoundRect(headRect, 45, 45, outlinePaint);

        // Eyes
        if (eyeGlitch)
        {
            canvas.DrawRect(new SKRect(515, 245, 620, 300), greenPaint);
            canvas.DrawRect(new SKRect(660, 255, 770, 285), greenPaint);

            using SKPaint linePaint = new()
            {
                Color = new SKColor(0, 255, 120),
                StrokeWidth = 3,
                IsAntialias = true
            };

            canvas.DrawLine(500, 230, 790, 310, linePaint);
        }
        else
        {
            DrawEmotionEyes(canvas, greenPaint, emotion);
        }

        DrawEmotionBrows(canvas, emotion, motionFrame, motionScale, glowIntensity);
        DrawEmotionMouth(canvas, mouthPaint, blackPaint, mouthOpen, emotion);

        canvas.Restore();

        DrawLandscapeBeatOverlay(canvas, beatState, frameIndex);

        if (!string.IsNullOrWhiteSpace(subtitle))
            DrawLandscapeSubtitle(canvas, subtitle);

        return bitmap;
    }

    static void DrawLandscapeSubtitle(SKCanvas canvas, string text)
    {
        using SKTypeface bold = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold);
        using SKFont font = new(bold, 42);
        using SKPaint outline = new()
        {
            Color = new SKColor(0, 0, 0, 240),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 9,
            StrokeJoin = SKStrokeJoin.Round
        };
        using SKPaint fill = new()
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        string[] lines = WrapLandscapeSubtitle(text, font, maxWidth: 1080);
        const float lineHeight = 50;
        float firstBaseline = lines.Length == 1 ? 635 : 608;

        for (int i = 0; i < lines.Length; i++)
        {
            float y = firstBaseline + i * lineHeight;
            canvas.DrawText(lines[i], 640, y, SKTextAlign.Center, font, outline);
            canvas.DrawText(lines[i], 640, y, SKTextAlign.Center, font, fill);
        }
    }

    static void DrawLandscapeBeatOverlay(
        SKCanvas canvas,
        VisualBeatFrameState beatState,
        int frameIndex)
    {
        if (beatState.ShowGlitch)
        {
            using SKPaint glitchPaint = new()
            {
                Color = new SKColor(0, 255, 120, 150),
                StrokeWidth = 3,
                IsAntialias = false
            };

            for (int line = 0; line < 4; line++)
            {
                float y = 150 + ((frameIndex * 29 + line * 113) % 330);
                float x = 390 + ((frameIndex * 17 + line * 71) % 180);
                canvas.DrawLine(x, y, Math.Min(900, x + 220 + line * 25), y, glitchPaint);
            }
        }

        if (beatState.ShowStatusWarning)
        {
            using SKTypeface bold = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold);
            using SKFont warningFont = new(bold, 28);
            using SKPaint warningPaint = new()
            {
                Color = new SKColor(255, 190, 45),
                IsAntialias = true
            };
            using SKPaint warningBox = new()
            {
                Color = new SKColor(65, 30, 0, 210),
                IsAntialias = true
            };

            canvas.DrawRoundRect(new SKRect(930, 35, 1245, 85), 10, 10, warningBox);
            canvas.DrawText(
                "SYSTEM WARNING",
                1087,
                69,
                SKTextAlign.Center,
                warningFont,
                warningPaint);
        }
    }

    static string[] WrapLandscapeSubtitle(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
            return new[] { text };

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int bestSplit = 1;
        float bestBalance = float.MaxValue;

        for (int split = 1; split < words.Length; split++)
        {
            string first = string.Join(" ", words[..split]);
            string second = string.Join(" ", words[split..]);
            float firstWidth = font.MeasureText(first);
            float secondWidth = font.MeasureText(second);

            if (firstWidth > maxWidth || secondWidth > maxWidth)
                continue;

            float balance = Math.Abs(firstWidth - secondWidth);
            if (balance < bestBalance)
            {
                bestBalance = balance;
                bestSplit = split;
            }
        }

        return new[]
        {
            string.Join(" ", words[..bestSplit]),
            string.Join(" ", words[bestSplit..])
        };
    }

    static void RenderVideo(
        string voicePath,
        string videoPath,
        double duration,
        string framesDirectory,
        int fps)
    {
        string framePattern = Path.Combine(framesDirectory, "frame_%04d.png");

        RunProcess("ffmpeg", new[]
        {
            "-y",
            "-framerate", fps.ToString(),
            "-i", framePattern,
            "-i", voicePath,
            "-t", duration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-shortest",
            videoPath
        });
    }

    static void RunProcess(string fileName, string[] args)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)!;
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception($"{fileName} failed with exit code {process.ExitCode}");
        }
    }

    static string RunProcessCapture(string fileName, string[] args)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)!;

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception($"{fileName} failed: {error}");
        }

        return output;
    }

    static void DrawEmotionEyes(SKCanvas canvas, SKPaint greenPaint, EmotionState emotion)
    {
        switch (emotion)
        {
            case EmotionState.Deadpan:
                canvas.DrawRect(new SKRect(520, 272, 610, 284), greenPaint);
                canvas.DrawRect(new SKRect(670, 272, 760, 284), greenPaint);
                break;
            case EmotionState.Annoyed:
                DrawSlantedEye(canvas, greenPaint, 520, 246, 612, 296, -10);
                DrawSlantedEye(canvas, greenPaint, 668, 252, 760, 300, 10);
                break;
            case EmotionState.Smug:
                DrawSlantedEye(canvas, greenPaint, 520, 252, 610, 300, -6);
                DrawSlantedEye(canvas, greenPaint, 670, 246, 760, 294, 8);
                break;
            case EmotionState.Angry:
                DrawSlantedEye(canvas, greenPaint, 518, 248, 610, 298, -16);
                DrawSlantedEye(canvas, greenPaint, 670, 248, 762, 298, 16);
                break;
            case EmotionState.Panicked:
                canvas.DrawRoundRect(new SKRect(515, 236, 620, 308), 8, 8, greenPaint);
                canvas.DrawRoundRect(new SKRect(660, 236, 765, 308), 8, 8, greenPaint);
                break;
            case EmotionState.Sad:
                DrawSlantedEye(canvas, greenPaint, 520, 258, 610, 300, 10);
                DrawSlantedEye(canvas, greenPaint, 670, 258, 760, 300, -10);
                break;
            case EmotionState.Excited:
                canvas.DrawRoundRect(new SKRect(515, 238, 618, 304), 10, 10, greenPaint);
                canvas.DrawRoundRect(new SKRect(662, 238, 765, 304), 10, 10, greenPaint);
                break;
            default:
                canvas.DrawRect(new SKRect(520, 250, 610, 295), greenPaint);
                canvas.DrawRect(new SKRect(670, 250, 760, 295), greenPaint);
                break;
        }
    }

    static void DrawSlantedEye(
        SKCanvas canvas,
        SKPaint paint,
        float left,
        float top,
        float right,
        float bottom,
        float tilt)
    {
        SKPath path = new();
        path.MoveTo(left, top + Math.Max(0, tilt));
        path.LineTo(right, top + Math.Max(0, -tilt));
        path.LineTo(right, bottom + Math.Max(0, -tilt));
        path.LineTo(left, bottom + Math.Max(0, tilt));
        path.Close();
        canvas.DrawPath(path, paint);
    }

    static void DrawEmotionBrows(
        SKCanvas canvas,
        EmotionState emotion,
        int frameIndex,
        float motionScale,
        byte glowIntensity)
    {
        using SKPaint browPaint = new()
        {
            Color = new SKColor(0, 255, 120, glowIntensity),
            StrokeWidth = emotion == EmotionState.Angry ? 7 : 5,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round
        };

        float pulse = motionScale > 0 ? (float)Math.Sin(frameIndex * 0.12f) * motionScale * 2f : 0;

        switch (emotion)
        {
            case EmotionState.Deadpan:
                canvas.DrawLine(518, 235, 612, 235, browPaint);
                canvas.DrawLine(668, 235, 762, 235, browPaint);
                break;
            case EmotionState.Annoyed:
                canvas.DrawLine(518, 228, 612, 242, browPaint);
                canvas.DrawLine(668, 242, 762, 228, browPaint);
                break;
            case EmotionState.Smug:
                canvas.DrawLine(520, 238, 612, 226, browPaint);
                canvas.DrawLine(668, 232, 762, 236, browPaint);
                break;
            case EmotionState.Angry:
                canvas.DrawLine(516, 224, 610, 246, browPaint);
                canvas.DrawLine(670, 246, 764, 224, browPaint);
                break;
            case EmotionState.Panicked:
                canvas.DrawLine(515, 226 - pulse, 612, 214 + pulse, browPaint);
                canvas.DrawLine(668, 214 + pulse, 765, 226 - pulse, browPaint);
                break;
            case EmotionState.Sad:
                canvas.DrawLine(520, 230, 612, 242, browPaint);
                canvas.DrawLine(668, 242, 760, 230, browPaint);
                break;
            case EmotionState.Excited:
                canvas.DrawLine(518, 220 - pulse, 612, 232 - pulse, browPaint);
                canvas.DrawLine(668, 232 - pulse, 762, 220 - pulse, browPaint);
                break;
            default:
                canvas.DrawLine(520, 232, 612, 232, browPaint);
                canvas.DrawLine(668, 232, 760, 232, browPaint);
                break;
        }
    }

    static void DrawEmotionMouth(
        SKCanvas canvas,
        SKPaint mouthPaint,
        SKPaint blackPaint,
        bool mouthOpen,
        EmotionState emotion)
    {
        switch (emotion)
        {
            case EmotionState.Deadpan:
                if (mouthOpen)
                {
                    canvas.DrawRoundRect(new SKRect(575, 382, 705, 405), 8, 8, mouthPaint);
                    canvas.DrawRect(new SKRect(598, 390, 682, 398), blackPaint);
                }
                else
                {
                    canvas.DrawRect(new SKRect(578, 394, 702, 400), mouthPaint);
                }
                break;
            case EmotionState.Annoyed:
                DrawMouthCurve(canvas, mouthPaint, 568, 398, 710, 388, 720, 404, mouthOpen, blackPaint);
                break;
            case EmotionState.Smug:
                DrawMouthCurve(canvas, mouthPaint, 565, 392, 715, 405, 720, 418, mouthOpen, blackPaint);
                break;
            case EmotionState.Angry:
                if (mouthOpen)
                {
                    canvas.DrawRoundRect(new SKRect(556, 364, 724, 434), 10, 10, mouthPaint);
                    canvas.DrawRect(new SKRect(580, 384, 700, 408), blackPaint);
                }
                else
                {
                    canvas.DrawLine(565, 404, 718, 392, mouthPaint);
                }
                break;
            case EmotionState.Panicked:
                if (mouthOpen)
                {
                    canvas.DrawRoundRect(new SKRect(570, 350, 710, 442), 26, 26, mouthPaint);
                    canvas.DrawOval(new SKRect(595, 370, 685, 425), blackPaint);
                }
                else
                {
                    canvas.DrawRoundRect(new SKRect(584, 388, 696, 410), 10, 10, mouthPaint);
                }
                break;
            case EmotionState.Sad:
                DrawMouthCurve(canvas, mouthPaint, 568, 404, 640, 392, 712, 404, mouthOpen, blackPaint);
                break;
            case EmotionState.Excited:
                if (mouthOpen)
                {
                    canvas.DrawRoundRect(new SKRect(555, 354, 725, 436), 18, 18, mouthPaint);
                    canvas.DrawRect(new SKRect(586, 382, 694, 410), blackPaint);
                }
                else
                {
                    canvas.DrawLine(565, 395, 718, 402, mouthPaint);
                }
                break;
            default:
                if (mouthOpen)
                {
                    SKRect mouthRect = new(560, 360, 720, 430);
                    canvas.DrawRoundRect(mouthRect, 12, 12, mouthPaint);
                    canvas.DrawRect(new SKRect(585, 383, 695, 405), blackPaint);
                }
                else
                {
                    canvas.DrawRect(new SKRect(570, 390, 710, 407), mouthPaint);
                }
                break;
        }
    }

    static void DrawMouthCurve(
        SKCanvas canvas,
        SKPaint mouthPaint,
        float startX,
        float startY,
        float controlX,
        float controlY,
        float endX,
        float endY,
        bool mouthOpen,
        SKPaint blackPaint)
    {
        using SKPath path = new();
        path.MoveTo(startX, startY);
        path.QuadTo(controlX, controlY, endX, endY);

        if (mouthOpen)
        {
            using SKPaint fillPaint = new()
            {
                Color = mouthPaint.Color,
                IsAntialias = mouthPaint.IsAntialias,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 16,
                StrokeCap = SKStrokeCap.Round
            };

            canvas.DrawPath(path, fillPaint);
            canvas.DrawRect(new SKRect(592, 388, 688, 406), blackPaint);
        }
        else
        {
            using SKPaint linePaint = new()
            {
                Color = mouthPaint.Color,
                IsAntialias = mouthPaint.IsAntialias,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 7,
                StrokeCap = SKStrokeCap.Round
            };

            canvas.DrawPath(path, linePaint);
        }
    }

    static float GetHorizontalOffset(EmotionState emotion, int frameIndex)
    {
        return emotion switch
        {
            EmotionState.Panicked => ((frameIndex % 4) - 1.5f) * 2.4f,
            EmotionState.Excited => (float)Math.Sin(frameIndex * 0.20f) * 2.5f,
            EmotionState.Smug => 3f,
            EmotionState.Sad => -2f,
            _ => 0f
        };
    }

    static float GetVerticalOffset(EmotionState emotion, int frameIndex)
    {
        return emotion switch
        {
            EmotionState.Deadpan => 0f,
            EmotionState.Excited => (float)Math.Sin(frameIndex * 0.24f) * 3f,
            EmotionState.Panicked => (float)Math.Cos(frameIndex * 0.45f) * 2f,
            EmotionState.Sad => 4f,
            _ => (float)Math.Sin(frameIndex * 0.08f) * 1.2f
        };
    }

    static float GetRotationDegrees(EmotionState emotion, int frameIndex)
    {
        return emotion switch
        {
            EmotionState.Deadpan => 0f,
            EmotionState.Smug => -2.5f,
            EmotionState.Sad => 1.5f,
            EmotionState.Excited => (float)Math.Sin(frameIndex * 0.15f) * 1.5f,
            EmotionState.Panicked => (float)Math.Sin(frameIndex * 0.60f) * 1.2f,
            _ => 0f
        };
    }

    static float GetMotionScale(EmotionState emotion)
    {
        return emotion switch
        {
            EmotionState.Deadpan => 0f,
            EmotionState.Excited => 1.3f,
            EmotionState.Panicked => 1.1f,
            EmotionState.Sad => 0.3f,
            _ => 0.7f
        };
    }

    static byte GetGlowIntensity(EmotionState emotion)
    {
        return emotion switch
        {
            EmotionState.Deadpan => 120,
            EmotionState.Annoyed => 170,
            EmotionState.Smug => 210,
            EmotionState.Angry => 235,
            EmotionState.Panicked => 245,
            EmotionState.Sad => 135,
            EmotionState.Excited => 255,
            _ => 190
        };
    }

    static string GetStatusText(EmotionState emotion)
    {
        return emotion switch
        {
            EmotionState.Deadpan => "DEADPAN",
            EmotionState.Annoyed => "ANNOYED",
            EmotionState.Smug => "SMUG",
            EmotionState.Angry => "ANGRY",
            EmotionState.Panicked => "PANICKED",
            EmotionState.Sad => "SAD",
            EmotionState.Excited => "EXCITED",
            _ => "NEUTRAL"
        };
    }
}
