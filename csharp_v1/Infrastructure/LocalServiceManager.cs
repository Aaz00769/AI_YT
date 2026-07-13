using System.Diagnostics;
using System.Text;

namespace AI_YOUTUBER.Infrastructure;

public static class LocalServiceManager
{
    private const string DefaultSearxngUrl = "http://localhost:8080";
    private const string DefaultSearxngContainer = "ex01-search";
    private static readonly HttpClient HealthClient = new()
    {
        Timeout = TimeSpan.FromSeconds(3)
    };
    private static readonly SemaphoreSlim SearxngLock = new(1, 1);

    public static async Task EnsureSearxngRunningAsync()
    {
        string baseUrl = Environment.GetEnvironmentVariable("SEARXNG_URL")
            ?? DefaultSearxngUrl;

        // Always check the live service. A previously healthy container may have
        // stopped since the last search.
        if (await IsHttpServiceReadyAsync(baseUrl))
            return;

        await SearxngLock.WaitAsync();
        try
        {
            // Another caller may have started it while this caller waited.
            if (await IsHttpServiceReadyAsync(baseUrl))
                return;

            string container = Environment.GetEnvironmentVariable("SEARXNG_CONTAINER")
                ?? DefaultSearxngContainer;
            Console.WriteLine($"[Services] SearXNG is offline. Starting Docker container '{container}'...");

            // Docker may be usable without sudo when the user belongs to the docker group.
            CommandResult dockerResult = RunCommand("docker", new[] { "start", container });
            CommandResult? sudoResult = null;
            string? sudoFailureExplanation = null;

            if (!dockerResult.Succeeded)
            {
                if (Console.IsInputRedirected)
                {
                    sudoFailureExplanation =
                        "Sudo was not attempted because no interactive terminal is available. " +
                        "Start the container manually or run the application from a terminal.";
                }
                else
                {
                    Console.WriteLine(
                        "[Services] Normal Docker access failed. Sudo may now request your password.\n" +
                        "[Services] The password is read directly by sudo and is never stored, piped, or logged by this application.");
                    sudoResult = RunSudoCommand("docker", new[] { "start", container });
                    if (!sudoResult.Succeeded)
                    {
                        sudoFailureExplanation = sudoResult.TimedOut
                            ? "Sudo authentication or the Docker command timed out."
                            : "Sudo could not authenticate or could not start the container.";
                    }
                }
            }

            if (!dockerResult.Succeeded && sudoResult?.Succeeded != true)
                throw new InvalidOperationException(
                    BuildStartupError(container, dockerResult, sudoResult, sudoFailureExplanation));

            Stopwatch waitTimer = Stopwatch.StartNew();
            while (waitTimer.Elapsed < TimeSpan.FromSeconds(15))
            {
                TimeSpan healthTimeout = TimeSpan.FromSeconds(15) - waitTimer.Elapsed;
                if (healthTimeout > TimeSpan.FromSeconds(3))
                    healthTimeout = TimeSpan.FromSeconds(3);

                if (await IsHttpServiceReadyAsync(baseUrl, healthTimeout))
                {
                    Console.WriteLine("[Services] SearXNG is ready.");
                    return;
                }

                TimeSpan remaining = TimeSpan.FromSeconds(15) - waitTimer.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
            }

            throw new TimeoutException(
                $"SearXNG was started, but did not become available at {baseUrl} within 15 seconds.");
        }
        finally
        {
            SearxngLock.Release();
        }
    }

    public static CommandResult RunSudoCommand(
        string executable,
        IReadOnlyList<string> arguments)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("sudo commands are only supported on Unix-like systems.");

        List<string> sudoArguments = new() { executable };
        sudoArguments.AddRange(arguments);
        return RunCommand("sudo", sudoArguments, TimeSpan.FromMinutes(2));
    }

    private static CommandResult RunCommand(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        try
        {
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException($"Could not start '{executable}'.");

            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            bool exited = timeout is null
                ? WaitWithoutTimeout(process)
                : process.WaitForExit((int)timeout.Value.TotalMilliseconds);

            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }

            string stdout = stdoutTask.GetAwaiter().GetResult().Trim();
            string stderr = stderrTask.GetAwaiter().GetResult().Trim();
            return new CommandResult(exited ? process.ExitCode : -1, stdout, stderr, !exited);
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, "", ex.Message, false);
        }
    }

    private static bool WaitWithoutTimeout(Process process)
    {
        process.WaitForExit();
        return true;
    }

    private static string BuildStartupError(
        string container,
        CommandResult dockerResult,
        CommandResult? sudoResult,
        string? explanation)
    {
        StringBuilder message = new();
        message.AppendLine($"Could not start SearXNG container '{container}'.");
        AppendCommandFailure(message, "docker start", dockerResult);
        if (sudoResult is not null)
            AppendCommandFailure(message, "sudo docker start", sudoResult);
        if (!string.IsNullOrWhiteSpace(explanation))
            message.Append(explanation);
        return message.ToString().TrimEnd();
    }

    private static void AppendCommandFailure(
        StringBuilder message,
        string command,
        CommandResult result)
    {
        message.AppendLine($"{command} exit code: {result.ExitCode}");
        if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            message.AppendLine($"stdout: {result.StandardOutput}");
        if (!string.IsNullOrWhiteSpace(result.StandardError))
            message.AppendLine($"stderr: {result.StandardError}");
    }

    private static async Task<bool> IsHttpServiceReadyAsync(
        string baseUrl,
        TimeSpan? timeout = null)
    {
        try
        {
            using CancellationTokenSource? timeoutSource = timeout is null
                ? null
                : new CancellationTokenSource(timeout.Value);
            using HttpResponseMessage response = await HealthClient.GetAsync(
                baseUrl.TrimEnd('/') + "/",
                timeoutSource?.Token ?? CancellationToken.None);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }
}

public sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;
}
