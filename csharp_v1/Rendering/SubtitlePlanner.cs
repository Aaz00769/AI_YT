using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AI_YOUTUBER.Models;

namespace AI_YOUTUBER.Rendering;

public static class SubtitlePlanner
{
    public static List<SubtitleCue> BuildCues(
        string cleanedScript,
        double audioDuration,
        int minimumWords,
        int maximumWords,
        string? audioPath = null)
    {
        if (minimumWords < 1 || maximumWords < minimumWords)
            throw new ArgumentOutOfRangeException(nameof(minimumWords));

        string[] words = Regex.Matches(cleanedScript, @"\S+")
            .Select(match => match.Value)
            .Where(token => token.Any(char.IsLetterOrDigit))
            .ToArray();

        if (words.Length == 0 || audioDuration <= 0)
            return new List<SubtitleCue>();

        List<string[]> phrases = SplitIntoPhrases(words, minimumWords, maximumWords);
        double[] wordBoundaries = BuildWordBoundaries(words, audioDuration, audioPath);
        List<SubtitleCue> cues = new();
        int wordCursor = 0;

        foreach (string[] phrase in phrases)
        {
            double start = Math.Max(0, wordBoundaries[wordCursor] - 0.04);
            wordCursor += phrase.Length;
            double end = Math.Min(audioDuration, wordBoundaries[wordCursor] + 0.08);
            cues.Add(new SubtitleCue(string.Join(" ", phrase), start, Math.Max(start + 0.05, end)));
        }

        // Do not let adjacent cues overlap: an overlap makes the later cue unreachable
        // because GetCueAtTime returns the first match.
        for (int i = 0; i < cues.Count - 1; i++)
        {
            if (cues[i].EndTime <= cues[i + 1].StartTime)
                continue;

            double boundary = (cues[i].EndTime + cues[i + 1].StartTime) / 2;
            cues[i] = cues[i] with { EndTime = boundary };
            cues[i + 1] = cues[i + 1] with { StartTime = boundary };
        }

        return cues;
    }

    private static double[] BuildWordBoundaries(
        string[] words,
        double audioDuration,
        string? audioPath)
    {
        double[] weights = words.Select(GetSpokenWeight).ToArray();
        double totalWeight = weights.Sum();
        List<double>? voicedTimes = TryReadVoicedTimes(audioPath);

        double Map(double fraction)
        {
            fraction = Math.Clamp(fraction, 0, 1);
            if (voicedTimes is null || voicedTimes.Count < 2)
                return fraction * audioDuration;

            double position = fraction * (voicedTimes.Count - 1);
            int lower = (int)Math.Floor(position);
            int upper = Math.Min(lower + 1, voicedTimes.Count - 1);
            return voicedTimes[lower] + (voicedTimes[upper] - voicedTimes[lower]) * (position - lower);
        }

        double[] boundaries = new double[words.Length + 1];
        double usedWeight = 0;
        boundaries[0] = Map(0);
        for (int i = 0; i < words.Length; i++)
        {
            usedWeight += weights[i];
            boundaries[i + 1] = Map(usedWeight / totalWeight);
        }

        return boundaries;
    }

    private static double GetSpokenWeight(string word)
    {
        int characters = word.Count(char.IsLetterOrDigit);
        return 0.7 + Math.Min(2.2, characters / 4.0);
    }

