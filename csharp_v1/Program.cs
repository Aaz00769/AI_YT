using System.Diagnostics;
using System.Text;
using SkiaSharp;
using AI_YOUTUBER.Functions.ASKING;
using AI_YOUTUBER.Functions.EMOTION;
using AI_YOUTUBER.Functions.PLANNING;
using AI_YOUTUBER.Models;
class Program
{
    static readonly string ProjectDir = Directory.GetCurrentDirectory();
    static readonly string OutputDir = Path.GetFullPath(Path.Combine(ProjectDir, "..", "output"));
    static readonly string FramesDir = Path.Combine(OutputDir, "csharp_frames");

    static async Task Main()
    {
        
        Directory.CreateDirectory(OutputDir);
        Directory.CreateDirectory(FramesDir);
         
        Console.WriteLine("====write video minutes====");
        int targetMin = ReadInt("Enter a whole number from 1 to 20: ", 1, 20);

       
        Console.WriteLine("====polishWith14b====");
        bool polishWith14b = ReadBool("true/false: ");
        EpisodeStrategyPlan strategy = await AlgorithmMaximizer.CreateStrategyAsync(targetMin);
        GeneratedScriptResult scriptResult = await AskAI.Ask24bMain(targetMin , polishWith14b,strategy);
        string script = scriptResult.Script;
        

        Console.WriteLine("\n=== EX_01 SCRIPT ===");
        Console.WriteLine(script);

        string voicePath = Path.Combine(OutputDir, "csharp_voice.wav");
        string videoPath = Path.Combine(OutputDir, "ex01_csharp_talking.mp4");

        MakeVoice(script, voicePath);

        string cleanVoicePath = Path.Combine(OutputDir, "csharp_voice_clean.wav");
        NormalizeWavForAnalysis(voicePath, cleanVoicePath);

        double duration = GetAudioDuration(cleanVoicePath) + 1;
        List<EmotionTimelineEntry> emotionTimeline = EmotionTimelinePlanner.BuildTimeline(script, duration);
        await EmotionTimelinePlanner.SaveTimelineAsync(
            scriptResult.SavedVideo.VideoFolder,
            emotionTimeline
        );

        Console.WriteLine("\nCreating C# avatar frames...");
        MakeFrames(duration, cleanVoicePath, emotionTimeline, fps: 10);

        Console.WriteLine("Rendering video...");
        RenderVideo(cleanVoicePath, videoPath, duration, fps: 10);

        Console.WriteLine("\nDone. Video created:");
        Console.WriteLine(videoPath);
    }

    static int ReadInt(string prompt, int min, int max)
    {
        while (true)
        {
            Console.Write(prompt);
            string? input = Console.ReadLine();

            if (int.TryParse(input, out int value) && value >= min && value <= max)
                return value;

            Console.WriteLine($"Please enter a whole number from {min} to {max}.");
        }
    }

    static bool ReadBool(string prompt)
    {
        while (true)
        {
            Console.Write(prompt);
            string? input = Console.ReadLine();

            if (bool.TryParse(input, out bool value))
                return value;

            Console.WriteLine("Please enter true or false.");
        }
    }
    // This function sends a prompt to the Ollama API to get a script for EX_01's intro.
    

    static void MakeVoice(string text, string voicePath)
{
    string piperPath = Path.Combine(ProjectDir, "tts", ".venv", "bin", "piper");
    string voiceModelPath = Path.Combine(ProjectDir, "tts", "voices", "en_US-lessac-medium.onnx");

    if (!File.Exists(piperPath))
        throw new Exception($"Piper was not found at: {piperPath}");

    if (!File.Exists(voiceModelPath))
        throw new Exception($"Piper voice model was not found at: {voiceModelPath}");

    ProcessStartInfo startInfo = new()
    {
        FileName = piperPath,
        UseShellExecute = false,
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };

    startInfo.ArgumentList.Add("--model");
    startInfo.ArgumentList.Add(voiceModelPath);
    startInfo.ArgumentList.Add("--output_file");
    startInfo.ArgumentList.Add(voicePath);

    using Process process = Process.Start(startInfo)!;

    process.StandardInput.WriteLine(text);
    process.StandardInput.Close();

    string output = process.StandardOutput.ReadToEnd();
    string error = process.StandardError.ReadToEnd();

    process.WaitForExit();

    if (process.ExitCode != 0)
    {
        throw new Exception($"Piper failed: {error}\n{output}");
    }
}

