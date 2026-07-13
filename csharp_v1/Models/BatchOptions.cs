namespace AI_YOUTUBER.Models;

public sealed class BatchOptions
{
    public int DefaultBatchSize { get; set; } = 5;
    public int MaximumBatchSize { get; set; } = 20;
    public bool StopOnVideoFailure { get; set; } = true;
    public int RecentMemoryCount { get; set; } = 3;
    public int RelevantMemoryCount { get; set; } = 5;
    public int MaximumBatchContextWords { get; set; } = 1800;
    public int ShortDurationSeconds { get; set; } = 30;
    public int MaximumShortDurationSeconds { get; set; } = 60;
    public int MaximumScriptGenerationAttempts { get; set; } = 3;
    public int ShortMinimumWordTolerance { get; set; }
    public int ShortMaximumWordTolerance { get; set; }
    public double MinimumDurationRatio { get; set; } = 0.75;
    public double MaximumDurationRatio { get; set; } = 1.25;
    public double MaximumAudioVideoDifferenceSeconds { get; set; } = 1.0;
    public long MinimumVideoFileSizeBytes { get; set; } = 10_000;
    public bool AllowLimitedValidationWithoutFfprobe { get; set; }

    public static BatchOptions FromEnvironment()
    {
        BatchOptions options = new();

        if (bool.TryParse(
                Environment.GetEnvironmentVariable("EX01_BATCH_CONTINUE_ON_FAILURE"),
                out bool continueOnFailure))
        {
            options.StopOnVideoFailure = !continueOnFailure;
        }

        if (double.TryParse(
                Environment.GetEnvironmentVariable("EX01_SHORT_MIN_DURATION_RATIO"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double minimumDurationRatio))
        {
            options.MinimumDurationRatio = Math.Clamp(minimumDurationRatio, 0.1, 1.0);
        }

        if (double.TryParse(
                Environment.GetEnvironmentVariable("EX01_SHORT_MAX_DURATION_RATIO"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double maximumDurationRatio))
        {
            options.MaximumDurationRatio = Math.Clamp(maximumDurationRatio, 1.0, 3.0);
        }

        if (double.TryParse(
                Environment.GetEnvironmentVariable("EX01_MAX_AUDIO_VIDEO_DIFFERENCE_SECONDS"),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double maximumAudioVideoDifferenceSeconds))
        {
            options.MaximumAudioVideoDifferenceSeconds = Math.Clamp(
                maximumAudioVideoDifferenceSeconds,
                0.1,
                10.0);
        }

        if (int.TryParse(
                Environment.GetEnvironmentVariable("EX01_BATCH_SHORT_SECONDS"),
                out int durationSeconds))
        {
            options.ShortDurationSeconds = Math.Clamp(durationSeconds, 15, 60);
        }

        if (bool.TryParse(
                Environment.GetEnvironmentVariable("EX01_ALLOW_LIMITED_VIDEO_VALIDATION"),
                out bool allowLimitedValidation))
        {
            options.AllowLimitedValidationWithoutFfprobe = allowLimitedValidation;
        }

        return options;
    }
}
