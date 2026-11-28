# Patch notes

What changed in each release of Niskala Utilities, newest first.

## v0.3.4 (2026-11-28)

### Added

- **USB S.M.A.R.T.** Health is now read through USB bridges with SAT and NVMe pass-through, including every attribute with its threshold.
- **Scheduled optimization.** Weekly, monthly or idle upkeep through Windows Task Scheduler: TRIM for SSDs and Quick Defrag for hard disks.
- **Richer cluster map.** MFT / metadata reserved areas and compressed files have their own colors in the map and the cluster inspector.

### Improved

- **Instant Drive Optimizer.** The page opens at once and checks each drive in the background, so a sleeping USB disk no longer holds it up.
- **New layout.** A drive table with fragmentation, free-space fragments and a rating on top, and the Strategy Guide as a drawer on the right.

## v0.3.3 (2026-11-28)

### Added

- **Strategy Guide** suggests Maximum Speed, Low Impact or Reclaim Contiguous Space for each hard disk, and TRIM only for SSDs.
- **Cluster inspector.** Click a block on the drive map to list the files in it: contiguous, fragmented, archived or locked.
- **Hardware telemetry.** The health tab shows S.M.A.R.T. status, temperature, power-on hours and SSD wear where the drive reports them.
- **Drive queue.** Check several drives to analyze or optimize them one after another; SSDs in the queue still get TRIM only.

### Improved

- Releases are now signed as **Niskala Project**; updating replaces the old certificate that the installer trusted.

## v0.3.2 (2026-11-28)

### Improved

- **Faster drive analysis.** On NTFS, Drive Optimizer reads file details straight from the file table; a large USB hard disk went from about 9 to about 2 minutes.
- **Faster Apps list.** All install sources are read at once, folder sizes are remembered until the folder changes, and icons load for the rows on screen first.
- **Quicker cleaning scans.** Quick Scan and Registry Integrity run their checks side by side and stop almost at once when you cancel.

### Fixed

- **Consolidate Free Space** closes the gaps first and no longer pushes large files to the end of the drive.
- Switching pages, themes or drives many times no longer builds up memory or shows progress from an earlier run.

## v0.3.1 (2026-11-28)

### Improved

- **Much faster analysis on NTFS.** Files are read in the order they sit on the disk; a 1.7 million-file USB disk went from up to 89 to 13 minutes.
- **Results are kept while the app runs.** Switching drives keeps the last analysis, and **Re-analyze** scans again when you want fresh numbers.

### Fixed

- **Cancel never splits a file.** The file being moved is finished first, and the page says **Stopping after current file…** until it is done.
- **Consolidate Free Space** and **Full Zone Optimization** now put files where they belong instead of towards the end of the drive.
- Joined and moved files are counted correctly.

## v0.3.0 (2026-11-27)

### Added

- **Drive Optimizer.** See how fragmented each drive is, then defragment a hard disk or run TRIM on an SSD; the drive type is detected automatically.
- **Three modes and a live map.** Quick Defrag, Consolidate Free Space and Full Zone Optimization, with a cluster map that changes as files move.

### Improved

- **Simpler menus and one installer.** Maintenance holds Drive Optimizer, System Repair and History & backups, and updates verify a single installer.
- **Fits any screen.** The window, sidebar and pages adapt from 1366 × 768 to ultrawide and from 100% to 250% scaling.

### Fixed

- The theme and language buttons show what is in use, and switching the theme repaints every page.

## v0.2.0 (2026-11-27)

### Added

- **Local repair source.** Repair the component store from a Windows ISO when Windows Update cannot provide the files.

### Improved

- **Leftovers in two steps.** Review leftover files first, then registry entries; both land in one History entry so a restore brings back everything.
- **Safe cancel.** Cancel before removal stops cleanly; after removal the panel stays open until the leftovers are cleaned or skipped.
- **Cleaner System Repair output.** Progress bars update one line in the log, and a single banner shows the result of the last run.

### Fixed

- Hovering over the sidebar or buttons no longer shows a stray `Ctrl+K` tooltip.

## v0.1.3 (2026-11-27)

### Added

- **Update prompt.** When a newer release is out, a dialog shows what is new and offers **Update now**, **Review first** or **Later**.
- **Sidebar motion.** Categories slide open and shut, and the highlight follows the page you open.

### Improved

- With Windows animation effects turned off, every change in the sidebar is instant.

## v0.1.2 (2026-11-27)

### Improved

- **Sidebar categories.** Apps, Cleaning, Performance and Maintenance open and close with a click, and the page on screen is always marked.
- The sidebar fits a default-size window without scrolling, and switching the language keeps your place.
- The app list is dimmed behind an open uninstall panel so the panel stands out.

### Fixed

- The theme button shows the right theme after switching it from the command palette.

## v0.1.1 (2026-11-27)

### Added

- **Updates.** Check for, download and install a new release; every download is verified against its SHA-256 checksum first.
- **Installer.** Installs to Program Files with Start menu and desktop shortcuts and a clean uninstall.
- **Program icons in Resource Control** for startup entries, services, scheduled tasks and context-menu handlers.

### Improved

- The desktop download is about half its former size, from 55.5 MB to 29.1 MB.

### Fixed

- History & backups no longer closes the app when it opens older backups.

## v0.1.0 (2026-11-26)

### Added

- **Uninstaller** with a leftover scan, Force Remove, Batch Remove, the Target Picker and the Install Tracker.
- **Guardian.** Every removal is backed up, with an optional restore point, and can be undone from History & backups.
- **Cleaner** and **Registry Integrity** for temporary files, browser traces and entries that point at missing programs.
- **Resource Control** and **Process Suspend** to tame startup items, services and idle background processes.
- **System Repair** with Windows' own repair tools, in an English and Indonesian desktop app.
