namespace AI_YOUTUBER.Configuration;

public sealed class Ex01Settings
{
    public required string ProjectDirectory { get; init; }
    public required string Ex01Directory { get; init; }
    public required string OutputDirectory { get; init; }
    public required string MemoryDirectory { get; init; }
    public required string OllamaEndpoint { get; init; }
    public required string ShortScriptModel { get; init; }
    public required string LongScriptModel { get; init; }
    public required string MemoryModel { get; init; }
    public required string PiperExecutablePath { get; init; }
    public required string PiperVoicePath { get; init; }
    public required string ComfyUiEndpoint { get; init; }
    public required string SearxngEndpoint { get; init; }

    public string ShortsOutputDirectory => Path.Combine(OutputDirectory, "shorts");
    public string LongFormOutputDirectory => Path.Combine(OutputDirectory, "long-form");
    public string PreviewOutputDirectory => Path.Combine(OutputDirectory, "previews");
    public string OfficialVideoMemoryDirectory => Path.Combine(MemoryDirectory, "videos");
    public string ChannelStatePath => Path.Combine(MemoryDirectory, "channel_state.json");

    public static Ex01Settings Load()
    {
        string projectDirectory = FindProjectDirectory();
        string ex01Directory = Directory.GetParent(projectDirectory)?.FullName
            ?? throw new InvalidOperationException("Could not find the EX_01 directory.");

        return new Ex01Settings
        {
            ProjectDirectory = projectDirectory,
            Ex01Directory = ex01Directory,
            OutputDirectory = ReadPath("EX01_OUTPUT_DIR", Path.Combine(ex01Directory, "output")),
            MemoryDirectory = ReadPath("EX01_MEMORY_DIR", Path.Combine(projectDirectory, "memory")),
            OllamaEndpoint = ReadValue("EX01_OLLAMA_URL", "http://localhost:11434").TrimEnd('/'),
            ShortScriptModel = ReadValue("EX01_SHORT_SCRIPT_MODEL", "qwen3:14b"),
            LongScriptModel = ReadValue("EX01_LONG_SCRIPT_MODEL", "mistral-small3.2:24b"),
            MemoryModel = ReadValue("EX01_MEMORY_MODEL", "qwen3:8b"),
            PiperExecutablePath = ReadPath(
                "EX01_PIPER_PATH",
                Path.Combine(projectDirectory, "tts", ".venv", "bin", "piper")),
            PiperVoicePath = ReadPath(
                "EX01_PIPER_VOICE",
                Path.Combine(projectDirectory, "tts", "voices", "en_US-lessac-medium.onnx")),
            ComfyUiEndpoint = ReadValue("EX01_COMFYUI_URL", "http://localhost:8188").TrimEnd('/'),
            SearxngEndpoint = ReadValue("EX01_SEARXNG_URL", "http://localhost:8080").TrimEnd('/')
        };
    }

    private static string FindProjectDirectory()
    {
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            string directProject = Path.Combine(directory.FullName, "csharp_v1.csproj");
            if (File.Exists(directProject))
                return directory.FullName;

            string nestedProject = Path.Combine(directory.FullName, "EX_01", "csharp_v1", "csharp_v1.csproj");
            if (File.Exists(nestedProject))
                return Path.GetDirectoryName(nestedProject)!;

            directory = directory.Parent;
        }

        directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "csharp_v1.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate csharp_v1.csproj.");
    }

    private static string ReadValue(string name, string fallback) =>
        string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))
            ? fallback
            : Environment.GetEnvironmentVariable(name)!.Trim();

    private static string ReadPath(string name, string fallback) =>
        Path.GetFullPath(ReadValue(name, fallback));
}
