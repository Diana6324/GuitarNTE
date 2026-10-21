"""Adapt an openly licensed score MIDI to GuitarNTE without processing protected audio."""
from __future__ import annotations

import argparse
import base64
from collections import defaultdict
import csv
import hashlib
import json
import os
from pathlib import Path
import uuid

import mido
import pretty_midi

KEYS = "A S D F G H J K L ; ' Enter Q W E R T Y U I O P [ ] 1 2 3 4 5 6 7 8 9 0 - =".split()
GLOBAL_META = {"set_tempo", "time_signature", "key_signature"}


def normalize_global_metadata(source: Path, destination: Path) -> None:
    midi = mido.MidiFile(source)
    if midi.type == 2:
        raise ValueError("Independent-sequence type 2 MIDI needs an explicit arrangement")
    global_events = {}
    other_tracks = []
    for track in midi.tracks:
        tick = 0
        events = []
        for message in track:
            tick += message.time
            if message.type in GLOBAL_META:
                key = (tick, message.type)
                value = message.copy(time=0)
                if key in global_events and global_events[key] != value:
                    raise ValueError(f"Conflicting {message.type} at MIDI tick {tick}")
                global_events[key] = value
            else:
                events.append((tick, message.copy()))
        other_tracks.append(events)
    canonical = mido.MidiFile(type=1, ticks_per_beat=midi.ticks_per_beat)
    conductor = mido.MidiTrack()
    previous = 0
    for (tick, _), message in sorted(global_events.items()):
        conductor.append(message.copy(time=tick - previous))
        previous = tick
    canonical.tracks.append(conductor)
    for events in other_tracks:
        track = mido.MidiTrack()
        previous = 0
        for tick, message in events:
            track.append(message.copy(time=tick - previous))
            previous = tick
        canonical.tracks.append(track)
    canonical.save(destination)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("--name", required=True)
    parser.add_argument("--title", required=True)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--source-url", required=True)
    parser.add_argument("--attribution", required=True)
    parser.add_argument("--license-url", required=True)
    parser.add_argument("--install", action="store_true")
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    source_hash = hashlib.sha256(args.source.read_bytes()).hexdigest()
    canonical_path = args.source.parent / "normalized-score.midi"
    normalize_global_metadata(args.source, canonical_path)
    source = pretty_midi.PrettyMIDI(str(canonical_path))
    original_midi = mido.MidiFile(canonical_path)
    duration = original_midi.length
    notes = []
    adjusted = 0
    pedal_extended = 0
    original_pitches = []
    for voice, instrument in enumerate(source.instruments):
        if instrument.is_drum:
            continue
        pedal_intervals = []
        pedal_start = None
        for cc in sorted(instrument.control_changes, key=lambda c: c.time):
            if cc.number != 64:
                continue
            if cc.value >= 64 and pedal_start is None:
                pedal_start = cc.time
            elif cc.value < 64 and pedal_start is not None:
                pedal_intervals.append((pedal_start, cc.time))
                pedal_start = None
        if pedal_start is not None:
            pedal_intervals.append((pedal_start, duration))
        for n in instrument.notes:
            original_pitches.append(n.pitch)
            pitch = n.pitch
            while pitch < 52:
                pitch += 12
            while pitch > 87:
                pitch -= 12
            adjusted += pitch != n.pitch
            end = n.end
            for pedal_on, pedal_off in pedal_intervals:
                if pedal_on <= end < pedal_off:
                    end = pedal_off
                    pedal_extended += int(end > n.end + 0.000001)
                    break
            notes.append(dict(pitch=pitch, start=n.start, end=end, velocity=n.velocity, voice=voice))
    if not notes:
        raise ValueError("No playable notes in score")
    # Merge only true simultaneous collisions. Unlike audio estimates, ornamental
    # note onsets in a notation MIDI are exact and should not be clustered.
    simultaneous = {}
    for n in notes:
        key = (n["pitch"], source.time_to_tick(n["start"]))
        if key not in simultaneous:
            simultaneous[key] = dict(n)
        else:
            prior = simultaneous[key]
            prior["end"] = max(prior["end"], n["end"])
            prior["velocity"] = max(prior["velocity"], n["velocity"])
    per_pitch = defaultdict(list)
    for n in simultaneous.values():
        per_pitch[n["pitch"]].append(n)
    playable = []
    for group in per_pitch.values():
        group.sort(key=lambda n: n["start"])
        for i, n in enumerate(group):
            if i + 1 < len(group):
                n["end"] = min(n["end"], group[i + 1]["start"] - 0.004)
            if n["end"] - n["start"] >= 0.01:
                playable.append(n)
    playable.sort(key=lambda n: (n["start"], n["pitch"]))
    output = mido.MidiFile(type=1, ticks_per_beat=original_midi.ticks_per_beat)
    total_ticks = source.time_to_tick(duration)
    conductor = mido.MidiTrack()
    output.tracks.append(conductor)
    conductor.append(mido.MetaMessage("track_name", name="Chopin Op34 No2 - licensed score adaptation"))
    absolute = 0
    previous = 0
    for msg in original_midi.tracks[0]:
        absolute += msg.time
        if msg.type in GLOBAL_META:
            conductor.append(msg.copy(time=absolute - previous))
            previous = absolute
    conductor.append(mido.MetaMessage("end_of_track", time=max(0, total_ticks - previous)))
    voices = sorted(set(n["voice"] for n in playable))
    for channel, voice in enumerate(voices):
        track = mido.MidiTrack()
        output.tracks.append(track)
        track.append(mido.MetaMessage("track_name", name="Score " + source.instruments[voice].name))
        track.append(mido.Message("program_change", channel=channel, program=24))
        events = []
        for n in playable:
            if n["voice"] != voice:
                continue
            start = source.time_to_tick(n["start"])
            end = max(start + 1, source.time_to_tick(n["end"]))
            events.append((start, 1, mido.Message("note_on", channel=channel, note=n["pitch"], velocity=n["velocity"])))
            events.append((end, 0, mido.Message("note_off", channel=channel, note=n["pitch"], velocity=0)))
        previous = 0
        for tick, _, event in sorted(events, key=lambda x: (x[0], x[1])):
            track.append(event.copy(time=tick - previous))
            previous = tick
        track.append(mido.MetaMessage("end_of_track", time=max(0, total_ticks - previous)))
    midi_path = args.output / f"{args.name}-异环36音.mid"
    output.save(midi_path)
    with (args.output / f"{args.name}-时间轴音名与按键.csv").open("w", encoding="utf-8-sig", newline="") as f:
        writer = csv.writer(f)
        writer.writerow(["起始秒", "结束秒", "持续秒", "音名", "游戏键", "原谱声部"])
        for n in playable:
            writer.writerow([round(n["start"], 6), round(n["end"], 6), round(n["end"] - n["start"], 6), pretty_midi.note_number_to_name(n["pitch"]), KEYS[n["pitch"] - 52], source.instruments[n["voice"]].name])
    project = dict(Version=1, Id=uuid.uuid5(uuid.NAMESPACE_URL, args.source_url + ":GuitarNTE:" + source_hash).hex, Title=args.title, Mode=2, Text="", Tonic="C4", Bpm=120, Speed=1, Transpose=0, MidiBase64=base64.b64encode(midi_path.read_bytes()).decode("ascii"), SelectedTracks=list(range(1, len(voices) + 1)))
    project_path = args.output / f"{args.name}-GuitarNTE曲谱.json"
    previous_project = project_path.read_bytes() if project_path.exists() else None
    project_path.write_text(json.dumps(project, ensure_ascii=False, indent=2), encoding="utf-8")
    if args.install:
        library = Path(os.environ.get("GUITAR_NTE_DATA", str(Path(os.environ["LOCALAPPDATA"]) / "GuitarNTE"))) / "Songs"
        library.mkdir(parents=True, exist_ok=True)
        target = library / (project["Id"] + ".json")
        if not target.exists() or (previous_project is not None and target.read_bytes() == previous_project):
            target.write_bytes(project_path.read_bytes())
        else:
            raise RuntimeError("Existing edited library project was preserved; import the new MIDI separately")
        print("LIBRARY", target)
    active = peak = 0
    for _, change in sorted([(n["start"], 1) for n in playable] + [(n["end"], -1) for n in playable]):
        active += change
        peak = max(peak, active)
    report = dict(source_type="openly licensed notation MIDI, not Apple Music audio", source_name=args.source.name, source_sha256=source_hash, source_url=args.source_url, attribution=args.attribution, license_url=args.license_url, protected_audio_processed=False, duration_seconds=round(duration, 6), raw_notes=len(notes), playable_notes=len(playable), octave_folded_notes=adjusted, pedal_extended_notes=pedal_extended, simultaneous_collisions_merged=len(notes) - len(simultaneous), original_pitch_min=min(original_pitches), original_pitch_max=max(original_pitches), max_simultaneous_notes=peak, tempo_changes=len(source.get_tempo_changes()[0]), manually_note_checked=False)
    (args.output / "transcription-report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    assert hashlib.sha256(args.source.read_bytes()).hexdigest() == source_hash
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
