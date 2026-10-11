# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [2.0.0] - 2026-10-11

### Added
- Compact list view with horizontal faders, switchable in the title bar and in Settings
- Click anywhere on a fader track to jump straight to that value
- Segmented level meters (green to red) on a dB scale, capped at the fader position
- Taskbar entry while the window is open; the minimize button now sends the app to the tray
- Version number shown in the Settings window

### Changed
- Master mute now mutes all apps and restores their previous mute states on unmute
- Proportional master control rewritten: no more drift, and app levels survive dragging the master to zero
- Discord client and voice streams are now assigned deterministically
- Settings are written in the background and atomically; a corrupt settings file is backed up instead of overwritten

### Fixed
- Volume sliders jumping back and forth (for example between 100 % and 90 %)
- Master volume drifting or reverting on its own after being set
- Window opening off-screen after being minimized or after a monitor change
- Error when starting a second instance
- Missing app icons right after startup

### Performance
- Volume changes are coalesced so only the latest value is sent to Windows
- Background polling pauses while the window is hidden or minimized
- Fewer process lookups and layout passes during session scanning

## [1.0.0]

- Initial version
