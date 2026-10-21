# NTE Piano & Guitar Player — User guide

[English](USER_GUIDE.en.md) | [简体中文](USER_GUIDE.md) · [Project home](../README.en.md)

## Language and first launch

Download the portable ZIP from [Releases](https://github.com/Diana6324/NTE-Piano-Guitar-Player/releases/latest), extract it and run `GuitarNTE.exe`. Choose **中文** or **English** at the top right. The change applies immediately and is saved in `settings.json` independently of score validation. First use follows Windows UI language: Chinese for Chinese Windows, English otherwise.

Switching preserves the editor, selected score, search terms, track choices and pending key-map edits, including invalid numeric drafts. Stop playback before changing language. Import temporarily disables the workspace. Existing user titles, score comments and MIDI track names are not translated. Native Windows dialog controls and system error details use the Windows language.

## Basic workflow

1. Enter the matching piano or guitar interface in the game and confirm its bindings.
2. Choose a library score, click **New** for text notation, **Import MIDI** for one file or **Import folder** for a directory tree.
3. Select the intended MIDI tracks. Click **Validate** and review duration, octave folding and merged notes.
4. Try **Preview** to inspect mapping without input. It produces no local sound.
5. Set speed, transpose, countdown, key duration and loop count. Refresh and select the game window.
6. Click **Play**, then return to the game before countdown ends. Alternatively use F8 while in the game.
7. **F8** pauses / resumes; resuming starts another countdown. **F9** stops and returns to the beginning. Switching away from the target pauses and releases held keys.

Default settings: 120 BPM for text scores, 1× speed, no transpose, 5 s countdown, 40 ms short tap, one loop. Ranges: speed 0.25–4, transpose −48 to +48 semitones, countdown 1–30 s, tap 10–500 ms, loops 1–100.

**Sustain notes** holds each key for its own score duration and releases it during rests. Chord notes can end separately. Repeated pitches have a 4 ms release gap. Turning sustain off uses fixed-duration taps. Pausing releases all notes; resuming continues at the next unsent note and does not retrigger an already sent sustained note. Velocity, pedal timbre and original instrument sounds cannot be reproduced by keyboard input.

## Text notation

Separate notes with spaces. `|` is an optional bar divider; `//` starts a line comment. Add a positive beat count after `:`; omitted duration means one beat. Use a decimal point, for example `0.5`; fraction syntax such as `1/2` is unsupported.

### Numbered notation

The default tonic is `1=C4`; change it to D4, Bb3 or another note in the tonic field.

```text
// Frere Jacques
1 2 3 1 | 1 2 3 1
3 4 5:2 | 3 4 5:2
5:0.5 6:0.5 5:0.5 4:0.5 3 1
1 5, 1:2 | 1 5, 1:2
```

- `0:1` rests for one beat.
- `1'` moves up one octave; `5,` moves down. Octave marks can repeat.
- `#4` and `b7` alter notes of the tonic's major scale by a semitone.
- `[1 3 5]:2` triggers a chord lasting two beats. Chord members cannot have separate durations or rests.

### Note names

```text
E4:1 F#4:0.5 [G4 B4]:2 R:1
[A3 C4 E4]:2 Bb3:0.5 C5:0.5
```

`R` is a rest. Note names support `#` and lowercase `b`. Syntax errors include line and column information and position the editor selection at the error.

## MIDI import and duplicates

Standard MIDI files retain their tempo changes. Select multiple tracks after import; MIDI channel 10 percussion is excluded by default. Limits are 32 MB per file, 200,000 playable notes and 24 hours.

**Import folder** recursively reads `.mid` / `.midi`, ignoring extension case and not following directory links. Each distinct MIDI is saved as a self-contained library copy with melodic tracks selected. The app shows progress and a final count of added, duplicate and failed items. Invalid, empty, oversized or inaccessible files are skipped with a copyable failure report. Original files remain unchanged; imported copies continue working if the originals move.

Duplicate identity is the SHA-256 hash of the original MIDI bytes. Renamed or relocated identical files are skipped; same-title files with different content remain separate. At startup, fully duplicate historical projects are archived into `DuplicateBackups`, retaining the newest saved version. Differences in selected tracks, speed, transpose or text notes keep variants distinct. To recover a backup, close the app, move its JSON back into `Songs`, and adjust relevant variant settings before restarting to avoid identical duplicate cleanup again.

Transpose is applied first. Notes outside the instrument's 36-note range are then folded by octaves without changing pitch class. Simultaneous notes that land on the same pitch are merged. Folding changes the original voicing; choose tracks and transpose accordingly. Start and end rests are preserved. Already folded guitar MIDI does not regain its original extended range when switched to piano.

## Guitar key map

| Register | Notes | Default keys |
|---|---|---|
| High | E5 F5 F#5 G5 G#5 A5 A#5 B5 C6 C#6 D6 D#6 | 1 2 3 4 5 6 7 8 9 0 - = |
| Middle | E4 F4 F#4 G4 G#4 A4 A#4 B4 C5 C#5 D5 D#5 | Q W E R T Y U I O P [ ] |
| Low | E3 F3 F#3 G3 G#3 A3 A#3 B3 C4 C#4 D4 D#4 | A S D F G H J K L ; ' Enter |

Expand **36-note key map**, edit key names and click **Apply keys**. Supported names include letters, digits, common punctuation, Enter, Space, Tab and F1–F12. Both global hotkeys must be different and must not overlap instrument base keys. **Reset keys** fills defaults only for the current instrument; click Apply to save.

## Piano key map

Choose **Piano · 36 notes** under Playback settings. Piano and guitar keep independent custom maps.

| Register | White keys, degrees 1–7 |
|---|---|
| High | Q W E R T Y U |
| Middle | A S D F G H J |
| Low | Z X C V B N M |

The chromatic order is `1 #1 2 b3 3 4 #4 5 #5 6 b7 7`. `#1 / #4 / #5` use Shift plus the corresponding white key. `b3 / b7` use Ctrl plus white-key degrees 3 / 7. Middle C#4 is `Shift+A`; Eb4 is `Ctrl+D`.

The app assumes middle `1=C4`, so piano spans C3–B5. If the game's absolute octave differs, use transpose. Modifiers are pressed only while triggering notes, then released. Chords using different modifiers are triggered in batches. Notes sharing a physical letter key cannot sustain independently; a new note retriggers that key and shortens the previous note. Use Preview to inspect the map, then verify the result in the game.

## Library and troubleshooting

- Search titles instantly with case-insensitive English / Chinese terms separated by spaces. All terms must match. `Ctrl+F` focuses search; `Esc` or × clears it. Filtering preserves unsaved editor content. New and newly imported scores clear the filter so they remain visible.
- Back up `%LocalAppData%\GuitarNTE`, especially `Songs`, `settings.json` and `DuplicateBackups`. Naming changes retain this location.
- Valid edits autosave when switching scores or closing; **Save score** saves explicitly. Invalid notation can be saved as a draft but must validate before playback. Deleting a library copy asks for confirmation and does not delete the imported source.
- If hotkeys are occupied, change them and apply. The app attempts to restore the previous hotkeys on registration failure.
- After restarting the game, refresh and select its new window.
- If the game runs as administrator and the app does not, Windows UIPI blocks input. **Restart as admin** saves the current project and settings and requests UAC confirmation. Cancelling UAC leaves the original window open. The app does not confirm system prompts automatically.
- Release Shift, Ctrl, Alt and Win before playing. If scheduling is over 250 ms late, playback pauses to avoid sending a burst of overdue notes.
- A successful SendInput call does not guarantee game acceptance. Confirm the active instrument, bindings and target window. The tool cannot bypass game restrictions.

Image recognition, audio transcription and online song search are not built into the desktop app. Separate experimental Python helpers are described in the [development guide](DEVELOPMENT.en.md).
