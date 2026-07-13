using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AI_YOUTUBER.Functions.ASKING;
using AI_YOUTUBER.Functions.EMOTION;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Functions.VISUAL;
using AI_YOUTUBER.Infrastructure;
using AI_YOUTUBER.Models;
using AI_YOUTUBER.Rendering;

namespace AI_YOUTUBER.Functions.BATCH;

public sealed class ShortBatchCoordinator
{
    private readonly string _outputRoot;
    private readonly BatchOptions _options;
    private readonly Action<string, string> _makeVoice;
    private readonly Action<string, string> _normalizeWav;
    private readonly Func<string, double> _getAudioDuration;
    private readonly Action<string, string[]> _runProcess;
    private readonly Func<int, PlannedShort?, string?, int, string?, Task<ShortScriptGenerationResult>>
        _shortScriptGenerator;
    private readonly bool _usesInjectedScriptGenerator;
    private readonly VideoValidationService _validator = new();

    public ShortBatchCoordinator(
        string outputRoot,
        BatchOptions options,
        Action<string, string> makeVoice,
        Action<string, string> normalizeWav,
        Func<string, double> getAudioDuration,
        Action<string, string[]> runProcess,
        Func<int, PlannedShort?, string?, int, string?, Task<ShortScriptGenerationResult>>?
            shortScriptGenerator = null)
    {
        _outputRoot = outputRoot;
        _options = options;
        _makeVoice = makeVoice;
        _normalizeWav = normalizeWav;
        _getAudioDuration = getAudioDuration;
        _runProcess = runProcess;
        _usesInjectedScriptGenerator = shortScriptGenerator is not null;
        _shortScriptGenerator = shortScriptGenerator ??
            ((targetSeconds, plannedShort, batchContext, maximumAttempts, failureReason) =>
                AskAI.GenerateShortScriptAsync(
                    targetSeconds,
                    plannedShort,
                    batchContext,
                    maximumAttempts,
                    failureReason));
    }

    public async Task<string> CreateAndRunAsync(int requestedCount)
    {
        int clampedCount = Math.Clamp(requestedCount, 1, _options.MaximumBatchSize);
        if (clampedCount != requestedCount)
        {
            Console.WriteLine(
                $"[Batch] Requested {requestedCount} Shorts; clamped to {clampedCount}.");
        }

        string batchRoot = GetBatchRoot(testMode: false);
        string batchId = CreateBatchId(batchRoot, testMode: false);
        string batchDirectory = Path.Combine(batchRoot, batchId);
        Directory.CreateDirectory(batchDirectory);
        Console.WriteLine($"[Batch] Batch ID: {batchId}");

        ShortBatchPlan plan = await ShortBatchPlanner.CreatePlanAsync(
            batchId,
            clampedCount,
            _options);
        BatchManifest manifest = CreateManifest(plan, batchDirectory);
        await SavePlanAsync(batchDirectory, plan);
        await SaveManifestAsync(batchDirectory, manifest);

        await RunBatchAsync(plan, manifest, batchDirectory, testMode: false);
        Console.WriteLine($"[Batch] Batch output directory: {batchDirectory}");
        return batchDirectory;
    }

    public async Task<string> ResumeAsync(string batchId)
    {
        return await ResumeInternalAsync(batchId, testMode: false);
    }

    public async Task ShowAsync(string batchId)
    {
        string batchDirectory = ResolveBatchDirectory(batchId, testMode: false);
        BatchManifest manifest = await LoadManifestAsync(batchDirectory);

        Console.WriteLine($"[Batch] Batch ID: {manifest.BatchId}");
        Console.WriteLine($"[Batch] Theme: {manifest.BatchTheme}");
        Console.WriteLine($"[Batch] Overall status: {manifest.Status}");
        foreach (BatchVideoEntry video in manifest.Videos.OrderBy(video => video.Position))
        {
            Console.WriteLine($"[Batch] Short {video.Position}: {video.Title}");
            Console.WriteLine($"[Batch]   Topic: {video.Topic}");
            Console.WriteLine($"[Batch]   Stage: {video.Status}");
            Console.WriteLine($"[Batch]   Video: {video.LocalVideoPath}");
            Console.WriteLine($"[Batch]   Validation passed: {video.ValidationPassed}");
            Console.WriteLine($"[Batch]   Memory saved: {video.MemorySaved}");
            Console.WriteLine($"[Batch]   Error: {(string.IsNullOrWhiteSpace(video.Error) ? "none" : video.Error)}");
        }
    }

