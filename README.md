<div align="center">

# Dark Volume Mixer

**A fast, good-looking Windows volume mixer for every app you run.**
Per-app faders, live level meters, separate Discord chat & voice, favorites, and a compact list view.

[![Latest release](https://img.shields.io/github/v/release/101benson/DarkVolumeMixer?style=flat-square&color=FF9500)](https://github.com/101benson/DarkVolumeMixer/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/101benson/DarkVolumeMixer/total?style=flat-square&color=00D294)](https://github.com/101benson/DarkVolumeMixer/releases)
[![License: MIT](https://img.shields.io/github/license/101benson/DarkVolumeMixer?style=flat-square)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-444?style=flat-square)

<img width="820" height="386" alt="DarkVolumeMixer_v2 0 1" src="https://github.com/user-attachments/assets/c6bbc1d7-613a-454e-80f7-27bf476d3409" />
<img width="340" height="386" alt="DarkVolumeMixer_v2 0 1_vert" src="https://github.com/user-attachments/assets/514fecbc-8936-458b-ad93-f6a01d02cb47" />


<!-- TODO: add a screenshot of the new compact list view here -->

</div>

## Download

1. Open the **[latest release](https://github.com/101benson/DarkVolumeMixer/releases/latest)**.
2. Download `DarkVolumeMixer.exe`.
3. Run it. No installer and no .NET installation needed.

> **Windows SmartScreen:** the app is not code-signed, so Windows may show a warning on first start.
> Click **More info** and then **Run anyway**. The full source code is in this repository.

## Features

- **Per-app volume control** – every running app gets its own fader. Click anywhere on the track to jump straight to that value, drag, or use the mouse wheel.
- **Live level meters** – segmented green / yellow / orange / red meters on a dB scale, capped at the fader position.
- **Master section** – master fader and mute with output device selection. Optionally scales all apps proportionally with the master.
- **Two layouts** – classic vertical faders, or a compact list with horizontal faders (switch in the title bar or in Settings).
- **Discord split** – the Discord client and your voice chat show up as separate, independent streams, with an optional volume limiter.
- **Favorites** – click an app icon to pin it to the front. Drag cards to reorder them; the order is remembered.
- **Tray & taskbar** – visible in the taskbar while open, hides completely in the system tray when minimized.
- **Light on resources** – volume changes are coalesced, and background polling pauses while the window is hidden, so it stays out of the way during games.
- **Remembers your setup** – window size, position, layout, pinned apps and options are stored in `%AppData%\DarkVolumeMixer\settings.json`.

## Usage

| Action | How |
| --- | --- |
| Set a volume | Click or drag on the fader, or scroll the mouse wheel over a card (2 % steps) |
| Mute an app / everything | Speaker button on the card / in the master section |
| Pin an app | Click its icon |
| Reorder | Drag a card onto another one |
| Switch layout | List button in the title bar, or **Settings** |
| Minimize to tray | Minimize button in the title bar. Click the tray icon to bring it back |

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```powershell
git clone https://github.com/101benson/DarkVolumeMixer.git
cd DarkVolumeMixer
dotnet run
```

Single-file release build:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Pushing a tag like `v2.0.1` builds and publishes a release automatically via GitHub Actions (see `.github/workflows/release.yml`).

## Contributing

Contributions are welcome.

- **Found a bug?** [Open an issue](https://github.com/101benson/DarkVolumeMixer/issues) and describe what happened, which audio device you use, and how to reproduce it.
- **Want to improve something?** Fork the repo and open a pull request. Small, focused changes are easiest to review.

See the [changelog](CHANGELOG.md) for what changed in each version.

## Credits & license

Built with [NAudio](https://github.com/naudio/NAudio). Released under the [MIT License](LICENSE).
