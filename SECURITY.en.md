# Security policy

[English](SECURITY.en.md) | [简体中文](SECURITY.md)

The latest public version and `main` are maintained. Upgrade before reproducing issues on older releases.

Use [private vulnerability reporting](https://github.com/Diana6324/NTE-Piano-Guitar-Player/security/advisories/new) for potential code execution, file-write boundary issues, data disclosure or privilege problems. Do not post exploits, credentials or private libraries in public issues. If private reporting is unavailable, open an issue without sensitive details to request a private channel.

Include the affected version, minimal reproduction and impact. This is a community-maintained project with no guaranteed response deadline. The app reads selected score files and uses Windows SendInput; UAC elevation requires manual confirmation. Reproduce with isolated data and avoid sending input to untrusted targets.
