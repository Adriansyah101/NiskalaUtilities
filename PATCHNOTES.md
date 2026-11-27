# Patch notes

Changes in each release of Niskala Utilities, newest first.

## v0.1.3 (2026-11-27)

### Added

- **Update prompt.** When the check at start finds a newer release, a dialog shows this copy's version, the new one and the first items of its release notes. **Update now** opens Updates and starts the download, checksum check and install; **Review first** opens Updates with the update button ready; **Later** closes it for this session and the dot on Updates stays. It waits until the window is ready and no other dialog, menu or removal is open, and speaks English and Indonesian.
- **Sidebar motion.** A category's pages slide open and shut, the highlight pill and the marker on the guide line move to the new page, a pressed item gives a small press, and the menu button folds the rail smoothly. A quick second click turns a motion around at once, and with Windows' animation effects off every change is instant.

## v0.1.2 (2026-11-27)

### Changed

- **Rail categories.** Apps, Cleaning, Performance and Maintenance are now items with their own icon that open and close their pages with a click. The category of the page on screen opens by default and carries an accent pill; another category you open gets a neutral pill. Its pages sit indented beside a thin guide line, and the page on screen is marked on that line.
- Opening a page from `Ctrl+K` or from another page opens its category and highlights the page.
- The rail is tighter: with all four categories open it fits a window of the default size without a scroll bar. The menu button still folds it to icons, and a category's icon then lists its pages in a menu.
- Switching the language keeps the page on screen and the categories that are open.
- **Dimmed list behind the uninstall panel.** While an uninstall, Force Remove, Steam or Batch Remove panel is open, a dark layer covers the app list behind it.

### Fixed

- The theme button in the rail names the right theme after the theme is switched from the command palette.

## v0.1.1 (2026-11-27)

### Added

- **Updates.** A new page in the rail's footer and `niskala update check|install [--dry-run]` find the latest GitHub release, download the package for this copy, verify its SHA-256 checksum against `SHA256SUMS.txt` and install it.
- **Installer.** `NiskalaUtilities-Setup-v0.1.1-win-x64.exe` installs to `Program Files\Niskala Utilities` with Start menu and desktop shortcuts, an optional `PATH` entry for the command line and a clean uninstall.
- **Release notes.** These patch notes ship inside the app and in `lib\PATCHNOTES.md`, and the Updates page shows them next to the notes of the latest GitHub release.
- **Program icons in Resource Control.** Startup entries, services, scheduled tasks and context-menu handlers show the icon embedded in their program; a program without one keeps the kind's symbol.

### Changed

- The rail is open and docked when the app starts and folds with the menu button.
- System Repair shows its progress as a number (`42%`) beside a bar that fills from 0 to 100 as `sfc`, DISM and `chkdsk` report it; a run that ends normally says only `Finished` and how long it took.
- The duplicate search item at the bottom of the rail is gone; the search box in the title bar and `Ctrl+K` open the command palette.
- Smaller packages: the desktop app ships only the WinUI part of the Windows App SDK, both front ends are compiled for size, and only English and Indonesian resources are kept.

| Package | v0.1.0 | v0.1.1 |
|---|---|---|
| Desktop app zip | 55.5 MB | 29.1 MB |
| Command line zip | 7.7 MB | 7.7 MB |

### Fixed

- History & backups no longer closes the app when it lists journals written before Resource Control existed.
- The command line describes itself as `Niskala Utilities CLI`, and every executable carries the Niskala icon.

## v0.1.0 (2026-11-26)

### Added

- **Uninstaller.** Uninstall with a leftover scan, Force Remove, Batch Remove, the Target Picker for windows, desktop icons and tray icons, and the Install Tracker that records what an installer changes.
- **Leftover scanner.** Files, folders, registry keys, services, scheduled tasks, firewall rules, shortcuts and Steam game records, each scored and sorted into Safe and Review.
- **Guardian.** Every removal is journaled with a registry backup and an optional restore point, and can be restored from History & backups.
- **Cleaner.** Quick Scan and Selective Clean from declarative JSON rules, browser traces and SQLite compaction.
- **Registry Integrity.** File types, shared files, COM classes, firewall rules and services that point at missing programs.
- **Resource Control.** Startup entries, third-party services, scheduled tasks and context-menu handlers with a boot-impact estimate.
- **Process Suspend.** Freezing, efficiency mode and CPU caps for idle background processes.
- **Toolkit.** Storage Map, Duplicate Scan and Secure Wipe.
- **System Repair.** DISM, SFC, network resets, DNS flush and an online disk scan from a closed list.
- Two front ends on one engine: `Niskala.exe`, a WinUI 3 desktop app in English and Indonesian, and `niskala.exe`, a command line with JSON output.
