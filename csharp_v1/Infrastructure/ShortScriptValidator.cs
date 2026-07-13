using System.Text.RegularExpressions;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Infrastructure;

public static class ShortScriptValidator
{
    private static readonly Regex SpokenWordPattern = new(
        @"[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ThinkingOrFormattingPattern = new(
        @"<\s*/?\s*think\b|/no_think\b|```|\*\*|__|^\s*#{1,6}\s|^\s*[-*>]\s+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex ModelCommentaryPattern = new(
        @"\b(here(?:'s| is) (?:the|your) (?:script|narration)|as an ai(?: language model)?|narration\s*:|script\s*:|word count\s*:|prompt instructions?\s*:|i (?:cannot|can't) comply)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex PlaceholderPattern = new(
        @"\b(?:todo|tbd|lorem ipsum|placeholder|insert (?:text|joke|hook|topic) here|your (?:text|topic) here)\b|\[(?:insert|placeholder|todo|tbd)[^\]]*\]|\{\{[^}]+\}\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex JsonWrapperPattern = new(
        "^\\s*[\\[{].*[\\]}]\\s*$|\\\"(?:script|narration|response)\\\"\\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex TerminalPunctuationPattern = new(
        "[.!?…][\\\"'”’\\)\\]]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DanglingEndingPattern = new(
        "[-/:([{]\\s*$|[-/:([{]\\s*[.!?…][\\\"'”’\\)\\]]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FragmentEndingPattern = new(
        @"\b(?:and|or|but|because|so|with|without|to|from|on|at|for|the|a|an|this|that|my|your|our)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static (int MinimumWords, int MaximumWords) GetShortWordRange(int targetSeconds)
    {
        targetSeconds = Math.Clamp(targetSeconds, 15, 60);
        return (
            (int)Math.Ceiling(targetSeconds * 2.2),
            (int)Math.Floor(targetSeconds * 2.7));
    }

    public static (int MinimumWords, int MaximumWords) GetLongFormWordRange(int targetMinutes)
    {
        targetMinutes = Math.Clamp(targetMinutes, 1, 30);
        return (targetMinutes * 140, targetMinutes * 170);
    }

    public static int CountSpokenWords(string? script) =>
        string.IsNullOrWhiteSpace(script) ? 0 : SpokenWordPattern.Matches(script).Count;

    public static ScriptValidationResult Validate(
        string? script,
        int minimumWords,
        int maximumWords,
        OllamaGenerationResult? generation = null)
    {
        string value = script?.Trim() ?? "";
        ScriptValidationResult result = new()
        {
            MinimumWords = minimumWords,
            MaximumWords = maximumWords,
            WordCount = CountSpokenWords(value),
            ReachedOutputTokenLimit = generation?.ReachedOutputTokenLimit == true
        };

        if (string.IsNullOrWhiteSpace(value))
            result.Errors.Add("Script is empty.");

        if (result.WordCount < minimumWords)
            result.Errors.Add($"Script is too short: {result.WordCount} spoken words; minimum is {minimumWords}.");
        else if (result.WordCount > maximumWords)
            result.Errors.Add($"Script is too long: {result.WordCount} spoken words; maximum is {maximumWords}.");

        if (generation is not null && !generation.Completed)
        {
            result.AppearsTruncated = true;
            result.Errors.Add("Ollama streaming ended without a completed generation response.");
        }

        if (result.ReachedOutputTokenLimit)
        {
            result.AppearsTruncated = true;
            result.Errors.Add(
                $"Ollama reached the output-token limit ({generation!.OutputTokenCount}/{generation.MaximumOutputTokens}).");
        }

        if (!string.IsNullOrWhiteSpace(value))
        {
            if (!TerminalPunctuationPattern.IsMatch(value))
            {
                result.AppearsTruncated = true;
                result.Errors.Add("Script does not end with reasonable terminal punctuation.");
            }

            if (DanglingEndingPattern.IsMatch(value))
            {
                result.AppearsTruncated = true;
                result.Errors.Add("Script ends with dangling punctuation or an unfinished delimiter.");
            }

            string ending = Regex.Replace(value, "[.!?…\\\"'”’\\)\\]]+$", "").TrimEnd();
            if (FragmentEndingPattern.IsMatch(ending))
            {
                result.AppearsTruncated = true;
                result.Errors.Add("Script appears to end on an incomplete sentence fragment.");
            }

            if (HasUnbalancedBrackets(value))
            {
                result.AppearsTruncated = true;
                result.Errors.Add("Script contains an unclosed bracket or parenthesis.");
            }

            if (ThinkingOrFormattingPattern.IsMatch(value))
                result.Errors.Add("Script contains thinking tags or Markdown formatting.");
            if (JsonWrapperPattern.IsMatch(value))
                result.Errors.Add("Script contains a JSON wrapper instead of narration only.");
            if (ModelCommentaryPattern.IsMatch(value))
                result.Errors.Add("Script contains prompt instructions, labels, or model commentary.");
            if (PlaceholderPattern.IsMatch(value))
                result.Errors.Add("Script contains placeholder text.");
            if (!Regex.IsMatch(value, @"[\p{L}]", RegexOptions.CultureInvariant) || result.WordCount < 3)
                result.Errors.Add("Script is not complete, speakable prose.");
        }

        result.Success = result.Errors.Count == 0;
        return result;
    }

    public static string Describe(ScriptValidationResult result) =>
        result.Success
            ? $"passed ({result.WordCount} words; target {result.MinimumWords}–{result.MaximumWords})"
            : $"failed ({string.Join(" ", result.Errors)})";

    private static bool HasUnbalancedBrackets(string value)
    {
        Dictionary<char, char> pairs = new() { [')'] = '(', [']'] = '[', ['}'] = '{' };
        Stack<char> openings = new();
        foreach (char character in value)
        {
            if (character is '(' or '[' or '{')
                openings.Push(character);
            else if (pairs.TryGetValue(character, out char opening) &&
                     (openings.Count == 0 || openings.Pop() != opening))
                return true;
        }

        return openings.Count != 0;
    }
}
