# Contributing

[English](CONTRIBUTING.en.md) | [简体中文](CONTRIBUTING.md)

Read the [development guide](docs/DEVELOPMENT.en.md). Create a feature branch from `main`, make a focused change and open a PR describing the problem, resulting behavior, validation and remaining limits. Protected main requires successful `build` checks and resolved conversations; use squash or rebase.

For bug reports, include the app / Windows version, instrument, steps, expected result and actual result. Input problems should include whether the app and game run elevated. You may submit issues in English or Chinese. Remove account details and personal paths from logs and screenshots; share only minimal files you have permission to publish.

- Keep normal playback offline and original imported files unchanged.
- Tests must use isolated directories, never a real user library.
- Pause, stop, focus loss and failures must release keys pressed by the app.
- WPF control access belongs on the UI thread.
- MIDI deduplication must preserve meaningful playback variants.
- Update both languages and check English / Chinese layouts when changing UI text.
- Never commit private music, media, build output, credentials or personal configuration.

Run the Release build, executable assertions and `--ui-headless` before submitting. Include screenshots for UI changes. The `--native` test sends actual input and must not run unattended.

By contributing, you confirm you may provide the material under this repository's MIT license. Attribute third-party content separately. Follow the [community code of conduct](CODE_OF_CONDUCT.md); be respectful and constructive, and do not harass, threaten, discriminate or expose private information. Use GitHub's content-reporting feature for violations.
