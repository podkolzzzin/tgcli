#!/usr/bin/env python3
"""Exercise only the deployed single-file binary with real encoded speech."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from prepare_transcription import ROOT, DECODERS, download

FIXTURE_URL = "https://huggingface.co/datasets/Xenova/transformers.js-docs/resolve/159e0fd398b57d12a34e6a01e426e9e416ffce30/jfk.wav"
FIXTURE_SHA256 = "aa81c2552465568567e670f3823117e633900d16bd6202346a72f3c8464c74c8"


def run(command, **kwargs):
    result = subprocess.run([str(x) for x in command], text=True, capture_output=True, timeout=240, **kwargs)
    if result.returncode:
        raise AssertionError(f"{command[0]} failed ({result.returncode}): {result.stderr}")
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("binary", type=Path)
    parser.add_argument("--rid", required=True, choices=DECODERS)
    args = parser.parse_args()
    fixture = ROOT / ".transcription/test-fixtures/jfk.wav"
    download(FIXTURE_URL, fixture, FIXTURE_SHA256)
    decoder = ROOT / ".transcription" / args.rid / ("ffmpeg.exe" if args.rid.startswith("win") else "ffmpeg")
    with tempfile.TemporaryDirectory(prefix="tgcli-transcription-e2e-") as temporary:
        root = Path(temporary)
        deployed = root / "standalone"
        deployed.mkdir()
        binary = deployed / args.binary.name
        shutil.copy2(args.binary, binary)
        binary.chmod(0o755)
        speech = root / "speech.wav"
        shutil.copy2(fixture, speech)
        media = [speech, root / "voice note.ogg", root / "full audio.mp3", root / "video note.mp4"]
        run([decoder, "-nostdin", "-v", "error", "-i", speech, "-c:a", "libopus", "-y", media[1]])
        run([decoder, "-nostdin", "-v", "error", "-i", speech, "-c:a", "libmp3lame", "-y", media[2]])
        run([decoder, "-nostdin", "-v", "error", "-i", speech, "-f", "lavfi", "-i", "color=c=black:s=64x64:r=5", "-map", "0:a", "-map", "1:v", "-c:v", "mpeg4", "-c:a", "aac", "-shortest", "-y", media[3]])
        silent_video = root / "no-audio.mp4"
        run([decoder, "-nostdin", "-v", "error", "-f", "lavfi", "-i", "color=c=black:s=64x64:r=5:d=1", "-an", "-c:v", "mpeg4", "-y", silent_video])
        env = dict(os.environ)
        env.update(PATH="", XDG_DATA_HOME=str(root / "data"), LOCALAPPDATA=str(root / "data"), DOTNET_BUNDLE_EXTRACT_BASE_DIR=str(root / "bundle"))
        for path in media:
            result = run([binary, "transcribe", "--input", path, "--language", "en"], cwd=deployed, env=env)
            value = json.loads(result.stdout)
            assert value["generated"] is True and value["model"] == "whisper-base", value
            text = value["text"].lower()
            assert "country" in text and "you" in text, value
            assert value["segments"] and all(s["end"] >= s["start"] >= 0 for s in value["segments"]), value
            print(f"PASS standalone embedded transcription: {path.suffix}")
        # Automatic language detection and plain output use the same in-process engine.
        auto = json.loads(run([binary, "transcribe", "--input", speech], cwd=deployed, env=env).stdout)
        assert auto["language"] == "en", auto
        plain = run([binary, "transcribe", "--input", speech, "--language", "en", "--format", "plain"], cwd=deployed, env=env)
        assert "country" in plain.stdout.lower(), plain.stdout
        for path in [silent_video, root / "missing.wav"]:
            result = subprocess.run([str(binary), "transcribe", "--input", str(path)], text=True, capture_output=True, timeout=60, cwd=deployed, env=env)
            assert result.returncode != 0 and not result.stdout, result
        print("PASS auto language, plain output, missing media and video without audio")


if __name__ == "__main__":
    main()
