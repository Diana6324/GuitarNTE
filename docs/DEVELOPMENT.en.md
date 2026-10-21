# Development and releases

[English](DEVELOPMENT.en.md) | [简体中文](DEVELOPMENT.md)

## Environment and architecture

Use Windows 10 / 11 x64 and the .NET 10 SDK. `global.json` allows the latest installed .NET 10 feature band. NuGet dependencies are pinned in per-project `packages.lock.json` files.

- `src/GuitarNTE.Core`: notation parsing, MIDI, pitch folding, bindings and playback scheduling.
- `src/GuitarNTE.App`: WPF UI, library persistence, native windows, hotkeys and keyboard output.
- `tests/GuitarNTE.Tests`: executable assertions, WPF smoke and optional real-input tests.
- `src/GuitarNTE.App/Strings`: matching `zh-CN.xaml` / `en.xaml` UI resource dictionaries.
- `UiText.T(chinese, english)`: bilingual dynamic messages used by both Core and App; language selection is thread-safe for background playback.

Internal identifiers, `GuitarNTE.exe`, `GUITAR_NTE_DATA` and the existing user-data directory remain stable for compatibility with previous versions.

## Build and test

```powershell
dotnet restore GuitarNTE.slnx --locked-mode
dotnet build GuitarNTE.slnx -c Release --no-restore
dotnet run --project tests/GuitarNTE.Tests -c Release --no-build
dotnet run --project tests/GuitarNTE.Tests -c Release --no-build -- --ui-headless
```

Tests are executable assertions; `dotnet test` is not the validation entry point. The WPF harness writes only to an isolated library and captures screenshots under `artifacts/ui-headless/中文路径`. It tests live language switching, invalid draft / pending binding preservation, English playback messages, persisted preferences and Chinese / English compact layouts, as well as existing library and playback behavior.

Optional flags: `--ui-smoke` opens desktop windows; `--realtime` adds about three minutes of real scheduling; `--native` opens a local receiver and sends real keys. The native test needs exclusive foreground focus; do not run it unattended or in CI. Preview and headless tests do not send native input.

For manual development, use an isolated library:

```powershell
$env:GUITAR_NTE_DATA = Join-Path $PWD 'artifacts/dev-data'
dotnet run --project src/GuitarNTE.App -c Release
Remove-Item Env:GUITAR_NTE_DATA
```

After changing dependencies, run `dotnet restore GuitarNTE.slnx --use-lock-file --force-evaluate`, commit the lock files and rerun validation.

## Localization

Add static UI labels to **both** resource dictionaries with identical keys, and reference them with `DynamicResource`. Keep both translations beside each other for dynamic messages. `AppSettings.Language` stores `zh-CN` or `en`; an absent / empty value follows Windows UI language. `UiLanguage.Apply` replaces the resource dictionary and updates Core's language selection.

Language switching saves a deep copy of settings without parsing the editor or capturing pending key edits. Do not rewrite existing song titles, track names, score text or file paths. Disable language changes while playback is active or import is running. Add validation for both languages and inspect normal / compact screenshot layouts when changing labels.

## Package and publish

```powershell
./build.ps1
```

The script builds and runs assertions, then publishes a compressed self-contained `win-x64` executable and copies text examples, bilingual documents and licenses. It creates `artifacts/NTE-Piano-Guitar-Player-win-x64.zip` and `artifacts/SHA256SUMS.txt` with hashes of both the ZIP and EXE. `-OutputName` selects a new artifact directory; `-SkipTests` is for packaging after tests have already passed. Nonempty output directories are rejected to prevent stale or personal files from entering the package. Runtime binaries are unsigned.

The protected `main` branch requires a PR, the GitHub Actions `build` check against current main, resolved conversations and linear history. Use squash or rebase; there is no administrator bypass. A single-maintainer repository currently requires zero external approvals.

Update the version, changelog and validation record on a feature branch, open a PR and wait for checks before merging. A `vMAJOR.MINOR.PATCH` tag triggers the same Windows build / assertions / WPF smoke / packaging, then publishes the ZIP and checksums in a GitHub Release. The workflow has read-only permissions by default; only its release job has `contents: write`. Action revisions are pinned and Dependabot proposes updates.

## Optional Python tools

`tools/` contains independent experimental score-preparation scripts, not a desktop audio-recognition feature. Their models and Python runtime are not bundled or included in desktop release acceptance. Use material you own or are authorized to process.

```powershell
py -3.10 -m venv .venv-transcribe
./.venv-transcribe/Scripts/python.exe -m pip install basic-pitch==0.4.0 onnxruntime librosa pretty_midi soundfile mido numpy
# Install FFmpeg separately and add it to PATH.
./.venv-transcribe/Scripts/python.exe tools/transcribe_video.py 'local-source.mp4' --name 'My example' --output 'songs/My example'
```

`--install` adds a non-overwriting copy to the local library. `--reuse` requires matching source hashes and analysis parameters. `adapt_score_midi.py` folds existing MIDI to the guitar range and requires source / license attribution; use a writable working copy because it creates `normalized-score.midi` beside the input. `synthesize_preview.py` renders a synthetic reference. See each script's `--help`.

Do not commit generated media, private MIDI, library JSON, models, caches, environments or secrets.
