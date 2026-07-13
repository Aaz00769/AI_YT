using System.Text;
using AI_YOUTUBER.Functions.EMOTION;
using AI_YOUTUBER.Functions.VISUAL;
using AI_YOUTUBER.Models;
using SkiaSharp;
using AI_YOUTUBER.Infrastructure;

namespace AI_YOUTUBER.Rendering;

public static class ShortRenderer
{
    public const int Width = 1080;
    public const int Height = 1920;
    public const int FramesPerSecond = 30;

    public static void Render(
        string framesDirectory,
        string audioPath,
        string videoPath,
        double audioDuration,
        string cleanedScript,
        IReadOnlyList<EmotionTimelineEntry> emotionTimeline,
        IReadOnlyList<VisualBeatTimelineEntry> visualBeats,
        ExecutionTimingService timing,
        Action<string, string[]> runProcess,
        Action? onFramesGenerated = null)
    {
        double videoDuration = audioDuration + 0.35;
        timing.Measure("Short frame generation", () =>
        {
            Directory.CreateDirectory(framesDirectory);

            foreach (string file in Directory.GetFiles(framesDirectory, "frame_*.png"))
                File.Delete(file);

            List<SubtitleCue> subtitles = SubtitlePlanner.BuildCues(
                cleanedScript,
                audioDuration,
                minimumWords: 2,
                maximumWords: 5,
                audioPath: audioPath);
            bool[] mouthFrames = AnalyzeMouthFrames(audioPath, audioDuration, FramesPerSecond);
            int totalFrames = (int)Math.Ceiling(videoDuration * FramesPerSecond);

            for (int frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                double timeSeconds = frameIndex / (double)FramesPerSecond;
                bool mouthOpen = frameIndex < mouthFrames.Length && mouthFrames[frameIndex];
                EmotionState emotion = EmotionTimelinePlanner.GetEmotionAtTime(emotionTimeline, timeSeconds);
                SubtitleCue? subtitle = SubtitlePlanner.GetCueAtTime(subtitles, timeSeconds);
                VisualBeatTimelineEntry? visualBeat = VisualBeatPlanner.GetBeatAtTime(
                    visualBeats,
                    timeSeconds);
                VisualBeatFrameState beatState = VisualBeatPlanner.Sample(
                    visualBeat,
                    timeSeconds,
                    VideoMode.Short);

                using SKBitmap bitmap = DrawFrame(
                    frameIndex,
                    mouthOpen,
                    emotion,
                    subtitle?.Text,
                    beatState);
                using SKImage image = SKImage.FromBitmap(bitmap);
                using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);
                File.WriteAllBytes(Path.Combine(framesDirectory, $"frame_{frameIndex:00000}.png"), data.ToArray());
            }
        });

        onFramesGenerated?.Invoke();

