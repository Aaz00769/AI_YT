using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Functions.MEMORY;
using AI_YOUTUBER.Infrastructure;

namespace AI_YOUTUBER.ConsoleApp;

public sealed class StatusService(Ex01Settings settings, VideoMemory memory)
{
    public async Task ShowAsync()
    {
        DependencyChecker checker = new(settings);
        Task<DependencyStatus> ollama = checker.CheckOllamaAsync();
        Task<DependencyStatus> piper = checker.CheckPiperAsync();
        Task<DependencyStatus> comfy = checker.CheckComfyUiAsync();
        Task<DependencyStatus> searx = checker.CheckSearxngAsync();
        Task<DependencyStatus> ffmpeg = checker.CheckFfmpegAsync();
        Task<DependencyStatus> ffprobe = checker.CheckFfprobeAsync();
        int memoryCount = (await memory.LoadAllAsync()).Count;
        await Task.WhenAll(ollama, piper, comfy, searx, ffmpeg, ffprobe);

        Console.WriteLine();
        Console.WriteLine("EX_01 CONFIGURATION / STATUS");
        Console.WriteLine($"Ollama endpoint: {SafeEndpoint(settings.OllamaEndpoint)}");
        Console.WriteLine($"Ollama reachable: {Pass(await ollama)}");
        Console.WriteLine($"Configured Short script model: {settings.ShortScriptModel}");
        Console.WriteLine($"Configured long-form script model: {settings.LongScriptModel}");
        Console.WriteLine($"Configured memory model: {settings.MemoryModel}");
        Console.WriteLine($"Piper voice path: {settings.PiperVoicePath}");
        Console.WriteLine($"Piper runnable: {Pass(await piper)}");
        Console.WriteLine($"ComfyUI endpoint: {SafeEndpoint(settings.ComfyUiEndpoint)}");
        Console.WriteLine($"ComfyUI reachable: {Pass(await comfy)}");
        Console.WriteLine($"SearXNG endpoint: {SafeEndpoint(settings.SearxngEndpoint)}");
        Console.WriteLine($"SearXNG reachable: {Pass(await searx)}");
        Console.WriteLine($"FFmpeg available: {Pass(await ffmpeg)}");
        Console.WriteLine($"ffprobe available: {Pass(await ffprobe)}");
        Console.WriteLine($"Official memory count: {memoryCount}");
        Console.WriteLine($"Output directory: {settings.OutputDirectory}");
        Console.WriteLine("Current visual renderer: local SkiaSharp avatar (ComfyUI is not used by production yet)");
    }

    private static string Pass(DependencyStatus status) => status.Available ? "YES" : $"NO - {status.Detail}";

    private static string SafeEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            return "configured (invalid URL hidden)";
        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }
}
