using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Infrastructure;

public sealed class VideoValidationService
{
    public async Task<VideoValidationResult> ValidateAsync(VideoValidationRequest request)
    {
        VideoValidationResult result = new()
        {
            RequestedDurationSeconds = request.RequestedDurationSeconds,
            MinimumAcceptedDurationSeconds = request.RequestedDurationSeconds * request.MinimumDurationRatio,
            MaximumAcceptedDurationSeconds = request.RequestedDurationSeconds * request.MaximumDurationRatio,
            ScriptValidationPassed = request.ScriptValidation?.Success == true,
            VoiceDurationValidationPassed = request.VoiceDurationValidation?.Success == true,
            AudioDurationSeconds = request.VoiceDurationValidation?.ActualDurationSeconds ?? 0
        };

        if (!result.ScriptValidationPassed)
            result.Errors.Add("Successful script-validation evidence is missing.");
        else
            result.ChecksPassed.Add("Script validation passed.");

        if (!result.VoiceDurationValidationPassed)
            result.Errors.Add("Successful voice-duration validation evidence is missing.");
        else
            result.ChecksPassed.Add("Voice-duration validation passed.");

        if (!File.Exists(request.ScriptPath) ||
            string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(request.ScriptPath)))
            result.Errors.Add("Final script file is missing or empty.");
        else
            result.ChecksPassed.Add("Final script exists and is not empty.");

        if (!File.Exists(request.VideoPath))
        {
            result.Errors.Add("Final video file does not exist.");
            return result;
        }

        FileInfo videoFile = new(request.VideoPath);
        result.FileSizeBytes = videoFile.Length;
        if (videoFile.Length < request.MinimumFileSizeBytes)
            result.Errors.Add($"Video is too small ({videoFile.Length} bytes)." );
        else
            result.ChecksPassed.Add($"Video file size is {videoFile.Length} bytes.");

        await using (FileStream stream = File.OpenRead(request.VideoPath))
            result.FileHash = Convert.ToHexString(await SHA256.HashDataAsync(stream));

        try
        {
            MediaProbe probe = await ProbeAsync(request.VideoPath);
            result.FullValidationPerformed = true;
            result.DurationSeconds = probe.DurationSeconds;
            result.Width = probe.Width;
            result.Height = probe.Height;
            result.HasVideo = probe.HasVideo;
            result.HasAudio = probe.HasAudio;
            result.AudioVideoDurationDifferenceSeconds = Math.Abs(result.AudioDurationSeconds - probe.DurationSeconds);

            if (probe.DurationSeconds < result.MinimumAcceptedDurationSeconds ||
                probe.DurationSeconds > result.MaximumAcceptedDurationSeconds)
                result.Errors.Add(
                    $"Video duration {probe.DurationSeconds:F2}s is outside the accepted " +
                    $"{result.MinimumAcceptedDurationSeconds:F2}–{result.MaximumAcceptedDurationSeconds:F2}s range.");
            else
                result.ChecksPassed.Add($"Duration {probe.DurationSeconds:F2}s matches the requested job.");

            if (result.AudioDurationSeconds > 0 &&
                result.AudioVideoDurationDifferenceSeconds > request.MaximumAudioVideoDifferenceSeconds)
                result.Errors.Add(
                    $"Audio/video durations differ by {result.AudioVideoDurationDifferenceSeconds:F2}s.");

            if (!probe.HasVideo)
                result.Errors.Add("Video stream is missing.");
            if (!probe.HasAudio)
                result.Errors.Add("Audio stream is missing.");

            bool orientationMatches = request.Orientation == VideoOrientation.Portrait
                ? probe.Height > probe.Width && probe.Height / (double)Math.Max(1, probe.Width) is >= 1.2 and <= 2.3
                : probe.Width > probe.Height && probe.Width / (double)Math.Max(1, probe.Height) is >= 1.2 and <= 2.3;
            if (!orientationMatches)
                result.Errors.Add(
                    $"Video dimensions {probe.Width}x{probe.Height} do not match {request.Orientation.ToString().ToLowerInvariant()} output.");
            else
                result.ChecksPassed.Add($"Video dimensions are {probe.Width}x{probe.Height}.");
        }
        catch (Exception exception)
        {
            result.FullValidationPerformed = false;
            result.Errors.Add($"Full ffprobe validation failed: {exception.Message}");
        }

        result.Success = result.Errors.Count == 0 && result.FullValidationPerformed;
        return result;
    }

    public static async Task<double> GetDurationAsync(string mediaPath)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "ffprobe",
            new[]
            {
                "-v", "error", "-show_entries", "format=duration",
                "-of", "default=nw=1:nk=1", mediaPath
            },
            timeout: TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0 ||
            !double.TryParse(result.StandardOutput.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double duration))
            throw new InvalidOperationException($"ffprobe could not read duration: {result.StandardError.Trim()}");
        return duration;
    }

    private static async Task<MediaProbe> ProbeAsync(string videoPath)
    {
        ProcessResult result = await ProcessRunner.RunAsync(
            "ffprobe",
            new[]
            {
                "-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height",
                "-of", "json", videoPath
            },
            timeout: TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
            throw new InvalidOperationException(result.StandardError.Trim());

        using JsonDocument document = JsonDocument.Parse(result.StandardOutput);
        JsonElement root = document.RootElement;
        double duration = 0;
        if (root.TryGetProperty("format", out JsonElement format) &&
            format.TryGetProperty("duration", out JsonElement durationElement))
        {
            string raw = durationElement.ValueKind == JsonValueKind.String
                ? durationElement.GetString() ?? ""
                : durationElement.GetRawText();
            double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        }

        int width = 0;
        int height = 0;
        bool hasVideo = false;
        bool hasAudio = false;
        if (root.TryGetProperty("streams", out JsonElement streams))
        {
            foreach (JsonElement stream in streams.EnumerateArray())
            {
                string type = stream.TryGetProperty("codec_type", out JsonElement value)
                    ? value.GetString() ?? ""
                    : "";
                if (type == "audio")
                    hasAudio = true;
                if (type != "video")
                    continue;
                hasVideo = true;
                width = stream.TryGetProperty("width", out JsonElement widthValue) ? widthValue.GetInt32() : 0;
                height = stream.TryGetProperty("height", out JsonElement heightValue) ? heightValue.GetInt32() : 0;
            }
        }

        return new MediaProbe(duration, width, height, hasVideo, hasAudio);
    }

    private sealed record MediaProbe(double DurationSeconds, int Width, int Height, bool HasVideo, bool HasAudio);
}
