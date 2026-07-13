using System.Text;
using System.Text.Json;

namespace AI_YOUTUBER.Infrastructure;

public static class AtomicJsonFile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static async Task WriteAsync<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        string temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            string json = JsonSerializer.Serialize(value, Options);
            await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static async Task<T> ReadAsync<T>(string path)
    {
        string json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<T>(json, Options)
            ?? throw new InvalidDataException($"JSON file was empty or invalid: {path}");
    }
}
