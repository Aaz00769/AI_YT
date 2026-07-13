using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Infrastructure;

public sealed class VideoValidationService
{
    public async Task<VideoValidationResult> ValidateAsync(VideoValidationRequest request)
    {
        double minimumDuration = request.RequestedDurationSeconds * request.MinimumDurationRatio;
        double maximumDuration = request.RequestedDurationSeconds * request.MaximumDurationRatio;
        VideoValidationResult result = new()
        {
            RequestedDurationSeconds = request.RequestedDurationSeconds,
            MinimumAcceptedDurationSeconds = minimumDuration,
            MaximumAcceptedDurationSeconds = maximumDuration,
            ScriptValidationPassed = request.ScriptValidation?.Success == true,
            VoiceDurationValidationPassed = request.VoiceDurationValidation?.Success == true,
            AudioDurationSeconds = request.VoiceDurationValidation?.ActualDurationSeconds ?? 0
        };
        Console.WriteLine($"[VideoValidation] Validating: {request.VideoPath}");

        if (!result.ScriptValidationPassed)
            result.Errors.Add("Successful Short script validation evidence is missing.");
        else
            result.ChecksPassed.Add("Short script validation passed.");

        if (!result.VoiceDurationValidationPassed)
            result.Errors.Add("Successful voice-duration validation evidence is missing.");
        else
            result.ChecksPassed.Add("Voice duration validation passed.");

        if (!File.Exists(request.VideoPath))
        {
            result.Errors.Add("Final video file does not exist.");
            return Finish(result);
        }

        FileInfo file = new(request.VideoPath);
        result.FileSizeBytes = file.Length;
        if (file.Length < request.MinimumFileSizeBytes)
        {
            result.Errors.Add(
                $"Video file is too small ({file.Length} bytes; minimum is {request.MinimumFileSizeBytes}).");
        }
        else
        {
            result.ChecksPassed.Add($"Video file size is {file.Length} bytes.");
        }

        if (!File.Exists(request.ScriptPath) ||
            string.IsNullOrWhiteSpace(await File.ReadAllTextAsync(request.ScriptPath)))
            result.Errors.Add("Final script file is missing or empty.");
        else
            result.ChecksPassed.Add("Final script exists and is not empty.");

        result.FileHash = await CalculateFileHashAsync(request.VideoPath);
        if (request.CompletedVideoHashes.Contains(result.FileHash, StringComparer.OrdinalIgnoreCase))
            result.Errors.Add("Video is byte-for-byte identical to another completed video in this batch.");
        else
            result.ChecksPassed.Add("Video hash is unique within the completed batch outputs.");

        FfprobeResult probe;
        try
        {
            probe = await RunFfprobeAsync(request.VideoPath);
        }
        catch (Exception ex)
        {
            result.FullValidationPerformed = false;
            string warning = $"ffprobe validation was unavailable: {ex.Message}";
            result.Warnings.Add(warning);
            Console.WriteLine($"[VideoValidation] {warning}");

            if (request.AllowLimitedValidationWithoutFfprobe && result.Errors.Count == 0)
            {
                result.Success = true;
                result.ChecksPassed.Add(
                    "Limited validation accepted by configuration; media streams were not inspected.");
            }
            else
            {
                result.Errors.Add(
                    "Full validation is required before official memory can be saved.");
            }

            return Finish(result);
        }

        result.FullValidationPerformed = true;
        result.DurationSeconds = probe.DurationSeconds;
        result.Width = probe.Width;
        result.Height = probe.Height;
        result.HasVideo = probe.HasVideo;
        result.HasAudio = probe.HasAudio;
        result.AudioVideoDurationDifferenceSeconds = Math.Abs(
            result.AudioDurationSeconds - result.DurationSeconds);

        if (probe.DurationSeconds <= 0)
            result.Errors.Add("Video duration is zero or could not be read.");
        else if (probe.DurationSeconds < minimumDuration || probe.DurationSeconds > maximumDuration)
            result.Errors.Add(
                $"Video duration {probe.DurationSeconds:F2}s is outside the accepted " +
                $"{minimumDuration:F2}-{maximumDuration:F2}s range for the requested " +
                $"{request.RequestedDurationSeconds:F2}s Short.");
        else if (probe.DurationSeconds > request.AbsoluteMaximumDurationSeconds + 0.5)
            result.Errors.Add(
                $"Video duration {probe.DurationSeconds:F2}s exceeds the absolute " +
                $"{request.AbsoluteMaximumDurationSeconds}s Short limit.");
        else
            result.ChecksPassed.Add(
                $"Duration {probe.DurationSeconds:F2}s matches the requested generation job.");

        if (result.AudioDurationSeconds > 0 &&
            result.AudioVideoDurationDifferenceSeconds > request.MaximumAudioVideoDifferenceSeconds)
        {
            result.Errors.Add(
                $"Audio/video duration difference is {result.AudioVideoDurationDifferenceSeconds:F2}s; " +
                $"maximum is {request.MaximumAudioVideoDifferenceSeconds:F2}s.");
        }
        else if (result.AudioDurationSeconds > 0)
        {
            result.ChecksPassed.Add(
                $"Audio/video durations differ by only " +
                $"{result.AudioVideoDurationDifferenceSeconds:F2}s.");
        }

        if (!probe.HasVideo)
            result.Errors.Add("Video does not contain a video stream.");
        else
            result.ChecksPassed.Add("Video contains a video stream.");

        if (probe.Width <= 0 || probe.Height <= 0)
        {
            result.Errors.Add("Video width or height could not be read.");
        }
        else
        {
            double aspect = probe.Height / (double)probe.Width;
            if (probe.Height <= probe.Width || aspect < 1.2 || aspect > 2.3)
            {
                result.Errors.Add(
                    $"Video dimensions {probe.Width}x{probe.Height} are not an accepted vertical Short ratio.");
            }
            else
            {
                result.ChecksPassed.Add(
                    $"Video dimensions are vertical at {probe.Width}x{probe.Height}.");
            }
        }

        if (!probe.HasAudio)
            result.Errors.Add("Video does not contain an audio stream.");
        else
            result.ChecksPassed.Add("Video contains an audio stream.");

        result.Success = result.Errors.Count == 0;
        return Finish(result);
    }

