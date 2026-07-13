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
        minimumRatio = Math.Clamp(minimumRatio, 0.1, 1.0);
        maximumRatio = Math.Max(minimumRatio, maximumRatio);
        VoiceDurationValidationResult result = new()
        {
            ActualDurationSeconds = actualDurationSeconds,
            RequestedDurationSeconds = requestedDurationSeconds,
            MinimumAcceptedDurationSeconds = requestedDurationSeconds * minimumRatio,
            MaximumAcceptedDurationSeconds = requestedDurationSeconds * maximumRatio
        };

        if (double.IsNaN(actualDurationSeconds) || double.IsInfinity(actualDurationSeconds) ||
            actualDurationSeconds <= 0)
        {
            result.Errors.Add("Voice duration is zero, invalid, or unreadable.");
        }
        else if (actualDurationSeconds < result.MinimumAcceptedDurationSeconds ||
                 actualDurationSeconds > result.MaximumAcceptedDurationSeconds)
        {
            result.Errors.Add(
                $"Voice duration {actualDurationSeconds:F2}s is outside the accepted " +
                $"{result.MinimumAcceptedDurationSeconds:F2}-{result.MaximumAcceptedDurationSeconds:F2}s " +
                $"range for a {requestedDurationSeconds:F2}s Short.");
        }

        result.Success = result.Errors.Count == 0;
        Console.WriteLine(
            $"[VoiceDurationValidation] {(result.Success ? "Passed" : "Failed")}: " +
            $"actual={actualDurationSeconds:F2}s, expected=" +
            $"{result.MinimumAcceptedDurationSeconds:F2}-{result.MaximumAcceptedDurationSeconds:F2}s.");
        foreach (string error in result.Errors)
            Console.WriteLine($"[VoiceDurationValidation] Error: {error}");
        return result;
    }
}
