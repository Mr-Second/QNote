# QNote

[![CI](https://github.com/Mr-Second/QNote/actions/workflows/ci.yml/badge.svg)](https://github.com/Mr-Second/QNote/actions/workflows/ci.yml)
[![Release](https://github.com/Mr-Second/QNote/actions/workflows/release.yml/badge.svg)](https://github.com/Mr-Second/QNote/actions/workflows/release.yml)

A lightweight sticky-note app for Windows, built with **WinUI 3**.

QNote is a from-scratch C# / WinUI 3 rewrite of an earlier Qt/QML
implementation, with one north star: **low memory footprint**. It idles at
roughly **45–50 MB** of RAM where the Qt build consumed ~250 MB — lean enough
to keep running all day without thinking about it.

## Screenshots

| | |
|---|---|
| ![Main view — categories, note list and rich-text editor](docs/screenshots/1-main.png) | ![Instant full-text search with keyword highlighting](docs/screenshots/2-search.png) |
| ![Settings panel](docs/screenshots/3-settings.png) | ![Rich-text notes](docs/screenshots/4-richtext.png) |

## Features

- **Notes CRUD** with an RTF rich-text editor and a formatting toolbar
  (bold / italic / lists / colors / …)
- **Instant search** powered by SQLite FTS5, with keyword highlighting
- **Categories** in the sidebar, drag-to-reorder
- **Image insertion** in notes; double-click opens the system image viewer
- **Backup & restore** — `.qns` archives (ZIP + AES-256 encrypted) with three
  restore modes
- **Top-edge auto-hide** — dock notes to the screen edge; plus a global hotkey
- **System tray** with close-to-tray and launch-at-startup
- **Settings panel** — theme, sort order, density, always-on-top,
  remember window position
- **Crash dumps + rolling logs** for diagnostics

## Install

Download the latest `.msix` from
[Releases](https://github.com/Mr-Second/QNote/releases) and follow
[docs/msix-install.md](docs/msix-install.md).

Releases are currently signed with a **self-signed certificate**, so a one-time
certificate trust step is required (explained in the install doc). Free,
publicly trusted code signing via [SignPath.io](https://signpath.io) is being
applied for — once approved, packages will install with a plain double-click.
See [docs/code-signing-policy.md](docs/code-signing-policy.md).

**Requirements:** Windows 10 19041+ / Windows 11, x64. Windows App Runtime
2.3+ (usually preinstalled on Windows 11; the installer will tell you if it is
missing).

## Build from source

Requires **.NET 10 SDK** on Windows.

```powershell
# Build
dotnet build QNote.slnx -c Debug

# Run the app
dotnet run --project src/QNote/QNote.csproj -c Debug

# Run tests (Core layer, headless)
dotnet test tests/QNote.Tests/QNote.Tests.csproj -c Debug
```

## Tech stack

| Concern | Choice |
|---|---|
| UI | WinUI 3 / Windows App SDK 2.3.1, XAML with `x:Bind` |
| Runtime | .NET 10 |
| MVVM | CommunityToolkit.Mvvm |
| Data | SQLite via Microsoft.Data.Sqlite, FTS5 full-text search |
| Tests | xUnit |
| Distribution | MSIX installer |

## License

[MIT](LICENSE)
