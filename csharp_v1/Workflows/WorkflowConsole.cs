using AI_YOUTUBER.Infrastructure;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Workflows;

internal static class WorkflowConsole
{
    public static int ReadInteger(string prompt, int defaultValue, int minimum, int maximum)
    {
        Console.Write(prompt);
        string value = Console.ReadLine()?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (int.TryParse(value, out int parsed) && parsed >= minimum && parsed <= maximum)
            return parsed;
        Console.WriteLine($"Using {defaultValue}; enter a number from {minimum} to {maximum} next time.");
        return defaultValue;
    }

    public static bool AskYesNoDefaultNo(string prompt)
    {
        Console.Write(prompt);
        string answer = Console.ReadLine()?.Trim() ?? "";
        return answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
               answer.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public static void ShowScript(string heading, ScriptGenerationResult result, bool allowEdit)
    {
        Console.WriteLine();
        Console.WriteLine("--------------------------------");
        Console.WriteLine(heading);
        Console.WriteLine("--------------------------------");
        Console.WriteLine(result.Script);
        Console.WriteLine();
        Console.WriteLine($"Words: {result.Validation.WordCount}");
        Console.WriteLine($"Target: {result.Validation.MinimumWords}–{result.Validation.MaximumWords}");
        Console.WriteLine($"Validation: {(result.Validation.Success ? "passed" : "failed")}");
        foreach (string error in result.Validation.Errors)
            Console.WriteLine($"- {error}");
        Console.WriteLine();
        Console.WriteLine("A. Approve and continue");
        Console.WriteLine("R. Regenerate");
        if (allowEdit)
            Console.WriteLine("E. Edit instruction and regenerate");
        Console.WriteLine("C. Cancel and return to menu");
    }

    public static string Normalize(string? value) => (value ?? "").Trim().ToLowerInvariant();
}
