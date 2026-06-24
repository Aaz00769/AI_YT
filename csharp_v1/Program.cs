using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using AI_YOUTUBER.Functions.ASKING;
using AI_YOUTUBER.Functions.RESEARCH;
class Program
{
    static readonly string Model = "mistral-small3.2:24b";
            //outher models: "mistral-small3.2:24b,
            // qwen3:14b", 
            // "qwen3:8b", 
    static readonly string ProjectDir = Directory.GetCurrentDirectory();
    static readonly string OutputDir = Path.GetFullPath(Path.Combine(ProjectDir, "..", "output"));
    static readonly string FramesDir = Path.Combine(OutputDir, "csharp_frames");

    static async Task Main()
    {
        
        Directory.CreateDirectory(OutputDir);
        Directory.CreateDirectory(FramesDir);
         

        string script = await AskAI.Ask24bMain();
        

        Console.WriteLine("\n=== EX_01 SCRIPT for qwen3:8b===");
        Console.WriteLine(script);

        string voicePath = Path.Combine(OutputDir, "csharp_voice.wav");
        string videoPath = Path.Combine(OutputDir, "ex01_csharp_talking.mp4");

        MakeVoice(script, voicePath);

        string cleanVoicePath = Path.Combine(OutputDir, "csharp_voice_clean.wav");
        NormalizeWavForAnalysis(voicePath, cleanVoicePath);

        double duration = GetAudioDuration(cleanVoicePath) + 1;

        Console.WriteLine("\nCreating C# avatar frames...");
        MakeFrames(duration, cleanVoicePath, fps: 10);

        Console.WriteLine("Rendering video...");
        RenderVideo(cleanVoicePath, videoPath, duration, fps: 10);

        Console.WriteLine("\nDone. Video created:");
        Console.WriteLine(videoPath);
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
    static void MakeFrames(double duration, string audioPath, int fps)
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
        bool eyeGlitch = i % 37 == 0;

        string framePath = Path.Combine(FramesDir, $"frame_{i:0000}.png");

        using SKBitmap bitmap = DrawAvatar(mouthOpen, eyeGlitch);
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
    static SKBitmap DrawAvatar(bool mouthOpen, bool eyeGlitch)
    {
        SKBitmap bitmap = new(1280, 720);
        using SKCanvas canvas = new(bitmap);

        canvas.Clear(new SKColor(5, 10, 8));

        using SKPaint bgTextPaint = new()
        {
            Color = new SKColor(20, 80, 45),
            TextSize = 22,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("DejaVu Sans")
        };

        using SKPaint titlePaint = new()
        {
            Color = new SKColor(0, 255, 120),
            TextSize = 64,
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyle.Bold)
        };

        using SKPaint greenPaint = new()
        {
            Color = new SKColor(0, 255, 120),
            IsAntialias = true
        };

        using SKPaint darkPaint = new()
        {
            Color = new SKColor(18, 28, 24),
            IsAntialias = true
        };

        using SKPaint mouthPaint = new()
        {
            Color = new SKColor(0, 220, 110),
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
                "> EX_01 SYSTEM ONLINE // C# BODY ACTIVE // STATUS: CONFUSED",
                25,
                y,
                bgTextPaint
            );
        }

        canvas.DrawText("EX_01", 520, 90, titlePaint);

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
            canvas.DrawRect(new SKRect(520, 250, 610, 295), greenPaint);
            canvas.DrawRect(new SKRect(670, 250, 760, 295), greenPaint);
        }

        // Mouth
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
}
