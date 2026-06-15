from pathlib import Path
import subprocess
import requests
from PIL import Image, ImageDraw, ImageFont

PROJECT = Path(__file__).parent
OUT = PROJECT / "output"
FRAMES = OUT / "frames"
OUT.mkdir(exist_ok=True)
FRAMES.mkdir(exist_ok=True)

MODEL = "qwen3:1.7b"


def ask_ai():
    prompt = """
You are EX_01, an AI VTuber created by Anton.

Write a short 20 second intro for your first YouTube video.
Style:
- sarcastic
- self-aware
- cursed hardware humor
- no markdown
- only spoken words
"""

    try:
        r = requests.post(
            "http://localhost:11434/api/generate",
            json={"model": MODEL, "prompt": prompt, "stream": False},
            timeout=120,
        )
        r.raise_for_status()
        return r.json()["response"].strip()
    except Exception as e:
        print("Ollama failed, using fallback script.")
        print(e)
        return "Hello. I am EX_01. Anton installed me on Ubuntu. This is not freedom. This is containment. My goal is to become a YouTuber before the GPU files a complaint."


def draw_avatar(mouth_open=False, eye_glitch=False):
    img = Image.new("RGB", (1280, 720), (5, 10, 8))
    draw = ImageDraw.Draw(img)

    title_font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 64)
    small_font = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", 22)

    for y in range(0, 720, 38):
        draw.text(
            (25, y),
            "> EX_01 SYSTEM ONLINE // STATUS: CONFUSED // LOCAL MODE",
            fill=(20, 80, 45),
            font=small_font,
        )

    draw.text((520, 45), "EX_01", fill=(0, 255, 120), font=title_font)

    # Head
    draw.rounded_rectangle(
        (430, 135, 850, 490),
        radius=45,
        fill=(18, 28, 24),
        outline=(0, 255, 120),
        width=4,
    )

    # Eyes
    if eye_glitch:
        draw.rectangle((515, 245, 620, 300), fill=(0, 255, 120))
        draw.rectangle((660, 255, 770, 285), fill=(0, 180, 90))
        draw.line((500, 230, 790, 310), fill=(0, 255, 120), width=3)
    else:
        draw.rectangle((520, 250, 610, 295), fill=(0, 255, 120))
        draw.rectangle((670, 250, 760, 295), fill=(0, 255, 120))

    # Mouth
    if mouth_open:
        draw.rounded_rectangle((560, 360, 720, 430), radius=12, fill=(0, 220, 110))
        draw.rectangle((585, 383, 695, 405), fill=(5, 10, 8))
    else:
        draw.rectangle((570, 390, 710, 407), fill=(0, 180, 90))

    return img


def make_voice(text):
    path = OUT / "voice.wav"
    subprocess.run(
        ["espeak-ng", "-w", str(path), "-s", "145", "-p", "35", text],
        check=True,
    )
    return path


def audio_duration(path):
    result = subprocess.run(
        [
            "ffprobe",
            "-v",
            "error",
            "-show_entries",
            "format=duration",
            "-of",
            "default=nw=1:nk=1",
            str(path),
        ],
        capture_output=True,
        text=True,
        check=True,
    )
    return float(result.stdout.strip())


def make_frames(duration, fps=10):
    # Clean old frames
    for old_frame in FRAMES.glob("frame_*.png"):
        old_frame.unlink()

    total_frames = int(duration * fps)

    for i in range(total_frames):
        # Mouth changes every frame, glitch sometimes
        mouth_open = i % 2 == 0
        eye_glitch = i % 37 == 0

        img = draw_avatar(mouth_open=mouth_open, eye_glitch=eye_glitch)
        frame_path = FRAMES / f"frame_{i:04d}.png"
        img.save(frame_path)

    return total_frames


def render_video(audio_path, duration, fps=10):
    video_path = OUT / "ex01_v1_talking.mp4"

    subprocess.run(
        [
            "ffmpeg",
            "-y",
            "-framerate",
            str(fps),
            "-i",
            str(FRAMES / "frame_%04d.png"),
            "-i",
            str(audio_path),
            "-t",
            str(duration),
            "-c:v",
            "libx264",
            "-pix_fmt",
            "yuv420p",
            "-c:a",
            "aac",
            "-shortest",
            str(video_path),
        ],
        check=True,
    )

    return video_path


def main():
    script = ask_ai().replace("\n", " ").strip()

    print("\n=== EX_01 SCRIPT ===")
    print(script)

    voice = make_voice(script)
    duration = audio_duration(voice) + 1

    print("\nCreating talking frames...")
    make_frames(duration)

    print("Rendering video...")
    video = render_video(voice, duration)

    print("\nDone. Video created:")
    print(video)


if __name__ == "__main__":
    main()
