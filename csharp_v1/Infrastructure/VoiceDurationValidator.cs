using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Infrastructure;

public static class VoiceDurationValidator
{
    public static VoiceDurationValidationResult Validate(
        double actualDurationSeconds,
        double requestedDurationSeconds,
        double minimumRatio = 0.75,
        double maximumRatio = 1.25)
    {
        VoiceDurationValidationResult result = new()
        {
            ActualDurationSeconds = actualDurationSeconds,
            RequestedDurationSeconds = requestedDurationSeconds,
            MinimumAcceptedDurationSeconds = requestedDurationSeconds * minimumRatio,
            MaximumAcceptedDurationSeconds = requestedDurationSeconds * maximumRatio
        };

        if (!double.IsFinite(actualDurationSeconds) || actualDurationSeconds <= 0)
            result.Errors.Add("Voice duration is zero, invalid, or unreadable.");
        else if (actualDurationSeconds < result.MinimumAcceptedDurationSeconds ||
                 actualDurationSeconds > result.MaximumAcceptedDurationSeconds)
            result.Errors.Add(
                $"Voice duration {actualDurationSeconds:F2}s is outside the accepted " +
                $"{result.MinimumAcceptedDurationSeconds:F2}–{result.MaximumAcceptedDurationSeconds:F2}s range.");

        result.Success = result.Errors.Count == 0;
        return result;
    }
}
