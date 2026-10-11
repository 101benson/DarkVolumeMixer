## Dark Volume Mixer 2.0

A big update: a second layout, much more stable volume handling, and a fresh level meter.

### New
- **Compact list view** with horizontal faders. Toggle it in the title bar or in Settings.
- **Click to position:** click anywhere on a fader track and it jumps exactly there.
- **New level meters:** segmented green to red, never higher than the fader.
- **Taskbar entry** while the window is open. Minimize sends it to the tray.

### Improved
- Master mute now mutes all apps and restores their previous states.
- Proportional master control no longer drifts.
- Much lighter on the CPU: coalesced volume writes, background polling pauses when hidden.

### Fixed
- Sliders jumping back and forth, master drifting on its own
- Window opening off-screen, error when starting a second instance, missing icons at startup

### Download
Get **DarkVolumeMixer.exe** below. Windows 10/11, 64-bit, no .NET installation needed.

> Windows SmartScreen may warn about an unsigned app. Click **More info**, then **Run anyway**.

Full changes: see [CHANGELOG.md](https://github.com/101benson/DarkVolumeMixer/blob/main/CHANGELOG.md).