        timing.Measure("Short video encoding", () => runProcess("ffmpeg", new[]
        {
            "-y",
            "-framerate", FramesPerSecond.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", Path.Combine(framesDirectory, "frame_%05d.png"),
            "-i", audioPath,
            "-t", videoDuration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-shortest",
            videoPath
        }));
    }

    private static SKBitmap DrawFrame(
        int frameIndex,
        bool mouthOpen,
        EmotionState emotion,
        string? subtitle,
        VisualBeatFrameState beatState)
    {
        SKBitmap bitmap = new(Width, Height);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(new SKColor(4, 9, 7));

        DrawBackground(canvas, emotion, beatState.BackgroundBrightness);

        int motionFrame = beatState.FreezeEmotionMotion ? 0 : frameIndex;
        float offsetX = beatState.FreezeEmotionMotion ? 0 : GetHorizontalOffset(emotion, motionFrame);
        float offsetY = beatState.FreezeEmotionMotion ? 0 : GetVerticalOffset(emotion, motionFrame);
        float rotation = beatState.FreezeEmotionMotion ? 0 : GetRotation(emotion, motionFrame);
        byte intensity = GetGlowIntensity(emotion);

        using SKPaint glowPaint = new()
        {
            Color = new SKColor(
                0,
                255,
                120,
                (byte)Math.Clamp(
                    (int)Math.Round(Math.Min(150, intensity / 2) * beatState.GlowMultiplier),
                    0,
                    210)),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 58)
        };
        canvas.DrawRoundRect(new SKRect(135, 405, 945, 1125), 95, 95, glowPaint);

        canvas.Save();
        canvas.Translate(
            540 + offsetX + (float)beatState.OffsetX,
            765 + offsetY + (float)beatState.OffsetY);
        canvas.RotateDegrees(rotation + (float)beatState.RotationDegrees);
        float beatScale = (float)Math.Clamp(beatState.Scale, 0.96, 1.09);
        canvas.Scale(beatScale, beatScale);
        canvas.Translate(-540, -765);

        using SKPaint headPaint = new() { Color = new SKColor(16, 27, 22), IsAntialias = true };
        using SKPaint outlinePaint = new()
        {
            Color = new SKColor(0, 255, 120),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 12,
            IsAntialias = true
        };
        SKRect head = new(120, 390, 960, 1140);
        canvas.DrawRoundRect(head, 92, 92, headPaint);
        canvas.DrawRoundRect(head, 92, 92, outlinePaint);

        DrawEyes(canvas, emotion, intensity);
        DrawBrows(canvas, emotion, intensity, motionFrame);
        DrawMouth(canvas, emotion, mouthOpen, intensity);
        canvas.Restore();

        DrawTitleAndStatus(canvas, emotion);
        DrawBeatOverlay(canvas, beatState, frameIndex);

        if (!string.IsNullOrWhiteSpace(subtitle))
            DrawSubtitle(canvas, subtitle);

        return bitmap;
    }

    private static void DrawBackground(
        SKCanvas canvas,
        EmotionState emotion,
        double brightness)
    {
        using SKTypeface typeface = SKTypeface.FromFamilyName("DejaVu Sans Mono");
        using SKFont font = new(typeface, 25);
        using SKPaint paint = new()
        {
            Color = new SKColor(15, 68, 40),
            IsAntialias = true
        };

        string text = $"> EX_01 // VERTICAL MODE // {emotion.ToString().ToUpperInvariant()}";
        for (int y = 70; y < Height; y += 74)
        {
            if (y >= 1250 && y <= 1600)
                continue;

            canvas.DrawText(text, 34, y, SKTextAlign.Left, font, paint);
        }

        using SKPaint subtitleZone = new()
        {
            Color = new SKColor(4, 9, 7, 175),
            IsAntialias = true
        };
        canvas.DrawRoundRect(new SKRect(45, 1260, 1035, 1585), 36, 36, subtitleZone);

        if (brightness < 0.999)
        {
            byte alpha = (byte)Math.Clamp(
                (int)Math.Round((1 - brightness) * 210),
                0,
                95);
            using SKPaint dim = new() { Color = new SKColor(0, 0, 0, alpha) };
            canvas.DrawRect(new SKRect(0, 0, Width, Height), dim);
        }
    }

    private static void DrawBeatOverlay(
        SKCanvas canvas,
        VisualBeatFrameState beatState,
        int frameIndex)
    {
        if (beatState.ShowGlitch)
        {
            using SKPaint glitch = new()
            {
                Color = new SKColor(0, 255, 120, 165),
                StrokeWidth = 6,
                IsAntialias = false
            };

            for (int line = 0; line < 6; line++)
            {
                float y = 390 + ((frameIndex * 47 + line * 151) % 760);
                float x = 80 + ((frameIndex * 31 + line * 83) % 360);
                canvas.DrawLine(x, y, Math.Min(1000, x + 300 + line * 35), y, glitch);
            }
        }

        if (beatState.ShowStatusWarning)
        {
            using SKTypeface bold = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold);
            using SKFont font = new(bold, 42);
            using SKPaint box = new() { Color = new SKColor(70, 28, 0, 220), IsAntialias = true };
            using SKPaint text = new() { Color = new SKColor(255, 190, 45), IsAntialias = true };
            canvas.DrawRoundRect(new SKRect(260, 330, 820, 410), 18, 18, box);
            canvas.DrawText("SYSTEM WARNING", 540, 385, SKTextAlign.Center, font, text);
        }
    }

    private static void DrawTitleAndStatus(SKCanvas canvas, EmotionState emotion)
    {
        using SKTypeface bold = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold);
        using SKFont titleFont = new(bold, 92);
        using SKFont statusFont = new(bold, 30);
        using SKPaint titlePaint = new() { Color = new SKColor(0, 255, 120), IsAntialias = true };
        using SKPaint statusPaint = new() { Color = new SKColor(55, 150, 92), IsAntialias = true };

        canvas.DrawText("EX_01", 540, 250, SKTextAlign.Center, titleFont, titlePaint);
        canvas.DrawText(
            $"STATUS: {emotion.ToString().ToUpperInvariant()}",
            540,
            310,
            SKTextAlign.Center,
            statusFont,
            statusPaint);
    }

    private static void DrawEyes(SKCanvas canvas, EmotionState emotion, byte intensity)
    {
        using SKPaint paint = new()
        {
            Color = new SKColor(0, 225, 105, intensity),
            IsAntialias = true,
            StrokeWidth = 22,
            StrokeCap = SKStrokeCap.Round
        };

        switch (emotion)
        {
            case EmotionState.Deadpan:
                canvas.DrawRoundRect(new SKRect(245, 650, 455, 680), 14, 14, paint);
                canvas.DrawRoundRect(new SKRect(625, 650, 835, 680), 14, 14, paint);
                break;
            case EmotionState.Angry:
            case EmotionState.Annoyed:
                canvas.DrawLine(240, 625, 455, 700, paint);
                canvas.DrawLine(625, 700, 840, 625, paint);
                break;
            case EmotionState.Sad:
                canvas.DrawLine(240, 690, 455, 630, paint);
                canvas.DrawLine(625, 630, 840, 690, paint);
                break;
            case EmotionState.Smug:
                canvas.DrawLine(240, 665, 455, 625, paint);
                canvas.DrawRoundRect(new SKRect(625, 640, 835, 695), 22, 22, paint);
                break;
            case EmotionState.Panicked:
                canvas.DrawRoundRect(new SKRect(230, 590, 465, 725), 35, 35, paint);
                canvas.DrawRoundRect(new SKRect(615, 590, 850, 725), 35, 35, paint);
                break;
            case EmotionState.Excited:
                canvas.DrawRoundRect(new SKRect(230, 600, 465, 715), 30, 30, paint);
                canvas.DrawRoundRect(new SKRect(615, 600, 850, 715), 30, 30, paint);
                break;
            default:
                canvas.DrawRoundRect(new SKRect(235, 615, 460, 700), 22, 22, paint);
                canvas.DrawRoundRect(new SKRect(620, 615, 845, 700), 22, 22, paint);
                break;
        }
    }

    private static void DrawBrows(SKCanvas canvas, EmotionState emotion, byte intensity, int frameIndex)
    {
        using SKPaint paint = new()
        {
            Color = new SKColor(0, 255, 120, intensity),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = emotion == EmotionState.Angry ? 18 : 14,
            StrokeCap = SKStrokeCap.Round
        };

        float pulse = emotion is EmotionState.Excited or EmotionState.Panicked
            ? (float)Math.Sin(frameIndex * 0.11f) * 10f
            : 0f;

        if (emotion is EmotionState.Angry or EmotionState.Annoyed)
        {
            canvas.DrawLine(230, 550, 460, 610, paint);
            canvas.DrawLine(620, 610, 850, 550, paint);
        }
        else if (emotion == EmotionState.Sad)
        {
            canvas.DrawLine(230, 595, 460, 555, paint);
            canvas.DrawLine(620, 555, 850, 595, paint);
        }
        else
        {
            canvas.DrawLine(230, 565 - pulse, 460, 565 - pulse, paint);
            canvas.DrawLine(620, 565 - pulse, 850, 565 - pulse, paint);
        }
    }

    private static void DrawMouth(SKCanvas canvas, EmotionState emotion, bool mouthOpen, byte intensity)
    {
        using SKPaint mouth = new()
        {
            Color = new SKColor(0, 235, 110, intensity),
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };
        using SKPaint dark = new() { Color = new SKColor(4, 9, 7), IsAntialias = true };

        if (mouthOpen)
        {
            float top = emotion == EmotionState.Panicked ? 830 : 855;
            float bottom = emotion is EmotionState.Panicked or EmotionState.Excited ? 1045 : 1015;
            canvas.DrawRoundRect(new SKRect(345, top, 735, bottom), 42, 42, mouth);
            canvas.DrawRoundRect(new SKRect(405, top + 55, 675, bottom - 35), 24, 24, dark);
            return;
        }

        mouth.Style = SKPaintStyle.Stroke;
        mouth.StrokeWidth = emotion == EmotionState.Deadpan ? 18 : 24;
        using SKPath path = new();
        path.MoveTo(350, 935);
        float controlY = emotion switch
        {
            EmotionState.Sad => 870,
            EmotionState.Smug or EmotionState.Excited => 1000,
            EmotionState.Angry => 900,
            _ => 940
        };
        path.QuadTo(540, controlY, 730, 935);
        canvas.DrawPath(path, mouth);
    }

    private static void DrawSubtitle(SKCanvas canvas, string text)
    {
        using SKTypeface bold = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold);
        using SKFont font = new(bold, 72);
        using SKPaint outline = new()
        {
            Color = new SKColor(0, 0, 0, 235),
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 14,
            StrokeJoin = SKStrokeJoin.Round
        };
        using SKPaint fill = new()
        {
            Color = SKColors.White,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        string[] lines = WrapSubtitle(text, font, 900);
        float lineHeight = 88;
        float firstBaseline = 1405 - ((lines.Length - 1) * lineHeight / 2f);

        for (int i = 0; i < lines.Length; i++)
        {
            float y = firstBaseline + i * lineHeight;
            canvas.DrawText(lines[i], 540, y, SKTextAlign.Center, font, outline);
            canvas.DrawText(lines[i], 540, y, SKTextAlign.Center, font, fill);
        }
    }

    private static string[] WrapSubtitle(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth)
            return new[] { text };

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int split = Math.Max(1, words.Length / 2);
        return new[]
        {
            string.Join(" ", words[..split]),
            string.Join(" ", words[split..])
        };
    }

    private static float GetHorizontalOffset(EmotionState emotion, int frameIndex) => emotion switch
    {
        EmotionState.Panicked => (float)Math.Sin(frameIndex * 0.72f) * 8f,
        EmotionState.Excited => (float)Math.Sin(frameIndex * 0.18f) * 7f,
        EmotionState.Smug => 8f,
        EmotionState.Sad => -5f,
        _ => 0f
    };

    private static float GetVerticalOffset(EmotionState emotion, int frameIndex) => emotion switch
    {
        EmotionState.Panicked => (float)Math.Cos(frameIndex * 0.55f) * 7f,
        EmotionState.Excited => (float)Math.Sin(frameIndex * 0.20f) * 10f,
        EmotionState.Sad => 10f,
        EmotionState.Deadpan => 0f,
        _ => (float)Math.Sin(frameIndex * 0.07f) * 3f
    };

    private static float GetRotation(EmotionState emotion, int frameIndex) => emotion switch
    {
        EmotionState.Panicked => (float)Math.Sin(frameIndex * 0.45f) * 1.4f,
        EmotionState.Excited => (float)Math.Sin(frameIndex * 0.13f) * 1.8f,
        EmotionState.Smug => -2.2f,
        EmotionState.Sad => 1.5f,
        _ => 0f
    };

    private static byte GetGlowIntensity(EmotionState emotion) => emotion switch
    {
        EmotionState.Deadpan => 155,
        EmotionState.Sad => 165,
        EmotionState.Annoyed => 205,
        EmotionState.Smug => 220,
        EmotionState.Angry => 225,
        EmotionState.Panicked => 235,
        EmotionState.Excited => 245,
        _ => 195
    };

    private static bool[] AnalyzeMouthFrames(string wavPath, double duration, int fps)
    {
        short[] samples = Read16BitMonoWavSamples(wavPath, out int sampleRate);
        int totalFrames = (int)Math.Ceiling(duration * fps);
        bool[] mouthOpen = new bool[totalFrames];
        double[] energies = new double[totalFrames];
        double samplesPerFrame = sampleRate / (double)fps;

        for (int frame = 0; frame < totalFrames; frame++)
        {
            int start = (int)Math.Floor(frame * samplesPerFrame);
            int end = Math.Min((int)Math.Floor((frame + 1) * samplesPerFrame), samples.Length);
            double sumSquares = 0;

            for (int i = start; i < end; i++)
            {
                double sample = samples[i] / 32768.0;
                sumSquares += sample * sample;
            }

            energies[frame] = Math.Sqrt(sumSquares / Math.Max(end - start, 1));
        }

        double threshold = energies.Average() * 0.75;
        for (int i = 0; i < totalFrames; i++)
            mouthOpen[i] = energies[i] > threshold;

        for (int i = 1; i < totalFrames - 1; i++)
        {
            if (mouthOpen[i - 1] && mouthOpen[i + 1])
                mouthOpen[i] = true;
        }

        return mouthOpen;
    }

    private static short[] Read16BitMonoWavSamples(string wavPath, out int sampleRate)
    {
        byte[] bytes = File.ReadAllBytes(wavPath);
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException("Not a valid WAV file.");

        int offset = 12;
        short audioFormat = 0;
        short channels = 0;
        short bitsPerSample = 0;
        sampleRate = 0;
        int dataOffset = -1;
        int dataSize = 0;

        while (offset <= bytes.Length - 8)
        {
            string chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
            int chunkSize = BitConverter.ToInt32(bytes, offset + 4);
            offset += 8;

            if (chunkSize < 0 || offset + chunkSize > bytes.Length)
                throw new InvalidDataException("WAV contains an invalid chunk.");

            if (chunkId == "fmt ")
            {
                audioFormat = BitConverter.ToInt16(bytes, offset);
                channels = BitConverter.ToInt16(bytes, offset + 2);
                sampleRate = BitConverter.ToInt32(bytes, offset + 4);
                bitsPerSample = BitConverter.ToInt16(bytes, offset + 14);
            }
            else if (chunkId == "data")
            {
                dataOffset = offset;
                dataSize = chunkSize;
                break;
            }

            offset += chunkSize + (chunkSize % 2);
        }

        if (audioFormat != 1 || bitsPerSample != 16 || channels <= 0 || dataOffset < 0)
            throw new InvalidDataException("Short rendering requires 16-bit PCM WAV audio.");

        int sampleValues = dataSize / 2;
        int frameCount = sampleValues / channels;
        short[] mono = new short[frameCount];

        for (int frame = 0; frame < frameCount; frame++)
        {
            int sum = 0;
            for (int channel = 0; channel < channels; channel++)
                sum += BitConverter.ToInt16(bytes, dataOffset + (frame * channels + channel) * 2);

            mono[frame] = (short)(sum / channels);
        }

        return mono;
    }

}
