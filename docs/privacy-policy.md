# QNote Privacy Policy

**Last updated: 2026-10-01**

QNote ("the app") is a personal, non-commercial open-source project
(https://github.com/Mr-Second/QNote). This policy explains what data the app
handles — in short: **everything stays on your device**.

## Data the app stores

All content you create in QNote — notes, categories, settings, images, and
backup archives — is stored **locally on your device only**, in the app's
private data directory (next to `QNote.exe` in a `data\` folder for the
portable edition, under `%APPDATA%` otherwise, or the MSIX package's
virtualized equivalent). Uninstalling the app may delete this data.

## Data the app collects or transmits

**None.** QNote:

- has **no user accounts** and no sign-in of any kind
- collects **no telemetry, analytics, or usage statistics**
- transmits **no note content or personal data** over the network
- contains **no advertising or tracking SDKs**

The app only accesses the network if you explicitly use a feature that
requires it (for example, the portable edition's manual "check for updates"
button, which contacts the GitHub Releases API, app updates delivered via
the Microsoft Store, or downloading a release from GitHub). Those requests go
to Microsoft or GitHub and are governed by their respective privacy policies.

## Crash diagnostics

If the app crashes, it writes a local crash dump file (memory snapshot and a
text summary) into the app's local `CrashDumps` directory. These files
**never leave your device automatically** — they exist solely for you to
share voluntarily when reporting a bug. You can delete them at any time.

## Backups

Backups (`.qns` files) are created only at your explicit request, are saved
to a location you choose, and can optionally be AES-256 encrypted. They are
never uploaded anywhere by the app.

## Third-party services

QNote does not integrate any third-party analytics, advertising, or cloud
services.

## Changes to this policy

Any changes to this policy will be published in the project repository and
noted in release notes.

## Contact

Questions or privacy concerns: open an issue at
https://github.com/Mr-Second/QNote/issues.