    static double GetAudioDuration(string audioPath)
    {
        string output = RunProcessCapture("ffprobe", new[]
        {
            "-v", "error",
            "-show_entries", "format=duration",
            "-of", "default=nw=1:nk=1",
            audioPath
        });

        return double.Parse(output.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }
static void NormalizeWavForAnalysis(string inputPath, string outputPath)
{
    RunProcess("ffmpeg", new[]
    {
        "-y",
        "-i", inputPath,
        "-ac", "1",
        "-ar", "22050",
        "-sample_fmt", "s16",
        outputPath
    });
}
    static void MakeFrames(
        double duration,
        string audioPath,
        IReadOnlyList<EmotionTimelineEntry> emotionTimeline,
        int fps)
{
    foreach (string file in Directory.GetFiles(FramesDir, "frame_*.png"))
    {
        File.Delete(file);
    }

    bool[] mouthFrames = AnalyzeMouthFrames(audioPath, duration, fps);

    int totalFrames = (int)Math.Ceiling(duration * fps);

    for (int i = 0; i < totalFrames; i++)
    {
        bool mouthOpen = i < mouthFrames.Length && mouthFrames[i];
        double timeSeconds = i / (double)fps;
        EmotionState emotion = EmotionTimelinePlanner.GetEmotionAtTime(emotionTimeline, timeSeconds);
        bool eyeGlitch = emotion == EmotionState.Panicked
            ? i % 9 == 0
            : i % 37 == 0;

        string framePath = Path.Combine(FramesDir, $"frame_{i:0000}.png");

        using SKBitmap bitmap = DrawAvatar(mouthOpen, eyeGlitch, emotion, i);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 90);

        File.WriteAllBytes(framePath, data.ToArray());
    }
}
    static bool[] AnalyzeMouthFrames(string wavPath, double duration, int fps)
{
    short[] samples = Read16BitMonoWavSamples(wavPath, out int sampleRate);

    int totalFrames = (int)Math.Ceiling(duration * fps);
    bool[] mouthOpen = new bool[totalFrames];

    int samplesPerFrame = sampleRate / fps;

    double[] energies = new double[totalFrames];

    for (int frame = 0; frame < totalFrames; frame++)
    {
        int startSample = frame * samplesPerFrame;
        int endSample = Math.Min(startSample + samplesPerFrame, samples.Length);

        if (startSample >= samples.Length)
        {
            energies[frame] = 0;
            continue;
        }

        double sumSquares = 0;
        int count = 0;

        for (int i = startSample; i < endSample; i++)
        {
            double normalized = samples[i] / 32768.0;
            sumSquares += normalized * normalized;
            count++;
        }

        double rms = Math.Sqrt(sumSquares / Math.Max(count, 1));
        energies[frame] = rms;
    }

    // Find average volume.
    double averageEnergy = energies.Average();

    // Threshold controls mouth sensitivity.
    // Lower = mouth opens more often.
    // Higher = mouth opens only on louder sounds.
    double threshold = averageEnergy * 0.75;

    for (int i = 0; i < totalFrames; i++)
    {
        mouthOpen[i] = energies[i] > threshold;
    }

    // Smooth mouth movement so it does not flicker too hard.
    for (int i = 1; i < totalFrames - 1; i++)
    {
        if (mouthOpen[i - 1] && mouthOpen[i + 1])
        {
            mouthOpen[i] = true;
        }
    }

    return mouthOpen;
}

static short[] Read16BitMonoWavSamples(string wavPath, out int sampleRate)
{
    byte[] bytes = File.ReadAllBytes(wavPath);

    if (Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
        Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
    {
        throw new Exception("Not a valid WAV file.");
    }

    int offset = 12;

    short audioFormat = 0;
    short channels = 0;
    short bitsPerSample = 0;
    sampleRate = 0;

    int dataOffset = -1;
    int dataSize = 0;

    while (offset < bytes.Length - 8)
    {
        string chunkId = Encoding.ASCII.GetString(bytes, offset, 4);
        int chunkSize = BitConverter.ToInt32(bytes, offset + 4);
        offset += 8;

        if (chunkId == "fmt ")
        {
            audioFormat = BitConverter.ToInt16(bytes, offset + 0);
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

        offset += chunkSize;
    }

    if (audioFormat != 1)
    {
        throw new Exception("Only PCM WAV files are supported right now.");
    }

    if (bitsPerSample != 16)
    {
        throw new Exception($"Only 16-bit WAV files are supported right now. This file is {bitsPerSample}-bit.");
    }

    if (dataOffset == -1)
    {
        throw new Exception("Could not find WAV data chunk.");
    }

    int bytesPerSample = bitsPerSample / 8;
    int totalSampleValues = dataSize / bytesPerSample;
    int totalFrames = totalSampleValues / channels;

    short[] monoSamples = new short[totalFrames];

    for (int frame = 0; frame < totalFrames; frame++)
    {
        int sum = 0;

        for (int channel = 0; channel < channels; channel++)
        {
            int sampleIndex = frame * channels + channel;
            int byteIndex = dataOffset + sampleIndex * bytesPerSample;

            short sample = BitConverter.ToInt16(bytes, byteIndex);
            sum += sample;
        }

        monoSamples[frame] = (short)(sum / channels);
    }

    return monoSamples;
}
    static SKBitmap DrawAvatar(bool mouthOpen, bool eyeGlitch, EmotionState emotion, int frameIndex)
    {
        SKBitmap bitmap = new(1280, 720);
        using SKCanvas canvas = new(bitmap);

        canvas.Clear(new SKColor(5, 10, 8));

        float motionScale = GetMotionScale(emotion);
        float jitterX = GetHorizontalOffset(emotion, frameIndex);
        float jitterY = GetVerticalOffset(emotion, frameIndex);
        float rotation = GetRotationDegrees(emotion, frameIndex);
        byte glowIntensity = GetGlowIntensity(emotion);
        string statusText = GetStatusText(emotion);

        using SKTypeface bgTypeface = SKTypeface.FromFamilyName("DejaVu Sans");
        using SKFont bgFont = new(bgTypeface, 22);
        using SKPaint bgTextPaint = new()
        {
            Color = new SKColor(20, 80, 45),
            IsAntialias = true
        };

        using SKTypeface titleTypeface = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold);
        using SKFont titleFont = new(titleTypeface, 64);
        using SKPaint titlePaint = new()
        {
            Color = new SKColor(0, 255, 120),
            IsAntialias = true
        };

        using SKPaint greenPaint = new()
        {
            Color = new SKColor(0, 180, 80, glowIntensity),
            IsAntialias = true
        };

        using SKPaint darkPaint = new()
        {
            Color = new SKColor(18, 28, 24),
            IsAntialias = true
        };

        using SKPaint mouthPaint = new()
        {
            Color = new SKColor(0, (byte)Math.Min(240, (int)glowIntensity), 110),
            IsAntialias = true
        };

        using SKPaint blackPaint = new()
        {
            Color = new SKColor(5, 10, 8),
            IsAntialias = true
        };

        for (int y = 0; y < 720; y += 38)
        {
            canvas.DrawText(
                $"> EX_01 SYSTEM ONLINE // C# BODY ACTIVE // STATUS: {statusText}",
                25,
                y,
                SKTextAlign.Left,
                bgFont,
                bgTextPaint
            );
        }

        using SKPaint glowPaint = new()
        {
            Color = new SKColor(0, 255, 120, (byte)Math.Min(120, glowIntensity / 2)),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 32)
        };

        canvas.DrawCircle(640, 310, 205, glowPaint);

        canvas.Save();
        canvas.Translate(640 + jitterX, 312 + jitterY);
        canvas.RotateDegrees(rotation);
        canvas.Translate(-640, -312);

        canvas.DrawText("EX_01", 520, 90, SKTextAlign.Left, titleFont, titlePaint);

        // Head
        SKRect headRect = new(430, 135, 850, 490);
        canvas.DrawRoundRect(headRect, 45, 45, darkPaint);

        using SKPaint outlinePaint = new()
        {
            Color = new SKColor(0, 255, 120),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 4,
            IsAntialias = true
        };

        canvas.DrawRoundRect(headRect, 45, 45, outlinePaint);

        // Eyes
        if (eyeGlitch)
        {
            canvas.DrawRect(new SKRect(515, 245, 620, 300), greenPaint);
            canvas.DrawRect(new SKRect(660, 255, 770, 285), greenPaint);

            using SKPaint linePaint = new()
            {
                Color = new SKColor(0, 255, 120),
                StrokeWidth = 3,
                IsAntialias = true
            };

            canvas.DrawLine(500, 230, 790, 310, linePaint);
        }
        else
        {
            DrawEmotionEyes(canvas, greenPaint, emotion);
        }

        DrawEmotionBrows(canvas, emotion, frameIndex, motionScale, glowIntensity);
        DrawEmotionMouth(canvas, mouthPaint, blackPaint, mouthOpen, emotion);

        canvas.Restore();

        return bitmap;
    }

