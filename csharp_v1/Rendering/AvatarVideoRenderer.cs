using System.Globalization;
using AI_YOUTUBER.Infrastructure;
using AI_YOUTUBER.Models;
using SkiaSharp;

namespace AI_YOUTUBER.Rendering;

public sealed class AvatarVideoRenderer
{
    public async Task CreateVisualAsync(string topic, string outputPath, VideoOrientation orientation)
    {
        (int width, int height) = orientation == VideoOrientation.Portrait
            ? (1080, 1920)
            : (1280, 720);
        using SKBitmap bitmap = new(width, height);
        using SKCanvas canvas = new(bitmap);
        canvas.Clear(new SKColor(5, 10, 8));

        using SKPaint grid = Paint(new SKColor(16, 58, 36));
        using SKFont gridFont = Font(orientation == VideoOrientation.Portrait ? 28 : 18);
        string status = "> EX_01 LOCAL // QUADRO T1000 // THERMALS: NEGOTIATING";
        for (int y = 35; y < height; y += orientation == VideoOrientation.Portrait ? 70 : 46)
            canvas.DrawText(status, 24, y, SKTextAlign.Left, gridFont, grid);

        float centerX = width / 2f;
        float headWidth = orientation == VideoOrientation.Portrait ? 720 : 420;
        float headHeight = orientation == VideoOrientation.Portrait ? 620 : 355;
        float headTop = orientation == VideoOrientation.Portrait ? 430 : 145;
        SKRect head = new(centerX - headWidth / 2, headTop, centerX + headWidth / 2, headTop + headHeight);

        using SKPaint face = Paint(new SKColor(18, 28, 24));
        using SKPaint green = Paint(new SKColor(0, 255, 120));
        using SKPaint outline = Paint(new SKColor(0, 255, 120));
        outline.Style = SKPaintStyle.Stroke;
        outline.StrokeWidth = 5;
        canvas.DrawRoundRect(head, 55, 55, face);
        canvas.DrawRoundRect(head, 55, 55, outline);

        float eyeY = head.Top + headHeight * 0.36f;
        float eyeWidth = headWidth * 0.20f;
        float eyeHeight = headHeight * 0.10f;
        canvas.DrawRect(centerX - headWidth * 0.29f, eyeY, centerX - headWidth * 0.09f, eyeY + eyeHeight, green);
        canvas.DrawRect(centerX + headWidth * 0.09f, eyeY, centerX + headWidth * 0.29f, eyeY + eyeHeight, green);
        canvas.DrawRoundRect(
            new SKRect(centerX - headWidth * 0.18f, head.Top + headHeight * 0.68f,
                centerX + headWidth * 0.18f, head.Top + headHeight * 0.73f),
            8, 8, green);

        using SKPaint title = Paint(new SKColor(0, 255, 120));
        using SKFont titleFont = Font(orientation == VideoOrientation.Portrait ? 96 : 62, bold: true);
        DrawCentered(canvas, "EX_01", centerX, orientation == VideoOrientation.Portrait ? 230 : 82, titleFont, title);

        using SKPaint topicPaint = Paint(new SKColor(225, 245, 233));
        using SKFont topicFont = Font(orientation == VideoOrientation.Portrait ? 56 : 34, bold: true);
        float topicTop = orientation == VideoOrientation.Portrait ? 1250 : 585;
        DrawWrappedCentered(canvas, topic, centerX, topicTop, width * 0.82f, topicFont, topicPaint, maxLines: 3);

        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 95);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, data.ToArray());
    }

    public async Task RenderVideoAsync(string visualPath, string voicePath, string videoPath, double durationSeconds)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(videoPath)!);
        await ProcessRunner.EnsureSuccessAsync(
            "ffmpeg",
            new[]
            {
                "-y", "-loop", "1", "-framerate", "2", "-i", visualPath,
                "-i", voicePath,
                "-t", durationSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-preset", "veryfast", "-tune", "stillimage",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k",
                "-shortest", "-movflags", "+faststart", videoPath
            },
            timeout: TimeSpan.FromHours(2));
    }

    private static SKPaint Paint(SKColor color) => new()
    {
        Color = color,
        IsAntialias = true
    };

    private static SKFont Font(float size, bool bold = false) => new(
        SKTypeface.FromFamilyName("DejaVu Sans", bold ? SKFontStyle.Bold : SKFontStyle.Normal),
        size);

    private static void DrawCentered(
        SKCanvas canvas,
        string text,
        float centerX,
        float baseline,
        SKFont font,
        SKPaint paint)
    {
        canvas.DrawText(text, centerX, baseline, SKTextAlign.Center, font, paint);
    }

    private static void DrawWrappedCentered(
        SKCanvas canvas,
        string text,
        float centerX,
        float top,
        float maximumWidth,
        SKFont font,
        SKPaint paint,
        int maxLines)
    {
        List<string> lines = new();
        string current = "";
        foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = string.IsNullOrEmpty(current) ? word : $"{current} {word}";
            if (font.MeasureText(candidate) <= maximumWidth)
            {
                current = candidate;
                continue;
            }

            if (!string.IsNullOrEmpty(current))
                lines.Add(current);
            current = word;
            if (lines.Count == maxLines - 1)
                break;
        }
        if (!string.IsNullOrEmpty(current) && lines.Count < maxLines)
            lines.Add(current);

        float lineHeight = font.Size * 1.25f;
        for (int i = 0; i < lines.Count; i++)
            DrawCentered(canvas, lines[i], centerX, top + i * lineHeight, font, paint);
    }
}
