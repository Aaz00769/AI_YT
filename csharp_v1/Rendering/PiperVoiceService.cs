using AI_YOUTUBER.Configuration;
using AI_YOUTUBER.Infrastructure;

namespace AI_YOUTUBER.Rendering;

public sealed class PiperVoiceService(Ex01Settings settings)
{
    public async Task GenerateAsync(string script, string outputPath)
    {
        if (!File.Exists(settings.PiperExecutablePath))
            throw new FileNotFoundException("Piper executable was not found.", settings.PiperExecutablePath);
        if (!File.Exists(settings.PiperVoicePath))
            throw new FileNotFoundException("Piper voice model was not found.", settings.PiperVoicePath);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await ProcessRunner.EnsureSuccessAsync(
            settings.PiperExecutablePath,
            new[] { "--model", settings.PiperVoicePath, "--output_file", outputPath },
            script + Environment.NewLine,
            TimeSpan.FromMinutes(20));

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidOperationException("Piper completed without creating a usable voice file.");
    }
}