    private static List<double>? TryReadVoicedTimes(string? audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return null;

        try
        {
            byte[] bytes = File.ReadAllBytes(audioPath);
            if (bytes.Length < 44 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
                return null;

            int offset = 12;
            short format = 0, channels = 0, bits = 0;
            int sampleRate = 0, dataOffset = -1, dataSize = 0;
            while (offset <= bytes.Length - 8)
            {
                string id = Encoding.ASCII.GetString(bytes, offset, 4);
                int size = BitConverter.ToInt32(bytes, offset + 4);
                offset += 8;
                if (size < 0 || offset + size > bytes.Length)
                    return null;

                if (id == "fmt " && size >= 16)
                {
                    format = BitConverter.ToInt16(bytes, offset);
                    channels = BitConverter.ToInt16(bytes, offset + 2);
                    sampleRate = BitConverter.ToInt32(bytes, offset + 4);
                    bits = BitConverter.ToInt16(bytes, offset + 14);
                }
                else if (id == "data")
                {
                    dataOffset = offset;
                    dataSize = size;
                    break;
                }
                offset += size + size % 2;
            }

            if (format != 1 || channels < 1 || bits != 16 || sampleRate < 1 || dataOffset < 0)
                return null;

            const double frameSeconds = 0.01;
            int samplesPerFrame = Math.Max(1, (int)(sampleRate * frameSeconds));
            int sampleFrames = dataSize / (2 * channels);
            int frameCount = (int)Math.Ceiling(sampleFrames / (double)samplesPerFrame);
            double[] rms = new double[frameCount];

            for (int frame = 0; frame < frameCount; frame++)
            {
                int start = frame * samplesPerFrame;
                int end = Math.Min(start + samplesPerFrame, sampleFrames);
                double sum = 0;
                for (int sample = start; sample < end; sample++)
                {
                    double value = BitConverter.ToInt16(
                        bytes, dataOffset + sample * channels * 2) / 32768.0;
                    sum += value * value;
                }
                rms[frame] = Math.Sqrt(sum / Math.Max(1, end - start));
            }

            double[] sorted = rms.Order().ToArray();
            double noise = sorted[(int)((sorted.Length - 1) * 0.20)];
            double loud = sorted[(int)((sorted.Length - 1) * 0.90)];
            double threshold = noise + Math.Max(0.0015, (loud - noise) * 0.12);
            bool[] voiced = rms.Select(value => value >= threshold).ToArray();

            // Include quiet consonants around detected speech while retaining real pauses.
            bool[] expanded = (bool[])voiced.Clone();
            for (int i = 0; i < voiced.Length; i++)
            {
                if (!voiced[i]) continue;
                for (int j = Math.Max(0, i - 3); j <= Math.Min(voiced.Length - 1, i + 3); j++)
                    expanded[j] = true;
            }

            List<double> times = new();
            for (int i = 0; i < expanded.Length; i++)
            {
                if (expanded[i])
                    times.Add((i + 0.5) * frameSeconds);
            }
            return times.Count == 0 ? null : times;
        }
        catch
        {
            return null;
        }
    }

    public static SubtitleCue? GetCueAtTime(
        IReadOnlyList<SubtitleCue> cues,
        double timeSeconds)
    {
        for (int i = 0; i < cues.Count; i++)
        {
            SubtitleCue cue = cues[i];
            if (timeSeconds >= cue.StartTime && timeSeconds < cue.EndTime)
                return cue;
        }

        return null;
    }

    public static void SaveSrt(string path, IReadOnlyList<SubtitleCue> cues)
    {
        StringBuilder srt = new();

        for (int i = 0; i < cues.Count; i++)
        {
            SubtitleCue cue = cues[i];
            srt.AppendLine((i + 1).ToString(CultureInfo.InvariantCulture));
            srt.AppendLine($"{FormatSrtTime(cue.StartTime)} --> {FormatSrtTime(cue.EndTime)}");
            srt.AppendLine(cue.Text);
            srt.AppendLine();
        }

        File.WriteAllText(path, srt.ToString(), Encoding.UTF8);
    }

    private static List<string[]> SplitIntoPhrases(
        string[] words,
        int minimumWords,
        int maximumWords)
    {
        List<string[]> phrases = new();
        int cursor = 0;

        while (cursor < words.Length)
        {
            int remaining = words.Length - cursor;
            int take = Math.Min(maximumWords, remaining);
            int remainder = remaining - take;

            if (remainder > 0 && remainder < minimumWords)
                take -= minimumWords - remainder;

            take = Math.Max(1, take);
            phrases.Add(words[cursor..(cursor + take)]);
            cursor += take;
        }

        return phrases;
    }

    private static string FormatSrtTime(double seconds)
    {
        TimeSpan time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00},{time.Milliseconds:000}";
    }
}
