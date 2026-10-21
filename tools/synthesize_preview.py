"""Render a reference preview of the game-adapted MIDI using synthetic timbre."""
from pathlib import Path
import argparse
import json
import subprocess
import numpy as np
import pretty_midi
import soundfile as sf

parser = argparse.ArgumentParser()
parser.add_argument("--name", default="江南")
parser.add_argument("--output", type=Path)
parser.add_argument("--cache", type=Path)
parser.add_argument("--midi", type=Path, help="Explicit input MIDI for notation-based arrangements")
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
name = args.name
song_dir = args.output or root / "songs" / f"{name}-完整指弹转谱"
midi = pretty_midi.PrettyMIDI(str(args.midi or song_dir / f"{name}-异环36音完整指弹.mid"))
sample_rate = 22050
duration = json.loads((song_dir / "transcription-report.json").read_text(encoding="utf-8"))["duration_seconds"]
audio = np.zeros(round((duration + 0.1) * sample_rate), dtype=np.float32)
for instrument in midi.instruments:
    for note in instrument.notes:
        length = note.end - note.start
        t = np.arange(max(1, round((length + 0.025) * sample_rate)), dtype=np.float32) / sample_rate
        envelope = np.minimum(1, t / 0.006) * np.exp(-1.8 * t) * np.clip((length + 0.025 - t) / 0.025, 0, 1)
        frequency = pretty_midi.note_number_to_hz(note.pitch)
        signal = sum((0.48 ** (h - 1)) * np.sin(2 * np.pi * frequency * h * t) for h in range(1, 6))
        signal *= envelope * (note.velocity / 127) * 0.28
        first = round(note.start * sample_rate)
        count = min(len(signal), len(audio) - first)
        audio[first:first + count] += signal[:count]
audio *= 0.90 / max(0.90, float(np.max(np.abs(audio))))
cache = args.cache or root / "artifacts" / "transcription" / ("jiangnan" if name == "江南" else name)
cache.mkdir(parents=True, exist_ok=True)
wav_path = cache / "reference-preview.wav"
sf.write(wav_path, audio, sample_rate)
subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(wav_path), "-c:a", "libmp3lame", "-b:a", "128k", str(song_dir / f"{name}-36音转谱参考试听.mp3")], check=True)
print("Reference preview rendered (synthetic timbre, not recorded game audio).")
