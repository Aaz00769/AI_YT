using System.Diagnostics;
using System.Text.Json;

namespace AI_YOUTUBER.Infrastructure;

public sealed class ExecutionTimingService
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly List<StageTiming> _stages = new();
    private readonly Dictionary<string, Stopwatch> _active = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset? _finishedUtc;

    public ExecutionTimingService(string mode, string outputPath)
    {
        Mode = mode;
        OutputPath = outputPath;
    }

    public string Mode { get; }
    public string OutputPath { get; set; }
    public string? LastFailedStage { get; private set; }
    public Exception? LastFailureException { get; private set; }

    public void StartStage(string name)
    {
        if (_active.ContainsKey(name))
            throw new InvalidOperationException($"Timing stage '{name}' is already running.");

        _active[name] = Stopwatch.StartNew();
    }

    public TimeSpan StopStage(string name)
    {
        if (!_active.Remove(name, out Stopwatch? stopwatch))
            throw new InvalidOperationException($"Timing stage '{name}' is not running.");

        stopwatch.Stop();
        StageTiming? existing = _stages.FirstOrDefault(stage => stage.Name == name);
        if (existing is null)
            _stages.Add(new StageTiming(name, stopwatch.Elapsed));
        else
            existing.Elapsed += stopwatch.Elapsed;

        Console.WriteLine($"[Timing] {name}: {FormatDuration(stopwatch.Elapsed)}");
        return stopwatch.Elapsed;
    }

    public T Measure<T>(string name, Func<T> operation)
    {
        StartStage(name);
        try
        {
            return operation();
        }
        catch (Exception ex)
        {
            LastFailedStage = name;
            LastFailureException = ex;
            Console.WriteLine($"[Timing] Stage failed: {name}");
            throw;
        }
        finally
        {
            StopStage(name);
        }
    }

    public void Measure(string name, Action operation) =>
        Measure(name, () =>
        {
            operation();
            return true;
        });

    public async Task<T> MeasureAsync<T>(string name, Func<Task<T>> operation)
    {
        StartStage(name);
        try
        {
            return await operation();
        }
        catch (Exception ex)
        {
            LastFailedStage = name;
            LastFailureException = ex;
            Console.WriteLine($"[Timing] Stage failed: {name}");
            throw;
        }
        finally
        {
            StopStage(name);
        }
    }

    public async Task MeasureAsync(string name, Func<Task> operation) =>
        await MeasureAsync(name, async () =>
        {
            await operation();
            return true;
        });

    public async Task CompleteAndSaveAsync(
        bool completedSuccessfully,
        string? failureStage = null,
        string? failureMessage = null)
    {
        foreach (string activeStage in _active.Keys.ToArray())
            StopStage(activeStage);

        if (_total.IsRunning)
            _total.Stop();
        _finishedUtc ??= DateTimeOffset.UtcNow;

        PrintSummary();

        double totalMilliseconds = _total.Elapsed.TotalMilliseconds;
        var document = new
        {
            mode = Mode,
            startUtc = _startedUtc,
            finishUtc = _finishedUtc,
            totalSeconds = _total.Elapsed.TotalSeconds,
            completedSuccessfully,
            failureStage,
            failureMessage,
            stages = _stages.Select(stage => new
            {
                name = stage.Name,
                elapsedMilliseconds = stage.Elapsed.TotalMilliseconds,
                percentageOfTotal = totalMilliseconds <= 0
                    ? 0
                    : stage.Elapsed.TotalMilliseconds / totalMilliseconds * 100
            })
        };

        string? directory = Path.GetDirectoryName(OutputPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        await File.WriteAllTextAsync(
            OutputPath,
            JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"[Timing] Metrics saved: {OutputPath}");
    }

    private void PrintSummary()
    {
        Console.WriteLine("\n=== EXECUTION TIME SUMMARY ===");
        foreach (StageTiming stage in _stages)
        {
            double percentage = _total.Elapsed.TotalMilliseconds <= 0
                ? 0
                : stage.Elapsed.TotalMilliseconds / _total.Elapsed.TotalMilliseconds * 100;
            Console.WriteLine($"{stage.Name,-28} {FormatDuration(stage.Elapsed),14}  {percentage,6:F2}%");
        }
        Console.WriteLine($"{"Total",-28} {FormatDuration(_total.Elapsed),14}  {100,6:F2}%");
    }

    public static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalSeconds < 1)
            return $"{duration.TotalMilliseconds:F0} ms";
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}.{duration.Milliseconds:000}";
    }

    private sealed class StageTiming(string name, TimeSpan elapsed)
    {
        public string Name { get; } = name;
        public TimeSpan Elapsed { get; set; } = elapsed;
    }
}

public static class ExecutionTimingContext
{
    private static readonly AsyncLocal<ExecutionTimingService?> CurrentTimer = new();

    public static ExecutionTimingService? Current
    {
        get => CurrentTimer.Value;
        set => CurrentTimer.Value = value;
    }
}
