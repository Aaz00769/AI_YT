using AI_YOUTUBER.Configuration;

namespace AI_YOUTUBER.Infrastructure;

public sealed record DependencyStatus(string Name, bool Available, string Detail)
{
    public string Summary => $"{Name}: {(Available ? "PASS" : "FAIL")}" +
                             (string.IsNullOrWhiteSpace(Detail) ? "" : $" - {Detail}");
}

public sealed class DependencyChecker(Ex01Settings settings)
{
    public async Task<IReadOnlyList<DependencyStatus>> CheckAllAsync()
    {
        Task<DependencyStatus>[] checks =
        {
            CheckOllamaAsync(),
            CheckPiperAsync(),
            CheckComfyUiAsync(),
            CheckSearxngAsync(),
            CheckExecutableAsync("FFmpeg", "ffmpeg"),
            CheckExecutableAsync("ffprobe", "ffprobe")
        };
        return await Task.WhenAll(checks);
    }

    public Task<DependencyStatus> CheckOllamaAsync() =>
        CheckHttpAsync("Ollama", $"{settings.OllamaEndpoint}/api/tags");

    public async Task<DependencyStatus> CheckPiperAsync()
    {
        if (!File.Exists(settings.PiperExecutablePath))
            return new DependencyStatus("Piper", false, "executable not found");
        if (!File.Exists(settings.PiperVoicePath))
            return new DependencyStatus("Piper", false, "voice model not found");
        try
        {
            ProcessResult result = await ProcessRunner.RunAsync(
                settings.PiperExecutablePath,
                new[] { "--help" },
                timeout: TimeSpan.FromSeconds(10));
            return new DependencyStatus(
                "Piper",
                result.ExitCode == 0,
                result.ExitCode == 0 ? "runnable; voice model found" : "executable failed to start cleanly");
        }
        catch (Exception exception)
        {
            return new DependencyStatus("Piper", false, ShortError(exception.Message));
        }
    }

    public Task<DependencyStatus> CheckComfyUiAsync() =>
        CheckHttpAsync("ComfyUI", $"{settings.ComfyUiEndpoint}/system_stats");

    public Task<DependencyStatus> CheckSearxngAsync() =>
        CheckHttpAsync("SearXNG", settings.SearxngEndpoint);

    public Task<DependencyStatus> CheckFfmpegAsync() => CheckExecutableAsync("FFmpeg", "ffmpeg");
    public Task<DependencyStatus> CheckFfprobeAsync() => CheckExecutableAsync("ffprobe", "ffprobe");

    private static async Task<DependencyStatus> CheckExecutableAsync(string name, string executable)
    {
        try
        {
            bool available = await ProcessRunner.IsAvailableAsync(executable);
            return new DependencyStatus(name, available, available ? "available" : "not found or not runnable");
        }
        catch (Exception exception)
        {
            return new DependencyStatus(name, false, ShortError(exception.Message));
        }
    }

    private static async Task<DependencyStatus> CheckHttpAsync(string name, string url)
    {
        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(4) };
            using HttpResponseMessage response = await client.GetAsync(url);
            bool reachable = (int)response.StatusCode < 500;
            return new DependencyStatus(
                name,
                reachable,
                reachable ? $"reachable ({(int)response.StatusCode})" : $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception exception)
        {
            return new DependencyStatus(name, false, ShortError(exception.Message));
        }
    }

    private static string ShortError(string message)
    {
        string oneLine = string.Join(" ", message.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 100 ? oneLine : oneLine[..100] + "…";
    }
}
