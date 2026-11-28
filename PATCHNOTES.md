# Patch notes

Changes in each release of Niskala Utilities, newest first.

## v0.3.1 (2026-11-28)

### Changed

- **Much faster analysis on NTFS.** Drive Optimizer now reads the drive's file table from start to end and opens the files in the order they are stored in it, instead of walking every folder, so the disk head no longer jumps across the drive. On a 5 TB USB hard disk with 1.7 million files, an analysis that took between 18 and 89 minutes now took 13 minutes. FAT32 and exFAT drives, and any drive whose file table cannot be read, are still analyzed folder by folder.
- **Results are kept while the app runs.** The last analysis of each drive stays available when you switch drives, and every defragment run updates it, so a second run or **Defrag checked** starts at once. Once a drive has a result, **Analyze** becomes **Re-analyze** and scans the whole drive again. A different disk on the same letter is never given the old result, and each file is checked again just before it is moved.
- The page file, the hibernation and swap files, the file system's own files, `$Extend` and `System Volume Information` are left alone in every folder, not only at the root of the drive.

### Fixed

- **Cancel no longer splits a file.** Cancel stops the queue at once, but a file that is already being moved, up to 2 GB, is finished first, so it never ends up in more pieces than before. The page says **Stopping after current file…** until it is done and then **Stopped**.
- A run cancelled while its last file was being moved is reported as stopped.
- A file that was already in one piece by the time it was reached is no longer counted as joined.
- **Consolidate Free Space** puts each joined file into the lowest free space that holds it and compacts it afterwards like any other small file, so it no longer pushes data towards the end of the drive and the free space at the end grows.
- **Full Zone Optimization** first moves documents out of the fast zone and only then places programs and libraries there, so programs no longer end up outside the fast zone for lack of room.
- A file that is joined and then compacted in the same run is counted as one moved file.

## v0.3.0 (2026-11-27)

### Added

- **Drive Optimizer.** A new page, first under **Maintenance** and in `Ctrl+K` (search for defrag, TRIM, SSD or HDD), lists every fixed drive with its label, an **HDD** or **SSD** badge, the file system, capacity and free space, how fragmented it is and the action it gets.
- Hard disks and solid-state drives are told apart automatically, including hard disks in USB enclosures that hide it, which are recognised by their spindle speed; the drive line shows the bus and the speed, for example `HDD · NTFS · USB · 4,800 rpm`. A hard disk is **defragmented**; a solid-state drive gets **Optimize (TRIM)** and its clusters are never moved, whatever its enclosure reports. An external USB hard disk can usually be analyzed and defragmented without running as administrator.
- Three modes for hard disks: **Quick Defrag** joins fragmented files into free space that fits them; **Consolidate Free Space** also fills the gaps left by small files so free space ends up in large blocks; **Full Zone Optimization** puts programs and libraries on the fast outer tracks, everyday files in the middle and large archives, disc images and videos on the inner tracks.
- **Live cluster map.** A grid of blocks shows free space, contiguous files, fragmented files, the archive zone and system or locked areas, fills in while the drive is analyzed and changes as files are moved. Its colours and legend work in the light and dark theme. Analyzing reads only file system information, keeps its progress bar moving through folders full of small files, steps aside while you use the disk and stops at once when you cancel.
- **Fragmented files** lists the most fragmented files with their fragment count, size and target zone; **Defrag checked** joins only the files you tick. **Drive health & summary** shows the clusters, free space, fragmented files, excess fragments, the largest free block and the files per speed zone.
- Defragmenting runs at background priority, moves files in small steps and pauses while you use the disk, so the PC stays responsive. The page file, the hibernation and swap files, the file system's own metadata and every `System Volume Information` folder are left alone, and files in use are skipped without stopping the run. Free space that another program takes while the run goes on is noticed, and the file is moved somewhere else instead of being reported as locked.

### Changed

- **Maintenance is shorter.** It now holds **Drive Optimizer**, **System Repair** and **History & backups**. Storage Map, Duplicate Scan and Secure Wipe are gone; a page from an older version that is no longer there opens **Apps**.
- **One package.** Niskala Utilities ships only as its installer, with `SHA256SUMS.txt`. The portable archive and the command-line tool are no longer built, and updates download and verify only the installer. An update removes the command-line folder and its `PATH` entry left by an earlier version.
- **Any screen, any scaling.** The window opens at a size that fits the monitor's work area, from 1366 × 768 up to ultrawide and from 100% to 250% scaling. The sidebar packs its items tighter on short windows and folds to icons on narrow ones, the title bar shortens its search box and drops its badges before they overlap, and the pages share their width in proportion instead of fixed columns. The splash is sized for the monitor it opens on.

### Fixed

- The theme and language buttons at the bottom of the sidebar now show the theme and language in use (**Dark theme**, **Light theme**, **English**, **Bahasa Indonesia**, **Tema gelap**, **Tema terang**) with a switch icon, and their tooltips say what a click changes, in the open and the folded sidebar.
- Switching between the light and dark theme repaints every page, including pages opened earlier, the window buttons, the cluster map and the dimmed layer behind dialogs.

## v0.2.0 (2026-11-27)

### Changed

- **Leftovers in two steps.** The uninstall panel now has six steps: Fingerprint, Restore point, Live process tree, Leftover scan, Leftover files and Leftover registry. **Leftover files** lists only files and folders, with the Safe, Review and Low filters, **Select shown** and **Clear shown**, the details beside the list and the option to delete permanently; **Clean files & continue** removes the chosen ones and **Skip files** keeps them. **Leftover registry** then lists registry keys, values and firewall rules with their path, value, confidence and the reasons they were found; **Clean registry** removes the chosen ones and **Finish without deleting** leaves them. With no leftover files the panel goes straight to the registry, and with nothing left at all it says so and finishes.
- Both steps write to one entry in **History & backups**, so a restore brings back the files and the registry entries together.
- **Safe cancel and a locked panel.** Until the app itself is uninstalled, **Cancel**, the close button, a click beside the panel, `Esc` or another page in the rail cancels the uninstall and closes the panel. Once it is uninstalled, the close button goes away, clicks beside the panel and `Esc` are ignored, and the rail and `Ctrl+K` stay locked, with a note in the panel, until the leftover steps are cleaned or skipped.
- **Smoother start.** The splash fades in, the mark and the name slide gently into place, and it waits until the main window has drawn its first page before one short fade-out, so the window appears whole. The background, the mark and the name fade together.
- **System Repair output.** A progress line that a tool rewrites in place, such as DISM's bar or the percentages of `sfc` and `chkdsk`, now updates one line in the log instead of adding a copy for every step.
- **One status banner.** System Repair shows a single green, yellow or red banner for the last run, never an empty one, and clears it when another repair is chosen or started.

### Added

- **Local repair source.** **Repair the component store** can use `install.wim` or `install.esd` from a Windows ISO instead of Windows Update. When DISM stops with `0x800F0915` or `0x800F081F`, the banner explains in English or Indonesian that the repair files could not be found or downloaded, and offers to choose an image and run again. The command line gets `niskala repair run dism-restore-health --source <image>`.

### Fixed

- Hovering over the rail, the System Repair list or any button no longer shows a floating `Ctrl+K` tooltip; the shortcut still opens the command palette and the badge in the title bar search box stays.

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
