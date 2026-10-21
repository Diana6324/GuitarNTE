"""One-off local polyphonic transcription; not a runtime dependency of GuitarNTE.

Use .venv-transcribe (Python 3.10) with basic-pitch==0.4.0.
The source video is read-only; all audio/model outputs are written separately.
"""
from __future__ import annotations

import argparse
import base64
import csv
import hashlib
import json
import os
from pathlib import Path
import subprocess
import uuid

import numpy as np
import onnxruntime as ort
import pretty_midi
import soundfile as sf
import mido
from basic_pitch import ICASSP_2022_MODEL_PATH
from basic_pitch.inference import Model, predict


KEYS = "A S D F G H J K L ; ' Enter Q W E R T Y U I O P [ ] 1 2 3 4 5 6 7 8 9 0 - =".split()


def digest(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for block in iter(lambda: f.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def fold(pitch: int) -> int:
    while pitch < 52:
        pitch += 12
    while pitch > 87:
        pitch -= 12
    return pitch


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("video", type=Path)
    parser.add_argument("--name", default="江南", help="Song name used in export filenames")
    parser.add_argument("--project-title", help="Library title; defaults to the song name plus transcription description")
    parser.add_argument("--harmonic", action="store_true", help="Use local harmonic/percussive separation before inference for mixed audio")
    parser.add_argument("--onset", type=float, default=0.5)
    parser.add_argument("--frame", type=float, default=0.3)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--install", action="store_true", help="Add a non-overwriting copy to the app's local library")
    parser.add_argument("--reuse", action="store_true", help="Reuse prior model-output notes to adjust exports without inference")
    parser.add_argument("--cache", type=Path)
    args = parser.parse_args()
    video = args.video.resolve()
    name = args.name.strip()
    if not name or any(c in name for c in '<>:"/\\|?*'):
        parser.error("Song name must be a valid Windows filename stem")
    if not (0 < args.onset < 1 and 0 < args.frame < 1):
        parser.error("Onset and frame thresholds must be between 0 and 1")
    out = (args.output or Path("songs") / f"{name}-完整指弹转谱").resolve()
    out.mkdir(parents=True, exist_ok=True)
    before = digest(video)
    cache = (args.cache or Path("artifacts/transcription") / ("jiangnan" if name == "江南" else name)).resolve()
    cache.mkdir(parents=True, exist_ok=True)
    audio_path = cache / "analysis-audio.wav"
    event_path = cache / "transcribed-events.json"
    manifest_path = cache / "source.json"
    if args.reuse:
        if not event_path.exists() or not audio_path.exists() or not manifest_path.exists():
            parser.error("Reuse requires audio, event and source-hash cache files")
        manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
        if manifest["sha256"] != before:
            parser.error("Cached source hash does not match this video; rerun without --reuse")
        if manifest.get("harmonic", False) != args.harmonic or manifest.get("onset", 0.5) != args.onset or manifest.get("frame", 0.3) != args.frame:
            parser.error("Cached preprocessing or detection thresholds differ; rerun without --reuse")
        raw_events = json.loads(event_path.read_text(encoding="utf-8"))
    else:
        subprocess.run(["ffmpeg", "-hide_banner", "-loglevel", "error", "-y", "-i", str(video), "-vn", "-ac", "1", "-ar", "22050", "-c:a", "pcm_s16le", str(audio_path)], check=True)
        inference_audio = audio_path
        if args.harmonic:
            import librosa
            samples, sr = sf.read(audio_path)
            harmonic = librosa.effects.harmonic(samples, margin=2.0)
            harmonic *= 0.95 / max(0.05, float(np.max(np.abs(harmonic))))
            inference_audio = cache / "harmonic-audio.wav"
            sf.write(inference_audio, harmonic, sr)
        print("Audio extracted; running local ONNX model (2 CPU threads)", flush=True)
        opts = ort.SessionOptions()
        opts.intra_op_num_threads = 2
        opts.inter_op_num_threads = 1
        model = Model.__new__(Model)
        model.model_type = Model.MODEL_TYPES.ONNX
        model.model = ort.InferenceSession(str(ICASSP_2022_MODEL_PATH), sess_options=opts, providers=["CPUExecutionProvider"])
        outputs, raw_midi, events = predict(inference_audio, model, onset_threshold=args.onset, frame_threshold=args.frame, minimum_note_length=90, minimum_frequency=65.4, maximum_frequency=2637, melodia_trick=True)
        np.savez_compressed(cache / "model-activations.npz", **outputs)
        raw_midi.write(str(out / f"{name}-原始多声部识别.mid"))
        raw_events = [[float(a), float(b), int(p), float(v)] for a, b, p, v, _ in events]
        event_path.write_text(json.dumps(raw_events, ensure_ascii=False), encoding="utf-8")
        manifest_path.write_text(json.dumps({"sha256": before, "name": video.name, "harmonic": args.harmonic, "onset": args.onset, "frame": args.frame}, ensure_ascii=False), encoding="utf-8")

    audio, sample_rate = sf.read(audio_path)
    audio_duration = len(audio) / sample_rate
    # Keep expressive timing: no global tempo grid or forced beat quantization.
    filtered = []
    removed = 0
    for start, end, pitch, confidence in raw_events:
        if end <= start or start < 0 or end - start < 0.065 or (end - start < 0.11 and confidence < 0.20):
            removed += 1
            continue
        filtered.append((start, min(end, audio_duration), pitch, confidence))

    # Quantize to 1 ms only; merge same-pitch onsets within 8 ms after folding.
    groups: dict[int, list[list[float]]] = {}
    folded_count = 0
    for start, end, pitch, confidence in sorted(filtered):
        target = fold(pitch)
        folded_count += target != pitch
        start, end = round(start, 3), round(end, 3)
        arr = groups.setdefault(target, [])
        if arr and start - arr[-1][0] <= 0.008:
            arr[-1][1] = max(arr[-1][1], end)
            arr[-1][2] = max(arr[-1][2], confidence)
        else:
            arr.append([start, end, confidence])

    playable = []
    for pitch, arr in groups.items():
        for i, (start, end, confidence) in enumerate(arr):
            if i + 1 < len(arr):
                end = min(end, arr[i + 1][0] - 0.004)
            if end - start >= 0.015:
                playable.append((start, end, pitch, confidence))
    playable.sort()
    if not playable:
        raise RuntimeError("No playable notes detected; source and app library remain untouched")
    midi = pretty_midi.PrettyMIDI(initial_tempo=120, resolution=960)
    # Split by keyboard register; all three tracks are selected in the project.
    instruments = [pretty_midi.Instrument(24, name=name) for name in ["Low guitar E3-D#4", "Mid guitar E4-D#5", "High harmonics E5-D#6"]]
    for start, end, pitch, confidence in playable:
        velocity = int(np.clip(round(confidence * 127), 40, 120))
        instruments[(pitch - 52) // 12].notes.append(pretty_midi.Note(velocity=velocity, pitch=pitch, start=start, end=end))
    midi.instruments.extend(instruments)
    midi_path = out / f"{name}-异环36音完整指弹.mid"
    midi.write(str(midi_path))
    # Preserve the video's trailing silence as track-end delta times.
    midi_file = mido.MidiFile(midi_path)
    total_ticks = round(audio_duration * midi_file.ticks_per_beat * 2)
    for track in midi_file.tracks:
        if track and track[-1].type == "end_of_track":
            track[-1].time += max(0, total_ticks - sum(msg.time for msg in track))
    midi_file.save(midi_path)

    with (out / f"{name}-时间轴音名与按键.csv").open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(["起始秒", "结束秒", "持续秒", "音名", "游戏键", "模型强度"])
        for start, end, pitch, confidence in playable:
            writer.writerow([f"{start:.3f}", f"{end:.3f}", f"{end-start:.3f}", pretty_midi.note_number_to_name(pitch), KEYS[pitch - 52], f"{confidence:.3f}"])

    identity = "jiangnan" if name == "江南" else name
    project = dict(Version=1, Id=uuid.uuid5(uuid.NAMESPACE_URL, "GuitarNTE:" + identity + ":" + before).hex, Title=args.project_title or f"{name} · 视频完整指弹转谱", Mode=2, Text="", Tonic="C4", Bpm=120, Speed=1, Transpose=0, MidiBase64=base64.b64encode(midi_path.read_bytes()).decode("ascii"), SelectedTracks=list(range(1, 1 + sum(bool(inst.notes) for inst in instruments))))
    # pretty_midi writes one global tempo track, then the three instrument tracks.
    project_path = out / f"{name}-GuitarNTE曲谱.json"
    previous_project = project_path.read_bytes() if project_path.exists() else None
    project_path.write_text(json.dumps(project, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.install:
        library = Path(os.environ.get("GUITAR_NTE_DATA", str(Path(os.environ["LOCALAPPDATA"]) / "GuitarNTE"))) / "Songs"
        library.mkdir(parents=True, exist_ok=True)
        destination = library / (project["Id"] + ".json")
        if not destination.exists() or (previous_project is not None and destination.read_bytes() == previous_project):
            destination.write_bytes(project_path.read_bytes())
        else:
            print("Existing library edits preserved; import the new MIDI separately.", flush=True)
        print("Library copy:", destination, flush=True)

    concurrency = sorted([(s, 1) for s, _, _, _ in playable] + [(e, -1) for _, e, _, _ in playable])
    active = peak = 0
    for _, delta in concurrency:
        active += delta
        peak = max(peak, active)
    after = digest(video)
    if before != after:
        raise RuntimeError("Source hash changed")
    report = dict(source_name=video.name, source_sha256=before, source_unchanged=before == after, duration_seconds=round(audio_duration, 3), model="Spotify Basic Pitch 0.4.0 ICASSP 2022 / ONNX CPU", preprocessing="harmonic-percussive separation (harmonic component)" if args.harmonic else "mono resample", onset_threshold=args.onset, frame_threshold=args.frame, minimum_note_length_ms=90, raw_notes=len(raw_events), cleaned_notes=len(filtered), removed_short_weak_notes=removed, octave_folded_notes=folded_count, playable_notes=len(playable), max_simultaneous_notes=peak, first_note_seconds=playable[0][0], last_note_seconds=max(n[1] for n in playable), original_pitch_min=min(n[2] for n in filtered), original_pitch_max=max(n[2] for n in filtered), mean_model_strength=round(float(np.mean([n[3] for n in playable])), 4), manually_note_checked=False)
    (out / "transcription-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2), flush=True)


if __name__ == "__main__":
    main()