    public async Task<string> RunTestAsync()
    {
        const int testCount = 2;
        IReadOnlyList<VideoMemoryRecord> productionBefore = await VideoMemory.LoadAllAsync();
        using IDisposable testMemoryScope = VideoMemory.BeginIsolatedTestMemoryScope("batch");
        string batchRoot = GetBatchRoot(testMode: true);
        string batchId = CreateBatchId(batchRoot, testMode: true);
        string batchDirectory = Path.Combine(batchRoot, batchId);
        Directory.CreateDirectory(batchDirectory);

        Console.WriteLine($"[Batch] Running isolated test batch: {batchId}");
        ShortBatchPlan plan = ShortBatchPlanner.CreateTestPlan(batchId, testCount);
        if (!ShortBatchPlanner.ValidateAndNormalizePlan(
                plan,
                batchId,
                testCount,
                out string planError))
        {
            throw new InvalidOperationException($"Test plan validation failed: {planError}");
        }

        BatchManifest manifest = CreateManifest(plan, batchDirectory);
        await SavePlanAsync(batchDirectory, plan);
        await SaveManifestAsync(batchDirectory, manifest);

        ShortBatchPlan loadedPlan = await LoadPlanAsync(batchDirectory);
        if (loadedPlan.Videos.Count != testCount ||
            !loadedPlan.Videos.Select(video => video.Position).SequenceEqual(new[] { 1, 2 }))
        {
            throw new InvalidOperationException("Test plan did not survive serialization correctly.");
        }

        await RunBatchAsync(loadedPlan, manifest, batchDirectory, testMode: true);
        BatchManifest completed = await LoadManifestAsync(batchDirectory);
        AssertTestManifest(completed);

        IReadOnlyList<VideoMemoryRecord> testRecordsBeforeResume =
            await VideoMemory.LoadAllAsync(testMode: true);
        foreach (BatchVideoEntry entry in completed.Videos)
        {
            if (!testRecordsBeforeResume.Any(record =>
                    record.VideoId.Equals(entry.VideoId, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException(
                    $"Test Short {entry.Position} was not saved in isolated test memory.");
            }
        }

        IReadOnlyList<VideoMemoryRecord> productionAfter = await VideoMemory.LoadAllAsync();
        if (productionAfter.Count != productionBefore.Count)
            throw new InvalidOperationException("Test batch polluted production video memory.");

        BatchVideoEntry first = completed.Videos.OrderBy(video => video.Position).First();
        string firstDirectory = GetShortDirectory(batchDirectory, first.Position);
        string firstScriptPath = Path.Combine(firstDirectory, "script.txt");
        string firstScript = await File.ReadAllTextAsync(firstScriptPath);
        int testMemoryCountBeforeDuplicate = testRecordsBeforeResume.Count;
        VideoMemoryRecord? duplicate = await VideoMemory.SaveCompletedVideoAsync(
            first.VideoId,
            first.Title,
            first.Topic,
            first.LocalVideoPath,
            firstScriptPath,
            firstScript,
            testMode: true,
            forceDeterministicExtraction: true);
        int testMemoryCountAfterDuplicate = (await VideoMemory.LoadAllAsync(testMode: true)).Count;
        if (duplicate is null || testMemoryCountAfterDuplicate != testMemoryCountBeforeDuplicate)
            throw new InvalidOperationException("Test-batch duplicate protection failed.");

        Dictionary<string, DateTime> videoWriteTimes = completed.Videos.ToDictionary(
            video => video.VideoId,
            video => File.GetLastWriteTimeUtc(video.LocalVideoPath),
            StringComparer.OrdinalIgnoreCase);
        await ResumeInternalAsync(batchId, testMode: true);
        BatchManifest resumed = await LoadManifestAsync(batchDirectory);
        AssertTestManifest(resumed);
        foreach (BatchVideoEntry entry in resumed.Videos)
        {
            if (File.GetLastWriteTimeUtc(entry.LocalVideoPath) != videoWriteTimes[entry.VideoId])
            {
                throw new InvalidOperationException(
                    $"Resume unexpectedly regenerated completed test Short {entry.Position}.");
            }
        }

        Console.WriteLine("[Batch] Test plan serialization and loading passed.");
        Console.WriteLine("[Batch] Sequential ordering and manifest stage updates passed.");
        Console.WriteLine("[Batch] Test-memory isolation and duplicate protection passed.");
        Console.WriteLine("[Batch] Resume reused completed validated videos without regeneration.");
        Console.WriteLine("[Batch] Batch test passed.");
        Console.WriteLine($"[Batch] Test output directory: {batchDirectory}");
        return batchDirectory;
    }

    internal async Task<BatchManifest> RunRegressionTestBatchAsync(
        ShortBatchPlan plan,
        string batchDirectory)
    {
        Directory.CreateDirectory(batchDirectory);
        BatchManifest manifest = CreateManifest(plan, batchDirectory);
        await SavePlanAsync(batchDirectory, plan);
        await SaveManifestAsync(batchDirectory, manifest);
        await RunBatchAsync(plan, manifest, batchDirectory, testMode: true);
        return await LoadManifestAsync(batchDirectory);
    }

    private async Task<string> ResumeInternalAsync(string batchId, bool testMode)
    {
        string batchDirectory = ResolveBatchDirectory(batchId, testMode);
        ShortBatchPlan plan = await LoadPlanAsync(batchDirectory);
        BatchManifest manifest = await LoadManifestAsync(batchDirectory);
        if (!ShortBatchPlanner.ValidateAndNormalizePlan(
                plan,
                batchId,
                manifest.RequestedVideoCount,
                out string planError))
        {
            throw new InvalidDataException($"Batch plan is invalid: {planError}");
        }

        NormalizeLoadedManifest(manifest, plan, batchDirectory);

        Console.WriteLine($"[Batch] Resuming batch {batchId}.");
        BatchVideoEntry? firstIncomplete = manifest.Videos
            .OrderBy(video => video.Position)
            .FirstOrDefault(video => !video.Status.Equals(
                BatchVideoStatuses.Completed,
                StringComparison.OrdinalIgnoreCase));
        Console.WriteLine(firstIncomplete is null
            ? "[Batch] Manifest is complete; verifying files and memory before reuse."
            : $"[Batch] First incomplete Short is {firstIncomplete.Position} at stage {firstIncomplete.Status}.");

        await SaveManifestAsync(batchDirectory, manifest);
        await RunBatchAsync(plan, manifest, batchDirectory, testMode);
        Console.WriteLine($"[Batch] Batch output directory: {batchDirectory}");
        return batchDirectory;
    }

    private async Task RunBatchAsync(
        ShortBatchPlan plan,
        BatchManifest manifest,
        string batchDirectory,
        bool testMode)
    {
        manifest.Status = BatchStatuses.Running;
        await SaveManifestAsync(batchDirectory, manifest);

        List<CompletedShortContext> completedShorts = new();
        HashSet<string> completedVideoHashes = new(StringComparer.OrdinalIgnoreCase);

        foreach (PlannedShort planned in plan.Videos.OrderBy(video => video.Position))
        {
            BatchVideoEntry entry = manifest.Videos.Single(video => video.Position == planned.Position);
            string shortDirectory = GetShortDirectory(batchDirectory, planned.Position);
            Directory.CreateDirectory(shortDirectory);
            string scriptPath = Path.Combine(shortDirectory, "script.txt");
            string rawVoicePath = Path.Combine(shortDirectory, "voice.wav");
            string cleanVoicePath = Path.Combine(shortDirectory, "voice_clean.wav");
            string videoPath = Path.Combine(shortDirectory, "video.mp4");
            string scriptValidationPath = Path.Combine(shortDirectory, "script_validation.json");
            string voiceValidationPath = Path.Combine(shortDirectory, "voice_validation.json");
            string validationPath = Path.Combine(shortDirectory, "validation.json");
            string metadataPath = Path.Combine(shortDirectory, "metadata.json");
            string framesDirectory = Path.Combine(shortDirectory, "frames");
            string timingPath = Path.Combine(shortDirectory, "run_metrics.json");

            if (entry.Status.Equals(BatchVideoStatuses.Completed, StringComparison.OrdinalIgnoreCase) &&
                (!File.Exists(videoPath) || !File.Exists(scriptPath)))
            {
                string missingMessage =
                    "Manifest marked this Short complete, but a required video or script file is missing. Regeneration required.";
                AppendError(entry, missingMessage);
                entry.Status = BatchVideoStatuses.Planned;
                entry.ValidationPassed = false;
                entry.MemorySaved = false;
                entry.CompletedUtc = null;
                Console.WriteLine($"[Batch] Short {entry.Position}: {missingMessage}");
                await SaveManifestAsync(batchDirectory, manifest);
            }

            if (File.Exists(videoPath) && File.Exists(scriptPath))
            {
                string existingScript = await File.ReadAllTextAsync(scriptPath);
                (int minimumWords, int maximumWords) = GetAcceptedWordRange();
                ShortScriptValidationResult existingScriptValidation =
                    ShortScriptValidator.Validate(
                        existingScript,
                        minimumWords,
                        maximumWords);
                VoiceDurationValidationResult existingVoiceValidation;
                if (File.Exists(cleanVoicePath) && new FileInfo(cleanVoicePath).Length > 44)
                {
                    double existingAudioDuration = _getAudioDuration(cleanVoicePath);
                    existingVoiceValidation = ValidateVoiceDuration(existingAudioDuration);
                }
                else
                {
                    existingVoiceValidation = ValidateVoiceDuration(0);
                    existingVoiceValidation.Errors.Add(
                        "Normalized voice file required for reuse is missing.");
                    existingVoiceValidation.Success = false;
                }

                entry.ScriptValidation = existingScriptValidation;
                entry.VoiceDurationValidation = existingVoiceValidation;
                await AtomicJsonFile.WriteAsync(scriptValidationPath, existingScriptValidation);
                await AtomicJsonFile.WriteAsync(voiceValidationPath, existingVoiceValidation);
                VideoValidationResult existingValidation = await _validator.ValidateAsync(
                    CreateVideoValidationRequest(
                        videoPath,
                        scriptPath,
                        completedVideoHashes,
                        existingScriptValidation,
                        existingVoiceValidation,
                        testMode));
                entry.VideoValidation = existingValidation;
                await AtomicJsonFile.WriteAsync(validationPath, existingValidation);

                if (existingValidation.Success &&
                    (testMode || existingValidation.FullValidationPerformed))
                {
                    Console.WriteLine(
                        $"[Batch] Reusing existing validated video for Short {entry.Position}: {videoPath}");
                    entry.ValidationPassed = true;
                    await UpdateStageAsync(
                        manifest,
                        entry,
                        BatchVideoStatuses.Validated,
                        batchDirectory);

                    VideoMemoryRecord? reusedMemory = await SaveBatchMemoryAsync(
                        entry,
                        planned,
                        videoPath,
                        scriptPath,
                        existingScript,
                        testMode,
                        existingScriptValidation,
                        existingVoiceValidation,
                        existingValidation,
                        renderingCompleted: true);
                    if (reusedMemory is null)
                    {
                        await MarkFailedAsync(
                            manifest,
                            entry,
                            batchDirectory,
                            "Validated output could not be saved to video memory.");
                        if (_options.StopOnVideoFailure)
                            break;
                        continue;
                    }

                    entry.MemorySaved = true;
                    await UpdateStageAsync(
                        manifest,
                        entry,
                        BatchVideoStatuses.MemorySaved,
                        batchDirectory);
                    Console.WriteLine(testMode
                        ? $"[VideoMemory] Reused Short {entry.Position} in isolated test memory."
                        : $"[VideoMemory] Reused Short {entry.Position} in official memory.");
                    await UpdateStageAsync(
                        manifest,
                        entry,
                        BatchVideoStatuses.Completed,
                        batchDirectory);
                    completedVideoHashes.Add(existingValidation.FileHash);
                    completedShorts.Add(ToCompletedContext(planned, reusedMemory));
                    continue;
                }

                string validationError = string.Join(" ", existingValidation.Errors);
                AppendError(
                    entry,
                    $"Existing video was not reusable: {validationError}");
                entry.ValidationPassed = false;
                entry.MemorySaved = false;
                entry.CompletedUtc = null;
                Console.WriteLine(
                    $"[Batch] Existing video for Short {entry.Position} failed validation and will be regenerated.");
                await SaveManifestAsync(batchDirectory, manifest);
            }

            Console.WriteLine(
                $"[Batch] Beginning Short {planned.Position} of {plan.Videos.Count} with updated memory.");
            ExecutionTimingService timing = new(
                testMode ? "test-batch-short" : "batch-short",
                timingPath);

            try
            {
                string script = "";
                ShortScriptValidationResult scriptValidation = new();
                VoiceDurationValidationResult voiceValidation = new();
                double audioDuration = 0;
                bool reusedScript = File.Exists(scriptPath) &&
                    !string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(scriptPath));
                bool useExistingScript = reusedScript;
                bool useExistingVoice = reusedScript &&
                    File.Exists(cleanVoicePath) &&
                    new FileInfo(cleanVoicePath).Length > 44;
                int remainingGenerationAttempts = reusedScript
                    ? _options.MaximumScriptGenerationAttempts - 1
                    : _options.MaximumScriptGenerationAttempts;
                string? regenerationReason = null;
                (int minimumWords, int maximumWords) = GetAcceptedWordRange();

                while (true)
                {
                    if (useExistingScript)
                    {
                        script = await File.ReadAllTextAsync(scriptPath);
                        Console.WriteLine($"[Batch] Validating existing script for Short {entry.Position}.");
                        scriptValidation = ShortScriptValidator.Validate(
                            script,
                            minimumWords,
                            maximumWords);
                        useExistingScript = false;
                        if (!scriptValidation.Success)
                            regenerationReason = ShortScriptValidator.DescribeFailure(scriptValidation);
                    }
                    else if (testMode && !_usesInjectedScriptGenerator)
                    {
                        await UpdateStageAsync(
                            manifest,
                            entry,
                            BatchVideoStatuses.GeneratingScript,
                            batchDirectory);
                        script = CreateTestScript(plan, planned);
                        scriptValidation = ShortScriptValidator.Validate(
                            script,
                            minimumWords,
                            maximumWords);
                        remainingGenerationAttempts = 0;
                    }
                    else
                    {
                        if (remainingGenerationAttempts <= 0)
                        {
                            throw new InvalidOperationException(
                                "Short script generation exhausted its retry allowance. " +
                                string.Join(" ", scriptValidation.Errors));
                        }

                        await UpdateStageAsync(
                            manifest,
                            entry,
                            BatchVideoStatuses.GeneratingScript,
                            batchDirectory);
                        string batchContext;
                        if (testMode)
                        {
                            batchContext = "Isolated regression test. No production memory is supplied.";
                        }
                        else
                        {
                            MemoryContext memory = await VideoMemory.BuildContextForTopicAsync(
                                planned.Topic,
                                _options.RecentMemoryCount,
                                _options.RelevantMemoryCount,
                                testMode: false);
                            batchContext = BuildBatchContext(
                                plan,
                                planned,
                                completedShorts,
                                memory);
                        }

                        ShortScriptGenerationResult generationResult = await timing.MeasureAsync(
                            "Batch Short script generation",
                            () => _shortScriptGenerator(
                                _options.ShortDurationSeconds,
                                planned,
                                batchContext,
                                remainingGenerationAttempts,
                                regenerationReason));
                        int consumedAttempts = Math.Clamp(
                            generationResult.AttemptCount,
                            1,
                            remainingGenerationAttempts);
                        remainingGenerationAttempts -= consumedAttempts;
                        entry.ScriptGenerationAttempts += consumedAttempts;
                        script = generationResult.Script;
                        scriptValidation = generationResult.Validation;
                    }

                    entry.ScriptValidation = scriptValidation;
                    await AtomicJsonFile.WriteAsync(scriptValidationPath, scriptValidation);
                    await SaveManifestAsync(batchDirectory, manifest);
                    if (!scriptValidation.Success)
                    {
                        if (remainingGenerationAttempts > 0)
                        {
                            reusedScript = false;
                            useExistingVoice = false;
                            regenerationReason =
                                ShortScriptValidator.DescribeFailure(scriptValidation);
                            continue;
                        }

                        throw new InvalidOperationException(
                            "Short script validation failed after retries: " +
                            string.Join(" ", scriptValidation.Errors));
                    }

                    await AtomicWriteTextAsync(scriptPath, script);
                    await UpdateStageAsync(
                        manifest,
                        entry,
                        BatchVideoStatuses.Scripted,
                        batchDirectory);

                    bool reusedCurrentVoice = useExistingVoice;
                    useExistingVoice = false;
                    if (reusedCurrentVoice)
                    {
                        Console.WriteLine(
                            $"[Batch] Reusing existing normalized voice for Short {entry.Position}.");
                        await UpdateStageAsync(
                            manifest,
                            entry,
                            BatchVideoStatuses.VoiceGenerated,
                            batchDirectory);
                    }
                    else
                    {
                        await UpdateStageAsync(
                            manifest,
                            entry,
                            BatchVideoStatuses.GeneratingVoice,
                            batchDirectory);
                        if (testMode)
                        {
                            timing.Measure(
                                "Test voice generation",
                                () => GenerateTestVoice(cleanVoicePath, planned.Position));
                        }
                        else if (reusedScript &&
                            File.Exists(rawVoicePath) &&
                            new FileInfo(rawVoicePath).Length > 44)
                        {
                            Console.WriteLine(
                                $"[Batch] Reusing raw voice and normalizing it for Short {entry.Position}.");
                            timing.Measure(
                                "WAV normalization",
                                () => _normalizeWav(rawVoicePath, cleanVoicePath));
                        }
                        else
                        {
                            timing.Measure(
                                "Piper voice generation",
                                () => _makeVoice(script, rawVoicePath));
                            timing.Measure(
                                "WAV normalization",
                                () => _normalizeWav(rawVoicePath, cleanVoicePath));
                        }

                        if (!File.Exists(cleanVoicePath) || new FileInfo(cleanVoicePath).Length <= 44)
                            throw new InvalidOperationException("TTS did not produce usable normalized audio.");

                        await UpdateStageAsync(
                            manifest,
                            entry,
                            BatchVideoStatuses.VoiceGenerated,
                            batchDirectory);
                    }

                    audioDuration = timing.Measure(
                        "Audio analysis",
                        () => _getAudioDuration(cleanVoicePath));
                    voiceValidation = ValidateVoiceDuration(audioDuration);
                    entry.VoiceDurationValidation = voiceValidation;
                    await AtomicJsonFile.WriteAsync(voiceValidationPath, voiceValidation);
                    await SaveManifestAsync(batchDirectory, manifest);
                    if (voiceValidation.Success)
                        break;

                    regenerationReason =
                        $"The narration produced {audioDuration:F2} seconds of speech, outside the " +
                        $"required {voiceValidation.MinimumAcceptedDurationSeconds:F2}-" +
                        $"{voiceValidation.MaximumAcceptedDurationSeconds:F2} second range. " +
                        "Rewrite to the requested word range with complete, naturally paced prose.";
                    if (remainingGenerationAttempts <= 0 || testMode && !_usesInjectedScriptGenerator)
                    {
                        throw new InvalidOperationException(
                            "Voice-duration validation failed after script retries: " +
                            string.Join(" ", voiceValidation.Errors));
                    }

                    Console.WriteLine(
                        "[VoiceDurationValidation] Regenerating the complete script; " +
                        "extremely short audio will not be stretched.");
                    reusedScript = false;
                    if (File.Exists(rawVoicePath))
                        File.Delete(rawVoicePath);
                    if (File.Exists(cleanVoicePath))
                        File.Delete(cleanVoicePath);
                }

                if (!scriptValidation.Success || !voiceValidation.Success)
                {
                    throw new InvalidOperationException(
                        "Short cannot proceed without successful script and voice-duration validation.");
                }

                await UpdateStageAsync(
                    manifest,
                    entry,
                    BatchVideoStatuses.PlanningScenes,
                    batchDirectory);

                List<EmotionTimelineEntry> emotionTimeline = EmotionTimelinePlanner.BuildTimeline(
                    script,
                    audioDuration);
                List<SubtitleCue> subtitles = SubtitlePlanner.BuildCues(
                    script,
                    audioDuration,
                    minimumWords: 2,
                    maximumWords: 5,
                    audioPath: cleanVoicePath);
                List<VisualBeatTimelineEntry> visualBeats = VisualBeatPlanner.BuildTimeline(
                    script,
                    subtitles,
                    emotionTimeline,
                    audioDuration,
                    VideoMode.Short);
                SubtitlePlanner.SaveSrt(Path.Combine(shortDirectory, "subtitles.srt"), subtitles);
                await EmotionTimelinePlanner.SaveTimelineAsync(shortDirectory, emotionTimeline);
                await VisualBeatPlanner.SaveTimelineAsync(shortDirectory, visualBeats);
                await AtomicJsonFile.WriteAsync(metadataPath, new BatchShortMetadata
                {
                    BatchId = plan.BatchId,
                    VideoId = entry.VideoId,
                    Position = planned.Position,
                    Title = planned.WorkingTitle,
                    Topic = planned.Topic,
                    Hook = planned.Hook,
                    AudioDurationSeconds = audioDuration,
                    UpdatedUtc = DateTime.UtcNow,
                    RequiredPoints = planned.RequiredPoints,
                    AvoidRepeating = planned.AvoidRepeating
                });

                await UpdateStageAsync(
                    manifest,
                    entry,
                    BatchVideoStatuses.GeneratingImages,
                    batchDirectory);
                if (testMode)
                {
                    await UpdateStageAsync(
                        manifest,
                        entry,
                        BatchVideoStatuses.Rendering,
                        batchDirectory);
                    timing.Measure(
                        "Test video rendering",
                        () => GenerateTestVideo(
                            cleanVoicePath,
                            videoPath,
                            audioDuration,
                            planned.Position));
                }
                else
                {
                    ShortRenderer.Render(
                        framesDirectory,
                        cleanVoicePath,
                        videoPath,
                        audioDuration,
                        script,
                        emotionTimeline,
                        visualBeats,
                        timing,
                        _runProcess,
                        () => UpdateStageAsync(
                                manifest,
                                entry,
                                BatchVideoStatuses.Rendering,
                                batchDirectory)
                            .GetAwaiter()
                            .GetResult());
                }

                await UpdateStageAsync(
                    manifest,
                    entry,
                    BatchVideoStatuses.Rendered,
                    batchDirectory);
                Console.WriteLine($"[Batch] Short {entry.Position} rendered successfully.");

                await UpdateStageAsync(
                    manifest,
                    entry,
                    BatchVideoStatuses.Validating,
                    batchDirectory);
                VideoValidationResult validation = await timing.MeasureAsync(
                    "Video validation",
                    () => _validator.ValidateAsync(CreateVideoValidationRequest(
                        videoPath,
                        scriptPath,
                        completedVideoHashes,
                        scriptValidation,
                        voiceValidation,
                        testMode)));
                entry.VideoValidation = validation;
                await AtomicJsonFile.WriteAsync(validationPath, validation);
                if (!validation.Success || !testMode && !validation.FullValidationPerformed)
                    throw new InvalidOperationException(string.Join(" ", validation.Errors));

                entry.ValidationPassed = true;
                await UpdateStageAsync(
                    manifest,
                    entry,
                    BatchVideoStatuses.Validated,
                    batchDirectory);
                Console.WriteLine($"[VideoValidation] Short {entry.Position} passed validation.");

                VideoMemoryRecord? memoryRecord = await timing.MeasureAsync(
                    "Completed video memory saving",
                    () => SaveBatchMemoryAsync(
                        entry,
                        planned,
                        videoPath,
                        scriptPath,
                        script,
                        testMode,
                        scriptValidation,
                        voiceValidation,
                        validation,
                        renderingCompleted: true));
                if (memoryRecord is null)
                    throw new InvalidOperationException("Completed Short could not be saved to video memory.");

                entry.MemorySaved = true;
                await UpdateStageAsync(
                    manifest,
                    entry,
                    BatchVideoStatuses.MemorySaved,
                    batchDirectory);
                Console.WriteLine(testMode
                    ? $"[VideoMemory] Saved Short {entry.Position} to isolated test memory."
                    : $"[VideoMemory] Saved Short {entry.Position} to official memory.");
                await UpdateStageAsync(
                    manifest,
                    entry,
                    BatchVideoStatuses.Completed,
                    batchDirectory);

                completedVideoHashes.Add(validation.FileHash);
                completedShorts.Add(ToCompletedContext(planned, memoryRecord));
                await timing.CompleteAndSaveAsync(true);
            }
            catch (Exception ex)
            {
                await MarkFailedAsync(manifest, entry, batchDirectory, ex.Message);
                try
                {
                    await timing.CompleteAndSaveAsync(false, entry.Status, ex.Message);
                }
                catch (Exception timingException)
                {
                    Console.WriteLine(
                        $"[Batch] Could not save failure timing for Short {entry.Position}: {timingException.Message}");
                }

                if (_options.StopOnVideoFailure)
                {
                    Console.WriteLine(
                        $"[Batch] Stopping after Short {entry.Position} failure; later Shorts may depend on it.");
                    break;
                }
            }
        }

        int completedCount = manifest.Videos.Count(video => video.Status.Equals(
            BatchVideoStatuses.Completed,
            StringComparison.OrdinalIgnoreCase));
        manifest.Status = completedCount == manifest.Videos.Count
            ? BatchStatuses.Completed
            : completedCount > 0
                ? BatchStatuses.PartiallyCompleted
                : BatchStatuses.Failed;
        await SaveManifestAsync(batchDirectory, manifest);
        Console.WriteLine(
            $"[Batch] Batch {manifest.BatchId} finished with status {manifest.Status} " +
            $"({completedCount}/{manifest.Videos.Count} completed). No upload was attempted.");
    }

    private async Task<VideoMemoryRecord?> SaveBatchMemoryAsync(
        BatchVideoEntry entry,
        PlannedShort planned,
        string videoPath,
        string scriptPath,
        string script,
        bool testMode,
        ShortScriptValidationResult scriptValidation,
        VoiceDurationValidationResult voiceValidation,
        VideoValidationResult videoValidation,
        bool renderingCompleted)
    {
        return await VideoMemory.SaveCompletedVideoAsync(
            entry.VideoId,
            planned.WorkingTitle,
            planned.Topic,
            videoPath,
            scriptPath,
            script,
            testMode: testMode,
            forceDeterministicExtraction: testMode,
            validationEvidence: new ProductionValidationEvidence
            {
                ScriptValidation = scriptValidation,
                TtsCompleted = true,
                VoiceDurationValidation = voiceValidation,
                RenderingCompleted = renderingCompleted,
                VideoValidation = videoValidation,
                IsTestOrPreview = testMode
            });
    }

    private (int MinimumWords, int MaximumWords) GetAcceptedWordRange() =>
        ShortScriptValidator.GetWordRange(
            _options.ShortDurationSeconds,
            _options.ShortMinimumWordTolerance,
            _options.ShortMaximumWordTolerance);

    private VoiceDurationValidationResult ValidateVoiceDuration(double actualDurationSeconds) =>
        VoiceDurationValidator.Validate(
            actualDurationSeconds,
            _options.ShortDurationSeconds,
            _options.MinimumDurationRatio,
            _options.MaximumDurationRatio);

    private VideoValidationRequest CreateVideoValidationRequest(
        string videoPath,
        string scriptPath,
        IReadOnlyCollection<string> completedVideoHashes,
        ShortScriptValidationResult scriptValidation,
        VoiceDurationValidationResult voiceValidation,
        bool testMode) =>
        new()
        {
            VideoPath = videoPath,
            ScriptPath = scriptPath,
            RequestedDurationSeconds = _options.ShortDurationSeconds,
            MinimumDurationRatio = _options.MinimumDurationRatio,
            MaximumDurationRatio = _options.MaximumDurationRatio,
            MaximumAudioVideoDifferenceSeconds = _options.MaximumAudioVideoDifferenceSeconds,
            AbsoluteMaximumDurationSeconds = _options.MaximumShortDurationSeconds,
            MinimumFileSizeBytes = testMode ? 1_000 : _options.MinimumVideoFileSizeBytes,
            CompletedVideoHashes = completedVideoHashes,
            AllowLimitedValidationWithoutFfprobe =
                _options.AllowLimitedValidationWithoutFfprobe,
            ScriptValidation = scriptValidation,
            VoiceDurationValidation = voiceValidation
        };

    private string BuildBatchContext(
        ShortBatchPlan plan,
        PlannedShort current,
        IReadOnlyList<CompletedShortContext> completed,
        MemoryContext memory)
    {
        string completedSummaries = completed.Count == 0
            ? "None. This is the first successfully completed Short in this batch."
            : string.Join(
                "\n",
                completed.Select(item =>
                    $"- Short {item.Position}: {item.Title} — {item.Summary}"));
        string usedHooks = completed.Count == 0
            ? "None."
            : string.Join("\n", completed.Select(item => $"- {item.Hook}"));
        List<string> usedMaterialItems = completed
            .SelectMany(item => item.UsedMaterial)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();
        string usedMaterial = usedMaterialItems.Count == 0
            ? "None recorded."
            : string.Join("\n", usedMaterialItems.Select(item => $"- {item}"));

        string context = $"""
        CURRENT BATCH

        Batch ID:
        {plan.BatchId}

        Batch theme:
        {plan.BatchTheme}

        This is Short {current.Position} of {plan.Videos.Count}.

        Purpose of this Short:
        {current.PurposeInBatch}

        Topic:
        {current.Topic}

        Required points:
        {FormatList(current.RequiredPoints)}

        Already completed in this batch:
        {completedSummaries}

        Hooks already used:
        {usedHooks}

        Jokes or explanations already used:
        {usedMaterial}

        Things this Short should avoid repeating:
        {FormatList(current.AvoidRepeating)}

        Relevant previous-video memory:
        {memory.FormattedContext}

        HISTORY GROUNDING
        Do not invent completed experiments, crashes, benchmarks, test results, viewer reactions,
        previous videos, hardware failures, or promises. Only describe an event as already completed
        when it appears in the supplied completed-batch context, previous-video memory, verified
        research, or explicit project facts. Describe unperformed experiments as plans, questions,
        predictions, or upcoming tests. Short 1 must not say something happened "again" unless the
        supplied previous-video memory proves it.
        """;

        return TruncateWords(context, _options.MaximumBatchContextWords);
    }

    private void GenerateTestVoice(string outputPath, int position)
    {
        double duration = _options.ShortDurationSeconds + position * 0.05;
        _runProcess("ffmpeg", new[]
        {
            "-y",
            "-f", "lavfi",
            "-i", $"sine=frequency={420 + position * 110}:duration={duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            "-ac", "1",
            "-ar", "22050",
            "-sample_fmt", "s16",
            outputPath
        });
    }

    private void GenerateTestVideo(
        string audioPath,
        string videoPath,
        double duration,
        int position)
    {
        string[] colors = { "0x063d22", "0x102a52", "0x4a1738", "0x4c3608", "0x263c09" };
        string color = colors[(position - 1) % colors.Length];
        _runProcess("ffmpeg", new[]
        {
            "-y",
            "-f", "lavfi",
            "-i", $"color=c={color}:s=360x640:r=12:d={duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            "-i", audioPath,
            "-t", duration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-shortest",
            videoPath
        });
    }

    private static string CreateTestScript(ShortBatchPlan plan, PlannedShort planned)
    {
        return $"""
        This isolated diagnostic checks batch Short {planned.Position} without touching production memory.
        A synthetic script, voice, vertical render, validation record, and temporary memory entry must
        appear in that order. The signal stays local, deterministic, and separate from every real video.
        Repeating the command starts with empty test memory, so yesterday's fixtures cannot change today's
        result. Nothing will be uploaded or treated as channel history. When every quality gate agrees,
        this temporary workstation check is complete.
        """.Replace("\n", " ").Trim();
    }

    private static BatchManifest CreateManifest(ShortBatchPlan plan, string batchDirectory)
    {
        DateTime now = DateTime.UtcNow;
        return new BatchManifest
        {
            BatchId = plan.BatchId,
            CreatedUtc = now,
            UpdatedUtc = now,
            Status = BatchStatuses.Planned,
            RequestedVideoCount = plan.Videos.Count,
            BatchTheme = plan.BatchTheme,
            Videos = plan.Videos.Select(planned => new BatchVideoEntry
            {
                VideoId = $"{plan.BatchId}-short-{planned.Position:000}",
                Position = planned.Position,
                Topic = planned.Topic,
                Title = planned.WorkingTitle,
                LocalVideoPath = Path.Combine(
                    GetShortDirectory(batchDirectory, planned.Position),
                    "video.mp4"),
                Status = BatchVideoStatuses.Planned,
                StageHistory = new List<string> { BatchVideoStatuses.Planned }
            }).ToList()
        };
    }

    private async Task UpdateStageAsync(
        BatchManifest manifest,
        BatchVideoEntry entry,
        string status,
        string batchDirectory)
    {
        entry.Status = status;
        if (entry.StartedUtc is null && !status.Equals(
                BatchVideoStatuses.Planned,
                StringComparison.OrdinalIgnoreCase))
        {
            entry.StartedUtc = DateTime.UtcNow;
        }

        entry.StageHistory ??= new List<string>();
        if (entry.StageHistory.Count == 0 ||
            !entry.StageHistory[^1].Equals(status, StringComparison.OrdinalIgnoreCase))
        {
            entry.StageHistory.Add(status);
        }

        if (status.Equals(BatchVideoStatuses.Completed, StringComparison.OrdinalIgnoreCase))
            entry.CompletedUtc ??= DateTime.UtcNow;

        Console.WriteLine($"[Batch] Short {entry.Position} stage: {status}");
        await SaveManifestAsync(batchDirectory, manifest);
    }

    private async Task MarkFailedAsync(
        BatchManifest manifest,
        BatchVideoEntry entry,
        string batchDirectory,
        string error)
    {
        AppendError(entry, error);
        await UpdateStageAsync(
            manifest,
            entry,
            BatchVideoStatuses.Failed,
            batchDirectory);
        int completed = manifest.Videos.Count(video => video.Status.Equals(
            BatchVideoStatuses.Completed,
            StringComparison.OrdinalIgnoreCase));
        manifest.Status = completed > 0
            ? BatchStatuses.PartiallyCompleted
            : BatchStatuses.Failed;
        Console.WriteLine($"[Batch] Short {entry.Position} failed: {error}");
        await SaveManifestAsync(batchDirectory, manifest);
    }

    private static void AppendError(BatchVideoEntry entry, string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return;
        if (entry.Error.Contains(error, StringComparison.OrdinalIgnoreCase))
            return;

        entry.Error = string.IsNullOrWhiteSpace(entry.Error)
            ? error.Trim()
            : entry.Error.TrimEnd() + " | " + error.Trim();
    }

    private static CompletedShortContext ToCompletedContext(
        PlannedShort planned,
        VideoMemoryRecord memory)
    {
        List<string> usedMaterial = memory.KeyPoints
            .Concat(memory.JokesAndLore)
            .Concat(memory.EventsAndExperiments)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();
        return new CompletedShortContext(
            planned.Position,
            planned.WorkingTitle,
            ExtractUsedHook(memory.CompactScriptExcerpt, planned.Hook),
            memory.Summary,
            usedMaterial);
    }

    private static string ExtractUsedHook(string excerpt, string fallback)
    {
        string normalized = Regex.Replace(excerpt ?? "", @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return fallback;

        int sentenceEnd = normalized.IndexOfAny(new[] { '.', '!', '?' });
        string hook = sentenceEnd >= 0
            ? normalized[..(sentenceEnd + 1)]
            : normalized;
        return hook.Length <= 220 ? hook : hook[..220].TrimEnd() + "...";
    }

    private static void NormalizeLoadedManifest(
        BatchManifest manifest,
        ShortBatchPlan plan,
        string batchDirectory)
    {
        manifest.Videos ??= new List<BatchVideoEntry>();
        foreach (PlannedShort planned in plan.Videos)
        {
            BatchVideoEntry? entry = manifest.Videos.FirstOrDefault(video =>
                video.Position == planned.Position);
            if (entry is null)
            {
                manifest.Videos.Add(new BatchVideoEntry
                {
                    VideoId = $"{plan.BatchId}-short-{planned.Position:000}",
                    Position = planned.Position,
                    Topic = planned.Topic,
                    Title = planned.WorkingTitle,
                    LocalVideoPath = Path.Combine(
                        GetShortDirectory(batchDirectory, planned.Position),
                        "video.mp4"),
                    Status = BatchVideoStatuses.Planned,
                    StageHistory = new List<string> { BatchVideoStatuses.Planned },
                    Error = "Manifest entry was missing and was reconstructed during resume."
                });
                continue;
            }

            entry.StageHistory ??= new List<string>();
            entry.Error ??= "";
            entry.LocalVideoPath = Path.Combine(
                GetShortDirectory(batchDirectory, planned.Position),
                "video.mp4");
        }

        manifest.Videos = manifest.Videos.OrderBy(video => video.Position).ToList();
    }

    private static void AssertTestManifest(BatchManifest manifest)
    {
        if (!manifest.Status.Equals(BatchStatuses.Completed, StringComparison.OrdinalIgnoreCase) ||
            manifest.Videos.Any(video =>
                !video.Status.Equals(BatchVideoStatuses.Completed, StringComparison.OrdinalIgnoreCase) ||
                !video.ValidationPassed ||
                !video.MemorySaved ||
                video.ScriptValidation?.Success != true ||
                video.VoiceDurationValidation?.Success != true ||
                video.VideoValidation?.Success != true))
        {
            throw new InvalidOperationException("Test manifest did not finish with validated completed videos.");
        }

        string[] requiredStages =
        {
            BatchVideoStatuses.GeneratingScript,
            BatchVideoStatuses.Scripted,
            BatchVideoStatuses.GeneratingVoice,
            BatchVideoStatuses.VoiceGenerated,
            BatchVideoStatuses.PlanningScenes,
            BatchVideoStatuses.GeneratingImages,
            BatchVideoStatuses.Rendering,
            BatchVideoStatuses.Rendered,
            BatchVideoStatuses.Validating,
            BatchVideoStatuses.Validated,
            BatchVideoStatuses.MemorySaved,
            BatchVideoStatuses.Completed
        };
        foreach (BatchVideoEntry entry in manifest.Videos)
        {
            foreach (string stage in requiredStages)
            {
                if (!entry.StageHistory.Contains(stage, StringComparer.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Test manifest did not record stage '{stage}' for Short {entry.Position}.");
                }
            }
        }

        List<BatchVideoEntry> ordered = manifest.Videos.OrderBy(video => video.Position).ToList();
        for (int index = 1; index < ordered.Count; index++)
        {
            if (ordered[index - 1].CompletedUtc is null ||
                ordered[index].StartedUtc is null ||
                ordered[index].StartedUtc < ordered[index - 1].CompletedUtc)
            {
                throw new InvalidOperationException("Test batch did not execute in sequential order.");
            }
        }
    }

    private static async Task SavePlanAsync(string batchDirectory, ShortBatchPlan plan) =>
        await AtomicJsonFile.WriteAsync(Path.Combine(batchDirectory, "batch_plan.json"), plan);

    private static async Task SaveManifestAsync(
        string batchDirectory,
        BatchManifest manifest)
    {
        manifest.UpdatedUtc = DateTime.UtcNow;
        await AtomicJsonFile.WriteAsync(
            Path.Combine(batchDirectory, "batch_manifest.json"),
            manifest);
    }

    private static async Task<ShortBatchPlan> LoadPlanAsync(string batchDirectory)
    {
        string path = Path.Combine(batchDirectory, "batch_plan.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Batch plan was not found.", path);
        return await AtomicJsonFile.ReadAsync<ShortBatchPlan>(path);
    }

    private static async Task<BatchManifest> LoadManifestAsync(string batchDirectory)
    {
        string path = Path.Combine(batchDirectory, "batch_manifest.json");
        if (!File.Exists(path))
            throw new FileNotFoundException("Batch manifest was not found.", path);
        return await AtomicJsonFile.ReadAsync<BatchManifest>(path);
    }

    private string GetBatchRoot(bool testMode) => Path.Combine(
        _outputRoot,
        testMode ? "test-batches" : "batches");

    private string ResolveBatchDirectory(string batchId, bool testMode)
    {
        if (string.IsNullOrWhiteSpace(batchId) ||
            !Regex.IsMatch(batchId, @"^[A-Za-z0-9_-]+$"))
        {
            throw new ArgumentException("Batch ID contains unsupported characters.", nameof(batchId));
        }

        string root = Path.GetFullPath(GetBatchRoot(testMode));
        string directory = Path.GetFullPath(Path.Combine(root, batchId));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Batch path escaped the configured output directory.");
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Batch was not found: {directory}");
        return directory;
    }

    private static string CreateBatchId(string batchRoot, bool testMode)
    {
        Directory.CreateDirectory(batchRoot);
        string prefix = testMode
            ? $"test-batch-{DateTime.UtcNow:yyyyMMdd}-"
            : $"batch-{DateTime.UtcNow:yyyyMMdd}-";
        int largest = Directory.GetDirectories(batchRoot, prefix + "*")
            .Select(Path.GetFileName)
            .Where(name => name is not null && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(name => int.TryParse(name![prefix.Length..], out int number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max();
        return prefix + (largest + 1).ToString("000");
    }

    private static string GetShortDirectory(string batchDirectory, int position) =>
        Path.Combine(batchDirectory, $"short-{position:000}");

    private static string FormatList(IEnumerable<string> values)
    {
        List<string> items = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        return items.Count == 0 ? "- None." : string.Join("\n", items.Select(item => $"- {item}"));
    }

    private static string TruncateWords(string text, int maximumWords)
    {
        MatchCollection words = Regex.Matches(text, @"\S+");
        if (words.Count <= maximumWords)
            return text.Trim();

        Match last = words[maximumWords - 1];
        return text[..(last.Index + last.Length)].TrimEnd() +
            "\n[Batch context truncated to stay within its prompt budget.]";
    }

    private static async Task AtomicWriteTextAsync(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, text, Encoding.UTF8);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record CompletedShortContext(
        int Position,
        string Title,
        string Hook,
        string Summary,
        List<string> UsedMaterial);
}