    private static VideoValidationResult Finish(VideoValidationResult result)
    {
        if (result.Success)
        {
            string mode = result.FullValidationPerformed ? "full" : "limited";
            Console.WriteLine($"[VideoValidation] Video passed {mode} validation.");
        }
        else
        {
            Console.WriteLine("[VideoValidation] Video failed validation:");
            foreach (string error in result.Errors)
                Console.WriteLine($"[VideoValidation] - {error}");
        }

        return result;
    }

    private static async Task<string> CalculateFileHashAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static async Task<FfprobeResult> RunFfprobeAsync(string videoPath)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "ffprobe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_entries");
        startInfo.ArgumentList.Add("format=duration:stream=codec_type,width,height");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add(videoPath);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start ffprobe.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("ffprobe did not finish within 30 seconds.");
        }

        string stdout = await stdoutTask;
        string stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe failed: {stderr.Trim()}");

        using JsonDocument document = JsonDocument.Parse(stdout);
        JsonElement root = document.RootElement;
        double duration = 0;
        if (root.TryGetProperty("format", out JsonElement format) &&
            format.TryGetProperty("duration", out JsonElement durationElement))
        {
            string rawDuration = durationElement.ValueKind == JsonValueKind.String
                ? durationElement.GetString() ?? ""
                : durationElement.GetRawText();
            double.TryParse(rawDuration, NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
        }

        int width = 0;
        int height = 0;
        bool hasVideo = false;
        bool hasAudio = false;
        if (root.TryGetProperty("streams", out JsonElement streams))
        {
            foreach (JsonElement stream in streams.EnumerateArray())
            {
                string codecType = stream.TryGetProperty("codec_type", out JsonElement codecTypeElement)
                    ? codecTypeElement.GetString() ?? ""
                    : "";
                if (codecType.Equals("audio", StringComparison.OrdinalIgnoreCase))
                    hasAudio = true;
                if (!codecType.Equals("video", StringComparison.OrdinalIgnoreCase))
                    continue;

                hasVideo = true;

                width = stream.TryGetProperty("width", out JsonElement widthElement)
                    ? widthElement.GetInt32()
                    : 0;
                height = stream.TryGetProperty("height", out JsonElement heightElement)
                    ? heightElement.GetInt32()
                    : 0;
            }
        }

        return new FfprobeResult(duration, width, height, hasVideo, hasAudio);
    }

    private sealed record FfprobeResult(
        double DurationSeconds,
        int Width,
        int Height,
        bool HasVideo,
        bool HasAudio);
}
