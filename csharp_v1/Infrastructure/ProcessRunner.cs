using System.Diagnostics;

namespace AI_YOUTUBER.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? standardInput = null,
        TimeSpan? timeout = null)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = standardInput is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {fileName}.");
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        using CancellationTokenSource cancellation = new(timeout ?? TimeSpan.FromMinutes(30));
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} did not finish before the timeout.");
        }

        return new ProcessResult(process.ExitCode, await stdout, await stderr);
    }

    public static async Task EnsureSuccessAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? standardInput = null,
        TimeSpan? timeout = null)
    {
        ProcessResult result = await RunAsync(fileName, arguments, standardInput, timeout);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }
    }

    public static async Task<bool> IsAvailableAsync(string fileName)
    {
        try
        {
            ProcessResult result = await RunAsync(
                fileName,
                new[] { "-version" },
                timeout: TimeSpan.FromSeconds(5));
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