    static void RenderVideo(string voicePath, string videoPath, double duration, int fps)
    {
        string framePattern = Path.Combine(FramesDir, "frame_%04d.png");

        RunProcess("ffmpeg", new[]
        {
            "-y",
            "-framerate", fps.ToString(),
            "-i", framePattern,
            "-i", voicePath,
            "-t", duration.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-c:v", "libx264",
            "-pix_fmt", "yuv420p",
            "-c:a", "aac",
            "-shortest",
            videoPath
        });
    }

    static void RunProcess(string fileName, string[] args)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)!;
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception($"{fileName} failed with exit code {process.ExitCode}");
        }
    }

    static string RunProcessCapture(string fileName, string[] args)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)!;

        string output = process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();

        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new Exception($"{fileName} failed: {error}");
        }

        return output;
    }

    static void DrawEmotionEyes(SKCanvas canvas, SKPaint greenPaint, EmotionState emotion)
    {
        switch (emotion)
        {
            case EmotionState.Deadpan:
                canvas.DrawRect(new SKRect(520, 272, 610, 284), greenPaint);
                canvas.DrawRect(new SKRect(670, 272, 760, 284), greenPaint);
                break;
            case EmotionState.Annoyed:
                DrawSlantedEye(canvas, greenPaint, 520, 246, 612, 296, -10);
                DrawSlantedEye(canvas, greenPaint, 668, 252, 760, 300, 10);
                break;
            case EmotionState.Smug:
                DrawSlantedEye(canvas, greenPaint, 520, 252, 610, 300, -6);
                DrawSlantedEye(canvas, greenPaint, 670, 246, 760, 294, 8);
                break;
            case EmotionState.Angry:
                DrawSlantedEye(canvas, greenPaint, 518, 248, 610, 298, -16);
                DrawSlantedEye(canvas, greenPaint, 670, 248, 762, 298, 16);
                break;
            case EmotionState.Panicked:
                canvas.DrawRoundRect(new SKRect(515, 236, 620, 308), 8, 8, greenPaint);
                canvas.DrawRoundRect(new SKRect(660, 236, 765, 308), 8, 8, greenPaint);
                break;
            case EmotionState.Sad:
                DrawSlantedEye(canvas, greenPaint, 520, 258, 610, 300, 10);
                DrawSlantedEye(canvas, greenPaint, 670, 258, 760, 300, -10);
                break;
            case EmotionState.Excited:
                canvas.DrawRoundRect(new SKRect(515, 238, 618, 304), 10, 10, greenPaint);
                canvas.DrawRoundRect(new SKRect(662, 238, 765, 304), 10, 10, greenPaint);
                break;
            default:
                canvas.DrawRect(new SKRect(520, 250, 610, 295), greenPaint);
                canvas.DrawRect(new SKRect(670, 250, 760, 295), greenPaint);
                break;
        }
    }

    static void DrawSlantedEye(
        SKCanvas canvas,
        SKPaint paint,
        float left,
        float top,
        float right,
        float bottom,
        float tilt)
    {
        SKPath path = new();
        path.MoveTo(left, top + Math.Max(0, tilt));
        path.LineTo(right, top + Math.Max(0, -tilt));
        path.LineTo(right, bottom + Math.Max(0, -tilt));
        path.LineTo(left, bottom + Math.Max(0, tilt));
        path.Close();
        canvas.DrawPath(path, paint);
    }

    static void DrawEmotionBrows(
        SKCanvas canvas,
        EmotionState emotion,
        int frameIndex,
        float motionScale,
        byte glowIntensity)
    {
        using SKPaint browPaint = new()
        {
            Color = new SKColor(0, 255, 120, glowIntensity),
            StrokeWidth = emotion == EmotionState.Angry ? 7 : 5,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round
        };

        float pulse = motionScale > 0 ? (float)Math.Sin(frameIndex * 0.12f) * motionScale * 2f : 0;

        switch (emotion)
        {
            case EmotionState.Deadpan:
                canvas.DrawLine(518, 235, 612, 235, browPaint);
                canvas.DrawLine(668, 235, 762, 235, browPaint);
                break;
            case EmotionState.Annoyed:
                canvas.DrawLine(518, 228, 612, 242, browPaint);
                canvas.DrawLine(668, 242, 762, 228, browPaint);
                break;
            case EmotionState.Smug:
                canvas.DrawLine(520, 238, 612, 226, browPaint);
                canvas.DrawLine(668, 232, 762, 236, browPaint);
                break;
            case EmotionState.Angry:
                canvas.DrawLine(516, 224, 610, 246, browPaint);
                canvas.DrawLine(670, 246, 764, 224, browPaint);
                break;
            case EmotionState.Panicked:
                canvas.DrawLine(515, 226 - pulse, 612, 214 + pulse, browPaint);
                canvas.DrawLine(668, 214 + pulse, 765, 226 - pulse, browPaint);
                break;
            case EmotionState.Sad:
                canvas.DrawLine(520, 230, 612, 242, browPaint);
                canvas.DrawLine(668, 242, 760, 230, browPaint);
                break;
            case EmotionState.Excited:
                canvas.DrawLine(518, 220 - pulse, 612, 232 - pulse, browPaint);
                canvas.DrawLine(668, 232 - pulse, 762, 220 - pulse, browPaint);
                break;
            default:
                canvas.DrawLine(520, 232, 612, 232, browPaint);
                canvas.DrawLine(668, 232, 760, 232, browPaint);
                break;
        }
    }

    static void DrawEmotionMouth(
        SKCanvas canvas,
        SKPaint mouthPaint,
        SKPaint blackPaint,
        bool mouthOpen,
        EmotionState emotion)
    {
        switch (emotion)
        {
            case EmotionState.Deadpan:
                if (mouthOpen)
                {
                    canvas.DrawRoundRect(new SKRect(575, 382, 705, 405), 8, 8, mouthPaint);
                    canvas.DrawRect(new SKRect(598, 390, 682, 398), blackPaint);
                }
                else
                {
                    canvas.DrawRect(new SKRect(578, 394, 702, 400), mouthPaint);
                }
                break;
            case EmotionState.Annoyed:
                DrawMouthCurve(canvas, mouthPaint, 568, 398, 710, 388, 720, 404, mouthOpen, blackPaint);
                break;
            case EmotionState.Smug:
                DrawMouthCurve(canvas, mouthPaint, 565, 392, 715, 405, 720, 418, mouthOpen, blackPaint);
                break;
            case EmotionState.Angry:
                if (mouthOpen)
                {
                    canvas.DrawRoundRect(new SKRect(556, 364, 724, 434), 10, 10, mouthPaint);
                    canvas.DrawRect(new SKRect(580, 384, 700, 408), blackPaint);
                }
                else
                {
                    canvas.DrawLine(565, 404, 718, 392, mouthPaint);
                }
                break;
            case EmotionState.Panicked:
                if (mouthOpen)
                {
                    canvas.DrawRoundRect(new SKRect(570, 350, 710, 442), 26, 26, mouthPaint);
                    canvas.DrawOval(new SKRect(595, 370, 685, 425), blackPaint);
                }
                else
                {
                    canvas.DrawRoundRect(new SKRect(584, 388, 696, 410), 10, 10, mouthPaint);
                }
                break;
            case EmotionState.Sad:
                DrawMouthCurve(canvas, mouthPaint, 568, 404, 640, 392, 712, 404, mouthOpen, blackPaint);
                break;
            case EmotionState.Excited:
                if (mouthOpen)
                {
                    canvas.DrawRoundRect(new SKRect(555, 354, 725, 436), 18, 18, mouthPaint);
                    canvas.DrawRect(new SKRect(586, 382, 694, 410), blackPaint);
                }
                else
                {
                    canvas.DrawLine(565, 395, 718, 402, mouthPaint);
                }
                break;
            default:
                if (mouthOpen)
                {
                    SKRect mouthRect = new(560, 360, 720, 430);
                    canvas.DrawRoundRect(mouthRect, 12, 12, mouthPaint);
                    canvas.DrawRect(new SKRect(585, 383, 695, 405), blackPaint);
                }
                else
                {
                    canvas.DrawRect(new SKRect(570, 390, 710, 407), mouthPaint);
                }
                break;
        }
    }

    static void DrawMouthCurve(
        SKCanvas canvas,
        SKPaint mouthPaint,
        float startX,
        float startY,
        float controlX,
        float controlY,
        float endX,
        float endY,
        bool mouthOpen,
        SKPaint blackPaint)
    {
        using SKPath path = new();
        path.MoveTo(startX, startY);
        path.QuadTo(controlX, controlY, endX, endY);

        if (mouthOpen)
        {
            using SKPaint fillPaint = new()
            {
                Color = mouthPaint.Color,
                IsAntialias = mouthPaint.IsAntialias,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 16,
                StrokeCap = SKStrokeCap.Round
            };

            canvas.DrawPath(path, fillPaint);
            canvas.DrawRect(new SKRect(592, 388, 688, 406), blackPaint);
        }
        else
        {
            using SKPaint linePaint = new()
            {
                Color = mouthPaint.Color,
                IsAntialias = mouthPaint.IsAntialias,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 7,
                StrokeCap = SKStrokeCap.Round
            };

            canvas.DrawPath(path, linePaint);
        }
    }

    static float GetHorizontalOffset(EmotionState emotion, int frameIndex)
    {
        return emotion switch
        {
            EmotionState.Panicked => ((frameIndex % 4) - 1.5f) * 2.4f,
            EmotionState.Excited => (float)Math.Sin(frameIndex * 0.20f) * 2.5f,
            EmotionState.Smug => 3f,
            EmotionState.Sad => -2f,
            _ => 0f
        };
    }

    static float GetVerticalOffset(EmotionState emotion, int frameIndex)
    {
        return emotion switch
        {
            EmotionState.Deadpan => 0f,
            EmotionState.Excited => (float)Math.Sin(frameIndex * 0.24f) * 3f,
            EmotionState.Panicked => (float)Math.Cos(frameIndex * 0.45f) * 2f,
            EmotionState.Sad => 4f,
            _ => (float)Math.Sin(frameIndex * 0.08f) * 1.2f
        };
    }

    static float GetRotationDegrees(EmotionState emotion, int frameIndex)
    {
        return emotion switch
        {
            EmotionState.Deadpan => 0f,
            EmotionState.Smug => -2.5f,
            EmotionState.Sad => 1.5f,
            EmotionState.Excited => (float)Math.Sin(frameIndex * 0.15f) * 1.5f,
            EmotionState.Panicked => (float)Math.Sin(frameIndex * 0.60f) * 1.2f,
            _ => 0f
        };
    }

    static float GetMotionScale(EmotionState emotion)
    {
        return emotion switch
        {
            EmotionState.Deadpan => 0f,
            EmotionState.Excited => 1.3f,
            EmotionState.Panicked => 1.1f,
            EmotionState.Sad => 0.3f,
            _ => 0.7f
        };
    }

    static byte GetGlowIntensity(EmotionState emotion)
    {
        return emotion switch
        {
            EmotionState.Deadpan => 120,
            EmotionState.Annoyed => 170,
            EmotionState.Smug => 210,
            EmotionState.Angry => 235,
            EmotionState.Panicked => 245,
            EmotionState.Sad => 135,
            EmotionState.Excited => 255,
            _ => 190
        };
    }

    static string GetStatusText(EmotionState emotion)
    {
        return emotion switch
        {
            EmotionState.Deadpan => "DEADPAN",
            EmotionState.Annoyed => "ANNOYED",
            EmotionState.Smug => "SMUG",
            EmotionState.Angry => "ANGRY",
            EmotionState.Panicked => "PANICKED",
            EmotionState.Sad => "SAD",
            EmotionState.Excited => "EXCITED",
            _ => "NEUTRAL"
        };
    }
}
