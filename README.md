# Niskala Utilities

[![release](https://img.shields.io/github/v/release/Adriansyah101/NiskalaUtilities?label=release&color=blue)](https://github.com/Adriansyah101/NiskalaUtilities/releases/latest)
[![license](https://img.shields.io/github/license/Adriansyah101/NiskalaUtilities?label=license&color=green)](LICENSE)
[![platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-blue)](#build-and-test)
[![CPU](https://img.shields.io/badge/CPU-x64%20(Intel%20%2F%20AMD)-blue)](#release-build)
[![downloads](https://img.shields.io/github/downloads/Adriansyah101/NiskalaUtilities/total?label=downloads&color=brightgreen)](https://github.com/Adriansyah101/NiskalaUtilities/releases)

Windows uninstaller, system and privacy cleaner, resource control, maintenance toolkit and
non-destructive system repair.
Clean-room implementation: no code or binaries of other uninstallers or system utilities are
analysed or reused.

One engine, two front ends: `niskala.exe`, a command line, and `Niskala.exe`, a WinUI 3 desktop
app. Both are Native AOT builds (`win-x64`, `win-arm64`). The product name is Niskala Utilities;
namespaces, file names, the `.nsklog` extension and `%LocalAppData%\Niskala` keep the short name.
This is an early release: version 0.1.0. Released under the GNU General Public License v3.0; see
[License](#license).

## Modules

| Module | What it covers |
|---|---|
| Uninstaller: Uninstall, Force Remove, Batch Remove, Target Picker, Install Tracker | Removing applications and what they leave behind. |
| Quick Scan, Selective Clean | System junk and browser traces from declarative JSON rules, SQLite compaction. |
| Registry Integrity | File types, shared files, COM classes, firewall rules and services that point at missing programs. |
| Resource Control | Startup entries, third-party services, scheduled tasks and context-menu handlers, with a boot-impact estimate. |
| Process Suspend | Freezing, efficiency mode and CPU caps for idle background processes. |
| Storage Map, Duplicate Scan, Secure Wipe | The maintenance toolkit. |
| System Repair | DISM, SFC, network resets, DNS flush and an online disk scan from a closed list. |

All of them ship in this release.

## Layout

| Project | Role |
|---|---|
| `src/Niskala.Interop` | CsWin32 bindings (`allowMarshaling: false`) behind small safe wrappers, including scoped token privileges. The only project with `unsafe`. |
| `src/Niskala.Core` | Models, command-line parsing, logging, and the safety layer: `PathGuard`, `RegistryGuard`. |
| `src/Niskala.Scanner` | Inventory (Uninstall keys, Windows Installer, packages), icons, folder sizes, pre-uninstall fingerprints, leftover scanner and leftover trees, Steam library reader. |
| `src/Niskala.Uninstall` | Command resolution, preflight checks, the uninstall orchestrator, job tracking, completion detection, package removal, Force Remove, Steam direct uninstall, Batch Remove and the Target Picker. |
| `src/Niskala.Guardian` | Restore points, compressed registry backups with a Registry Editor copy, the transaction journal, handle-based removal (Recycle Bin, permanent, at restart), firewall rules, scheduled tasks, journaled setting changes, restore. |
| `src/Niskala.Tracker` | Install Tracker: kernel trace of an installer's process tree, noise filter, `.nsklog` install logs, exact rollback through the Guardian. |
| `src/Niskala.Repair` | System Repair: the closed `RepairCommandId` list, its argument allowlist, and the process runner that decodes and parses the tools' console output. |
| `src/Niskala.Cleaner` | Quick Scan, Selective Clean and Registry Integrity: the rule catalog, browser profiles and their locks, SQLite compaction, the cleaner and the registry checks. |
| `src/Niskala.Optimizer` | Resource Control (startup, services, tasks, context menus, boot impact) and Process Suspend. |
| `src/Niskala.Toolkit` | Storage Map, Duplicate Scan and Secure Wipe. |
| `rules/*.json` | The cleaning rules; a release carries them in `lib\rules\`. |
| `native/Niskala.EtwHelper` | C++20 DLL (MSVC v143) that runs the real-time ETW session and follows the process tree; a small C ABI for `Niskala.Interop`. |
| `native/Niskala.Launcher` | C++20 launcher (MSVC v143) shipped as the release's root `Niskala.exe`; it starts the desktop app in `lib\`. |
| `src/Niskala.CLI` | `niskala.exe` (System.CommandLine, Native AOT, `asInvoker`). |
| `src/Niskala.Host` | Composition root shared by the CLI and the UI. |
| `src/Niskala.UI` | WinUI 3, unpackaged, self-contained, Native AOT, `requireAdministrator`. |
| `tests/*` | xunit v3 on Microsoft.Testing.Platform. |
| `scripts/Build-Release.ps1` | Release build: tests, Native AOT publish, launcher, `lib\` layout, privacy scan, zips and checksums. |
| `scripts/New-AppIcon.ps1` | Renders `assets/niskala.ico` (16 to 256 px) from the monogram geometry in `NiskalaMark.xaml`. |
| `LICENSE` | GNU General Public License v3.0; a release carries it in `lib\LICENSE`. |

Dependencies point one way: `UI`/`CLI` -> `Host` -> `Uninstall`, `Tracker`, `Cleaner`, `Optimizer` and `Toolkit` -> `Guardian` ->
`Scanner` -> `Core` -> `Interop`; `Host` -> `Repair` -> `Core`. The Quick Scan reads startup items
through `IStartupReview`, which the host implements with Resource Control, so the cleaner does not
depend on the optimizer.

Both front ends take their services from `NiskalaHost`, so they share one inventory, one set of
guard rules and one Guardian journal: a transaction made in the desktop app can be restored from
the command line and the other way round. `niskala.exe` runs `asInvoker`, so machine-wide work and
`track` need an elevated prompt; `Niskala.exe` is `requireAdministrator`.
`Niskala.EtwHelper.dll` is loaded only when an install is recorded; a build puts it next to each
executable, a release in `lib\`. All state lives under `%LocalAppData%\Niskala`: `cache`, `icons`, `fingerprints`,
`transactions`, `logs`, `ui-settings.json` and `restraints.json` (the processes Process Suspend is
holding).

## Build and test

Requires the .NET 10 SDK and Visual Studio 2022 Build Tools with the "Desktop development with C++"
workload (MSVC v143, Windows SDK). `Niskala.Tracker` builds `native/Niskala.EtwHelper` with the
Visual Studio MSBuild it finds through `vswhere.exe` and copies the DLL next to its output, so a
plain `dotnet build` covers the C++ project too; the solution lists it but does not build it
itself. Native AOT publishing also needs `vswhere.exe` on `PATH`
(`%ProgramFiles(x86)%\Microsoft Visual Studio\Installer`) when publishing outside a developer prompt.

```
dotnet build Niskala.sln
dotnet test --solution Niskala.sln
```

Path and folder-size tests create directory junctions under `%TEMP%\niskala-tests` with
`mklink /J` (no elevation needed) and remove the links before anything else. Guardian tests also
use scratch keys below `HKCU\Software\NiskalaTests` (including a registry symbolic link), and one
test moves a scratch file and folder to the Recycle Bin and restores them. From a normal prompt no
test creates a restore point, removes a firewall rule or touches a service. The cleaner, optimizer
and toolkit tests work the same way: their roots, profiles, rule files, SQLite databases, registry keys and
services key all live in the scratch folder and `HKCU\Software\NiskalaTests-<pid>`; recycled files
are restored again; the process tests suspend, throttle and cap a copy of `PING.EXE` they start
themselves; the free-space wipe writes at most 8 MB into the scratch folder. Nothing touches a real
browser profile, startup entry, service or task.

Four tests need elevation and are skipped from a normal prompt: the real kernel trace (`cmd.exe` and
`reg.exe` writing to scratch locations, then a rollback), a real restore point (removed again by its
sequence number), `ipconfig /flushdns` and `DISM /Online /Cleanup-Image /CheckHealth`. From a normal
prompt the Tracker tests still check that the helper loads and refuses the session. Elevated, the
erase test may schedule a locked scratch file for deletion at restart; it takes its own entries out
of `PendingFileRenameOperations` again.

## Release build

```
powershell -ExecutionPolicy Bypass -File scripts\Build-Release.ps1 [-Runtime win-x64|win-arm64]
```

The version (`Version`, `AssemblyVersion`, `FileVersion`, `InformationalVersion`) comes from
`Directory.Build.props`; the launcher's version resource takes it from there as well. The script
first empties `artifacts/release`, so zips of an older version never linger, and stops at the
first failure:

1. **Tests.** Every test project runs in Release (`dotnet test --solution Niskala.sln -c Release`).
   Run from a normal prompt, it warns that the four elevated tests are skipped; run it elevated
   before a release.
2. **Launcher.** `native/Niskala.Launcher` is rebuilt with the Visual Studio MSBuild.
3. **Publish.** Both front ends are published with Native AOT. The linker gets
   `/PDBALTPATH:%_PDB%`, so an executable names its PDB without a folder. The script checks that
   the executables and helpers are there, that no managed `Niskala.*.dll` is left, which would
   mean the AOT compile did not run, and that every executable carries the product name and version
   in its version resource. Windows App SDK copies its `.mui` files as native assets, which
   `SatelliteResourceLanguages` does not filter, so the desktop project drops every language folder
   but `en-US` and `id-ID` itself.
4. **Layout.** Only executables stay at the root of a payload; everything else goes to `lib\`:
   - `NiskalaUtilities-CLI-v<version>-<rid>`: `niskala.exe`, and `lib\Niskala.EtwHelper.dll`,
     which only `track` loads (from `lib\` first, then next to the program);
   - `NiskalaUtilities-v<version>-<rid>`: `Niskala.exe`, the launcher, and `lib\` with the
     desktop app (`lib\Niskala.exe`), the self-contained Windows App SDK runtime, its resources and
     `Niskala.EtwHelper.dll`.

   Both payloads get the repository's `rules/*.json` in `lib\rules\` and its `LICENSE` in
   `lib\LICENSE`; the published `niskala.exe` then loads the rules (`clean rules --json`) and the
   build stops if it cannot.

   The launcher starts `lib\Niskala.exe` with the same arguments through `ShellExecuteEx`, so the
   app's `requireAdministrator` manifest raises the usual UAC prompt, and exits. The app keeps its
   flat runtime layout inside `lib\`, where Windows App SDK finds its DLLs, `.pri` files and
   activation entries as in any self-contained app. The two executables cannot share one folder:
   Windows file names ignore case, so `Niskala.exe` and `niskala.exe` are one name.
5. **Symbols.** The PDBs move to `artifacts/symbols`. The Native AOT linker records object-file
   paths in them, so they stay on the build machine for crash analysis and never ship.
6. **Privacy scan.** Every shipped file is searched as ASCII and as UTF-16 for the user name, the
   machine name, the profile path and the repository path. Any hit fails the build.
7. **Archives.** Each payload is zipped with its folder at the root:
   `NiskalaUtilities-v0.1.0-win-x64.zip` and `NiskalaUtilities-CLI-v0.1.0-win-x64.zip`.
   `SHA256SUMS.txt` lists the zips in `sha256sum -c` format.

The program folder the user unpacked counts as Niskala's own: PathGuard protects `lib\` and the
folder above it.

ARM64 builds need the MSVC ARM64 build tools as well.

## CLI

| Command | What it does |
|---|---|
| `list` | Installed applications: Uninstall keys, Windows Installer and Store apps (MSIX and AppX packages). |
| `size` | Folder sizes, without following junctions or symbolic links. |
| `icons` | Extract the 256 px icon of every visible application into the icon cache. |
| `inspect` | Resolve the uninstall command, run the preflight checks and take a fingerprint; nothing is removed. |
| `uninstall` | Run the application's own uninstaller and verify that it is gone; with an install log, offer its rollback. A Steam game is removed directly. Several applications run as one Batch Remove queue with one leftover review at the end. |
| `leftovers` | Find and score what an application left behind, optionally as a tree of what each folder and key holds; nothing is removed. |
| `clean` | Quick Scan, the rule list, Selective Clean and Registry Integrity; `clean app` removes an uninstalled app's leftovers (Safe items by default). All of it as Guardian transactions. |
| `restore` | List Guardian transactions or undo one. |
| `force` | Remove an application without its uninstaller: by name, folder, program or shortcut, or the window, desktop icon or tray icon at a screen point. |
| `track` | Record what an installer changes (elevated). |
| `logs` | List, show, roll back or delete recorded installs. |
| `repair` | List the System Repair commands or run one, with its output and progress live (elevated). |
| `optimize` | Resource Control: list and switch startup entries, services, tasks and context-menu handlers. Process Suspend: list processes, suspend, efficiency mode, CPU cap, resume. |
| `toolkit` | Storage Map, Duplicate Scan, Secure Wipe of files and of free space. |

```
niskala list [--json] [--all] [--source all|win32|store] [--no-msi-index]
niskala size [folders...] [--installed] [--json]
niskala icons [--json] [--prune]
niskala inspect <app> [--silent] [--all] [--files] [--json]
niskala uninstall <app>... [--dry-run] [--silent] [--yes] [--all] [--no-restore-point] [--no-log] [--close-steam | --via-steam] [--permanent] [--json]
niskala leftovers <app> [--all] [--low] [--tree <depth>] [--json]
niskala clean quick [--dry-run] [--yes] [--permanent] [--no-restore-point] [--json]
niskala clean rules [--json]
niskala clean run <rule|prefix*|category>... [--dry-run] [--yes] [--permanent] [--no-restore-point] [--json]
niskala clean registry [--check <kind>...] [--fix] [--dry-run] [--yes] [--no-restore-point] [--json]
niskala clean app <app> [--dry-run] [--yes] [--permanent] [--review] [--no-restore-point] [--json]
niskala restore [<transaction>] [--yes] [--json]
niskala force <app|folder|file.exe|.lnk|.url> | --at X,Y [--dry-run] [--yes] [--permanent] [--review] [--no-restore-point] [--all] [--json]
niskala track <installer.exe|.msi> [--args "..."] [--name "..."] [--json]
niskala logs [--json]
niskala logs show <id> [--json]
niskala logs rollback <id> [--dry-run] [--yes] [--permanent] [--no-restore-point] [--json]
niskala logs delete <id> [--yes]
niskala repair list [--json]
niskala repair run <command> [--yes] [--json]
niskala optimize list [--kind <kind>...] [--json]
niskala optimize disable|enable <id|name> [--dry-run] [--yes] [--json]
niskala optimize processes [--idle] [--sample <seconds>] [--json]
niskala optimize suspend|eco <pid> [--dry-run] [--json]
niskala optimize resume <pid> | --all [--json]
niskala optimize limit <pid> [--cpu <percent>] [--seconds <n>] [--dry-run]
niskala toolkit map <folder> [--depth <n>] [--top <n>] [--json]
niskala toolkit duplicates <folder>... [--min-size <bytes>] [--hidden] [--keep Oldest|Newest] [--remove] [--dry-run] [--permanent] [--yes] [--json]
niskala toolkit wipe <file>... [--method ZeroFill|Dod522022M|Gutmann] [--dry-run] [--yes] [--json]
niskala toolkit wipe-free <drive> [--method ZeroFill|Dod522022M|Gutmann] [--dry-run] [--yes] [--json]
```

Every command that changes something has `--dry-run`, asks before it acts (`--yes` skips the
question; with redirected input it must be given), and writes the plan and its transaction with
`--json`. A change that needs elevation exits with `740` before it starts; Ctrl+C exits with `130`.

`list` merges three sources. The Uninstall keys come from the HKLM 64-bit and 32-bit views and
from HKCU. Windows Installer products are read with `MsiEnumProductsEx`. Store apps (MSIX and AppX
packages) are read with `Windows.Management.Deployment.PackageManager` for the current user, main
packages only, so frameworks and resource packages never show; `--source store` lists them alone.
The deduplication rules:

- **Uninstall entries.** Two entries are duplicates only when they share a ProductCode, or when
  name, version, publisher and uninstall command are all identical.
- **Windows Installer products.** A product fills the missing fields of the entry with the same
  ProductCode. A product with no Uninstall entry is listed but hidden (`NoUninstallEntry`).
- **Packages.** System-signed packages are marked protected and hidden. Sparse packages (identity
  for a Win32 app, `AllowExternalContent`) are hidden. Within one package family, only the highest
  version stays visible.

An MSI entry without `InstallLocation` gets a folder inferred from its component key paths. The
component index takes seconds to build, so it is built only for products that miss the cache
(`%LocalAppData%\Niskala\cache\msi-locations.json`). Each cache entry is keyed by ProductCode and
stamped with the Uninstall key's last write time. Other entries without one take the uninstaller's
folder, except shared installer caches and Steam games: a Steam game's uninstaller is the Steam
client, whose folder holds every game.

`size` measures folders with `FileSystemEnumerable` without following reparse points. It uses one
queue per volume, with 4 workers on SSD, 1 on HDD and 2 when the disk kind is unknown. The disk
kind comes from `IOCTL_STORAGE_QUERY_PROPERTY` (seek penalty).

`icons` writes 256 px PNGs to `%LocalAppData%\Niskala\icons`. It extracts them on a dedicated STA
thread: `SHDefExtractIconW` first, then `IShellItemImageFactory`. The source is tried in this
order: `DisplayIcon`, MSI `ProductIcon`, the uninstaller, or the package's app-list logo.
`--prune` deletes cached icons that no installed application maps to. A package also records the
manifest's `BackgroundColor` (`package.logoBackground`) so a white unplated glyph can be drawn on
its plate.

`inspect` changes nothing. It accepts an id, an exact name or part of a name, and shows:

- **the uninstall command** in both modes. A Windows Installer product always runs
  `%SystemRoot%\System32\msiexec.exe /x {ProductCode}`, and silent mode adds `/qn /norestart`.
  Other installers keep the stored command, with these silent switches added: NSIS `/S`,
  Inno `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART`, Burn `/quiet /norestart`, Squirrel `-s`,
  winget `--silent --disable-interactivity`. InstallShield and unknown installers have no known
  switch and are reported as such. A `QuietUninstallString` wins in silent mode.
- **warnings**: a machine-wide entry whose binary or folder standard users can modify (owner or
  allow ACE outside SYSTEM, Administrators, TrustedInstaller and service accounts), an ambiguous
  unquoted path, a shell or script host.
- **preflight**: elevation for machine-wide entries, `Global\_MSIExecute`, and running processes
  whose image lies in the install folder.
- **the fingerprint**: the files of the install folder (reparse points are listed, not walked),
  `.exe` names, ProductCode and UpgradeCode, Inno AppId, services (`ImagePath` and svchost
  `ServiceDll`), `Run`/`RunOnce` values, `App Paths`, scheduled tasks (Exec actions through
  `ITaskService`), and `.lnk` files in the Start menu, desktop and taskbar pins (`IShellLinkW`
  without `Resolve`; advertised shortcuts through `MsiGetShortcutTarget`). The install folder
  counts only when PathGuard allows it.

The exit code is 0 when the preflight can proceed, 1 when it is blocked and 2 when the name
matches no application or several.

## Desktop app

`Niskala.exe` (WinUI 3, unpackaged, Native AOT) runs elevated. It opens with a splash card while
the main window is built: 560 x 320, borderless, out of the taskbar, rounded at 12 px with clear
corners, on the brand gradient (`#183D66` to `#08111E`). The double-line N draws itself (0 to
0.65 s), its cut-off corner fades in and slides 4 px up and to the right (0.4 to 0.8 s), `NISKALA`
and `UTILITIES` fade in and rise (0.55 to 1 s), and the card fades out once the main window is
ready (from 1.15 s). Everything is XAML: the monogram is one vector control (`NiskalaMark`) shared
by the splash and the title bar, animated by storyboards; no image file is loaded. The executables
carry `assets/niskala.ico` (16 to 256 px), and the main window takes its icon from that resource.
A left rail holds thirteen sections in four groups (Apps, Cleaning, Performance, Maintenance);
`Ctrl+K` (or the search box in the title bar) opens a command palette that finds apps and runs
force removal, Target Picker, restore and the rest. The default theme is a dark slate
(`#09090b` / `#121317`); a light theme and English or Indonesian are one click away in the rail
and are kept in `%LocalAppData%\Niskala\ui-settings.json`. Every binding is `{x:Bind}`.

- **Apps.** A virtualized list with instant filters (All, Win32, Store apps, Large, Recently
  installed, Broken), 256 px icons (packages on their `BackgroundColor` plate) and folder sizes
  measured in the background; a row that scrolls into view jumps the size queue (`SizeQueue`).
  Selecting an app opens an inspector drawer on the right with its details. Each row has a check
  box; from two checked apps up, **Batch Remove (N)** starts one queue for all of them.
- **Uninstall, Force Remove, Steam and Batch Remove flows.** Starting one widens the drawer into a layout locked to the
  window height, without a page scroll bar, that fits from 720 px up: a one-line stepper
  (fingerprint, restore point, live process tree, leftover scan, review; force and Steam have their
  own steps), a progress bar with the active phase and counters (processes running, leftovers
  found), a work area, and an action bar pinned to the bottom (Cancel and Start, Stop waiting, Keep
  everything and Clean selected, Scan leftovers, Finish). While the flow runs, the work area is an
  activity log that follows the newest line: steps, every process of the uninstaller's job, checks,
  and each item the Guardian removes. At the review it becomes two trees, files and folders or
  registry and firewall, filtered by Safe, Review and Low, beside a detail pane: full path, kind,
  value type and data or size and date, the confidence and the reasons behind it. A row expands into
  its subfolders and files or subkeys and values (`REG_*` type and data); checking a row checks
  everything in it, checking part of it leaves the row partly checked, and only the checked parts
  go. Only the trees and the log scroll. When an install log exists for the app, the review lists
  the log's exact rollback instead of a heuristic scan.
- **Install Tracker.** Choose an installer, record it, finish early or abandon it; the logs list
  shows each `.nsklog` with its changes and rolls it back through the same review.
- **Target Picker.** The main window steps aside; a topmost layered overlay dims the screen except the
  window, desktop icon or tray icon under the pointer, a floating card names the app, and a click
  picks it. Low-level mouse and keyboard hooks (`WH_MOUSE_LL`, `WH_KEYBOARD_LL`) carry the pointer,
  the click and Esc or the right button while the mode is on, so another topmost window cannot take
  them, and the click never reaches the window below; the hooks go with the overlay. Two places let
  real clicks through, where the overlay is fully clear: the card's **Browse File / Shortcut…**
  button and the taskbar button that opens the hidden tray icons. The same button on the page opens
  the common item dialog for `.exe`, `.lnk` and `.url` files (`FOS_NODEREFERENCELINKS`, so a
  shortcut comes back as itself). A pick, pointed at or chosen, gets four quick actions: Uninstall
  (or Force Remove when the app has no uninstaller or no app matches), Stop process (after a
  confirmation, only while a process of it runs), Disable from startup (its enabled startup entries)
  and Open file location.
- **Quick Scan.** One Scan button; three totals (system junk, browser caches, heavy startup items),
  the rule results with check boxes, the startup items with a medium or high boot impact, each with
  Switch off, and Clean, which asks first and makes a restore point unless that box is cleared.
- **Selective Clean.** Every rule with its description and how it removes (deleted, Recycle Bin,
  needs administrator), check boxes, the Quick Scan set or none in one click, Scan for real byte
  counts, and Clean with the permanent and restore-point options.
- **Registry Integrity.** Scan, then every issue with its kind, key and what is missing; protected
  ones are shown but cannot be checked. Repair selected asks first.
- **Resource Control.** Four tabs (Startup, Services, Scheduled tasks, Context menu). Each row shows
  the command, publisher, where it starts, the boot-impact estimate and a switch; a protected row
  says why and cannot be switched. Run and Startup-folder entries can also be removed as a
  journaled transaction.
- **Process Suspend.** Processes of this session with their CPU use over 1.5 s, by default only the
  idle background ones that may be touched; Suspend, Efficiency mode, Limit CPU to 20% and Resume on
  each row, Resume all at the top. Refused processes say why.
- **Storage Map.** A folder or drive, Map, then its largest sub-folders with share bars (a click goes
  down a level, Up comes back) and the largest file types beside them.
- **Duplicate Scan.** Folders as chips, keep the oldest or newest copy, Scan, then every group with
  each copy marked Keep, Remove or Protected; Remove copies asks first and uses the Recycle Bin.
- **Secure Wipe.** The method (zero fill, DoD 5220.22-M, Gutmann), files added one by one, Wipe
  files, and Wipe free space for a fixed drive; both ask first and show the pass and bytes written.

  Every page is locked to the window height; only its lists scroll.
- **System Repair.** The repair list on the left; on the right the selected command line, Run and
  Cancel, a progress bar fed by the parsed percentage (indeterminate until the tool prints one), the
  elapsed time, the line the tool is redrawing, and a log that follows the newest line. The page is
  locked to the window height; only the list and the log scroll. A repair that changes Windows asks
  first, and only one runs at a time.
- **History & backups.** Every Guardian transaction with its items, restore point and method, and
  the restore.

Folders, files and web links open through Explorer started as a child of the desktop shell
(`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS`), so they run with the shell's unelevated token; only
`http` and `https` links are opened. File and folder pickers use the common item dialog, which
works elevated where the WinRT pickers do not.

## System Repair

`Niskala.Repair` runs Windows' own repair tools, and nothing else. `RepairCommandId` is a closed
enum; `RepairCommandCatalog.Resolve` maps each value to a program in the directory
`GetSystemDirectoryW` returns and to fixed arguments:

| Name | Command | Notes |
|---|---|---|
| `dism-check-health` | `Dism.exe /Online /Cleanup-Image /CheckHealth` | Reads only. |
| `dism-restore-health` | `Dism.exe /Online /Cleanup-Image /RestoreHealth` | Repairs the component store from Windows Update. |
| `sfc-scan` | `sfc.exe /scannow` | Replaces damaged protected files from the component store. |
| `dism-component-cleanup` | `Dism.exe /Online /Cleanup-Image /StartComponentCleanup` | Never `/ResetBase`, so installed updates stay uninstallable. |
| `winsock-reset` | `netsh.exe winsock reset` | Needs a restart. |
| `ip-reset` | `netsh.exe int ip reset` | Needs a restart. |
| `flush-dns` | `ipconfig.exe /flushdns` | |
| `chkdsk-scan` | `chkdsk.exe <system drive> /scan` | The drive comes from `Path.GetPathRoot(Environment.SystemDirectory)`; never `/f`, `/r`, `/x`, `/spotfix` or `/offlinescanandfix`. |

Every one needs elevation: the runner checks the token first and returns `NotElevated` without
starting anything. The public API takes only a `RepairCommandId`. `RepairInvocation`, the resolved
program and arguments, has an internal constructor that accepts only the five programs and the
argument tokens above (plus a drive letter) and refuses the destructive switches by name, so no
caller can build another command line. A program that is missing or is a reparse point is not
started.

The runner starts the program with `UseShellExecute = false`, `CreateNoWindow`, the argument list
quoted by .NET, standard input closed, and standard output and error redirected. Both pipes are read
as raw bytes in 4 KB chunks, so a progress line that the tool redraws with `\r` and never ends
with `\n` shows as it is written. The encoding is chosen from the first bytes of each pipe: a
UTF-16 mark or any NUL byte means UTF-16LE (`sfc.exe` writes UTF-16 to a pipe), a UTF-8 mark or
valid multibyte UTF-8 means UTF-8, anything else the system OEM code page (`GetOEMCP`; 65001 is
UTF-8), which DISM, netsh, ipconfig and chkdsk use. A stateful decoder keeps characters split
across chunks intact. A `\r` makes the next character start the line over, so `sfc` and DISM
progress arrives as updates of one line and only its last state stays in the log; `\r\r\n`
keeps the text. The percentage is the last `0` to `100` number followed by `%` in a line, with a
point or a comma as decimal separator and an optional space before `%`, so `Total: 5%` wins in
chkdsk's line.

Cancelling kills the process and its children (`Process.Kill(entireProcessTree: true)`), waits for
the exit, and returns `Cancelled`; the process object and its pipes are disposed in every case. Exit
codes map to an outcome and a diagnostic: `0` succeeded (or restart required for the network
resets), `3010` restart required, `740` elevation required, `5` access denied, `87` an option this
Windows does not know, DISM's `0x800F081F` (no repair source), `0x800F0906` (download failed),
`0x800F0907` (Group Policy), `0x800F082F` (pending servicing operations), and chkdsk's `1` and `2`
as success and `3` as problems that need an offline repair. Anything else is `Failed` with its code.

`niskala repair run` prints the tool's lines as they come; on a console the redrawn line is updated
in place. It asks before a repair that changes Windows (`--yes` skips the question) and exits with
`0`, `3010` when a restart is needed, `740` when not elevated, `130` when cancelled with Ctrl+C,
and `1` otherwise. `--json` writes the result to stdout and the tool's output to stderr.

## Cleaner

`Niskala.Cleaner` cleans from declarative rules in `rules/*.json` (a release reads `lib\rules\`).
The files are parsed with a source-generated `JsonSerializerContext`: unknown properties, comments,
trailing commas, unknown enum values and a `schemaVersion` other than `1` reject the whole file,
and every rule is validated before it is used:

- an id is lower-case words joined by `.` or `-`; `browser.*` is reserved for the browser rules;
- a target names a closed root (`userTemp`, `windowsTemp`, `windows`, `localAppData`,
  `roamingAppData`, `programData`) and a plain relative sub-folder: no drive, no `..`, no
  wildcards, no trailing dot or space; `windows` and `programData` need a sub-folder;
- file patterns are names with `*` and `?` only (`files`, `exclude`), `recurse` walks sub-folders,
  `keepFolders` keeps the emptied ones, `minimumAgeHours` keeps anything written or created more
  recently, `removal` is `permanent` or `recycleBin`, and `requiresElevation` skips the rule
  without elevation.

```json
{ "id": "windows.user-temp", "category": "temporaryFiles", "quick": true, "removal": "permanent",
  "minimumAgeHours": 24, "name": { "en": "...", "id": "..." }, "targets": [ { "root": "userTemp", "recurse": true } ] }
```

`windows.json` covers the user's and Windows' temp folders, thumbnail and icon caches, the shell's
`INetCache`, program and kernel crash dumps, Windows Error Reporting queues and the Delivery
Optimization cache. `browsers.json` lists Edge, Chrome, Brave, Vivaldi, Opera, Opera GX
(Chromium) and Firefox and LibreWolf (Gecko) by where their user data lives, and gives each engine
its items: cache, history, cookies and open sessions for Chromium; cache, cookies, sessions and form
history for Gecko. Each browser gets one rule per item (`browser.<browser>.<item>`) plus
`browser.<browser>.compact`. Gecko history is never deleted: `places.sqlite` also holds the
bookmarks. A profile is a folder with the engine's marker file (`Preferences`, `prefs.js`) in the
user-data folder or one level below it; Opera and the Gecko browsers keep their caches in a local
mirror of the roaming profile.

A browser profile is skipped while it is in use: Chromium's `lockfile` in the user-data folder or
Gecko's `parent.lock` in the profile cannot be opened exclusively, or a process of the browser
runs. The check is repeated right before cleaning, so a browser opened after the scan stops its
rules. Compaction opens each database with the `winsqlite3.dll` that ships with Windows
(P/Invoke, no package), with no busy wait; runs `PRAGMA integrity_check` and leaves the file alone
unless the answer is `ok`; then runs `VACUUM`. A locked or damaged database is reported, not
touched.

The scanner lists the real files with their sizes. A target folder that is itself a junction or
symbolic link is skipped, reparse points inside it are never followed, and the folder is resolved
to its physical path first, so every listed path is exact. The cleaner then removes through a
Guardian cleanup session:

1. one transaction (`SystemClean`) and, unless declined, a restore point;
2. for every file: still old enough, still not a link, then the Guardian's handle check (PathGuard,
   same physical path as scanned, not TrustedInstaller-owned, not a Microsoft-signed program);
   `permanent` marks it for deletion through the handle, `recycleBin` moves it to the Recycle Bin.
   Files in use are left alone, never scheduled for a restart;
3. sub-folders the rule emptied are removed bottom up, never the target folder itself;
4. the journal records one entry per rule with counts and bytes, every recycled file with its
   `$R` path (so `restore` brings it back) and every compacted database with the bytes saved.

The cleaner's PathGuard adds `Self` rules for the cache folders the shipped rules name inside
protected trees: `Windows\Temp`, `Windows\Minidump`, the Delivery Optimization cache, the WER
queues in ProgramData and the profile, `Microsoft\Windows\Explorer` and `INetCache`. A rule
pointing anywhere else inside a protected tree finds every file refused.

**Quick Scan** runs every rule marked `quick` (system junk and browser caches, no personal traces)
and lists the enabled startup items whose estimated boot impact is medium or high. **Selective
Clean** runs the rules you choose, by id, id prefix (`browser.firefox.*`) or category
(`crashDumps`).

## Registry Integrity

Five read-only checks, each reporting the key or value and the program that is gone:

| Check | Where | Repair |
|---|---|---|
| File types | `.ext` defaults naming a class registered nowhere; `shell\<verb>\command` of every class and of `Applications\*` | delete the default value or the verb key |
| Shared files | `SharedDLLs` values (64-bit and 32-bit views) whose path is gone | delete the value |
| COM classes | `CLSID\{guid}` whose `InprocServer32` and `LocalServer32` all name missing files | delete the class key |
| Firewall rules | rules whose program is gone (every rule sharing the name) | remove the rules |
| Services | Win32 services whose image is gone | delete the service through the SCM |

They look in `HKCU\Software\Classes`, `HKLM\SOFTWARE\Classes` and its 32-bit view. A path counts as
missing only when it is fully qualified, on a fixed local drive that is present, outside the
Windows folder, and absent in both expansions a 32-bit entry can mean (`%ProgramFiles%` or
`%ProgramFiles(x86)%`); removable, network and relative paths are never reported. Each issue goes
through RegistryGuard first: protected ones are listed with the rule that protects them and cannot
be repaired. A repair checks every issue again, then runs as a Guardian transaction
(`RegistryIntegrity`): restore point, the keys and values into `registry.json.gz` and
`registry.reg`, the firewall rules into the journal, and only then the removal. `restore` puts it
all back.

## Resource Control

`Niskala.Optimizer` lists:

- **startup entries**: the `Run` keys of the user, the machine and the machine's 32-bit view, both
  Startup folders, and the `RunOnce` keys;
- **services**: Win32 services (no drivers, no per-user service templates) whose image lies outside
  the Windows folder and is not Microsoft's;
- **scheduled tasks**: tasks outside `\Microsoft\` whose program is not Microsoft's;
- **context-menu handlers**: `shellex\ContextMenuHandlers` of `*`, `AllFilesystemObjects`,
  `Directory`, `Directory\Background`, `Folder`, `Drive` and `DesktopBackground` in the user's and
  the machine's classes, one row per COM class, when its DLL is not Microsoft's.

A program is Microsoft's when it lies in the Windows folder, inside a protected Windows component
tree (such as `Program Files\Windows Media Player`), or carries a signature that chains to a
Microsoft product root (2010) or application root (2011). Every switch is a Guardian transaction
(`ResourceControl`) that records the state before and after:

| Kind | Off | On |
|---|---|---|
| Run, Startup folder | `StartupApproved` value `03` + time, the way Windows itself switches them | `02` |
| RunOnce | the value moves into the journal | the same value, type and data, back in place |
| Service | start type Disabled (`ChangeServiceConfigW`) | the start type the journal recorded before, else Manual |
| Scheduled task | `IRegisteredTask::put_Enabled(false)` | `true` |
| Context menu | the class id under `Shell Extensions\Blocked` | the value removed |

RegistryGuard checks every value it writes and every service key it changes, so boot and system
start services, services whose image lies in the Windows folder and services whose start type
cannot be read show as locked. `restore` on such a transaction writes the earlier state back only
where the current one is still what Niskala set; anything changed since is left alone and named.

The boot-impact estimate is a score from the program's size (below 1 MB, 10 MB, 50 MB, above),
plus one on a hard disk, plus one for an automatic service or minus one for a delayed one: up to
one is Low, two Medium, more High. Disabled items, manual services, tasks without a sign-in or
start-up trigger and context-menu handlers have none; RunOnce entries stay at Low; a missing
program is Unknown.

## Process Suspend

Process Suspend may touch a process only when all of this holds, checked again right before every
action: it is not the idle or system process, not Niskala, not an ancestor of the running Niskala,
runs in this session (never session 0), its image is known, lies outside the Windows folder, is not
a `niskala*` program or in Niskala's folder, and `IsProcessCritical` says no (a process that cannot
be asked is refused). Three restraints:

- **Suspend**: `NtSuspendProcess`; Resume runs `NtResumeProcess`.
- **Efficiency mode**: EcoQoS through `SetProcessInformation(ProcessPowerThrottling)` with
  execution-speed throttling, and the idle priority class; Resume restores the earlier priority and
  hands throttling back to Windows.
- **CPU cap**: a job object with a hard CPU-rate limit (`JOBOBJECT_CPU_RATE_CONTROL_INFORMATION`).
  The cap lives as long as Niskala holds the job: the page lifts it on Resume or when the app
  closes, and `niskala optimize limit` holds it for `--seconds` or until Ctrl+C.

Suspensions and efficiency mode are kept in `restraints.json` with each process's start time, so
Resume works after Niskala restarts and never acts on a new process that reused the id. The list
measures CPU use over a short sample and marks a process idle in the background when it has no
visible window and used less than 0.5% CPU.

## Toolkit

**Storage Map** walks a folder with a pool of workers over a channel (`FileSystemEnumerable`, one
folder per work item). A folder with `FileAttributes.ReparsePoint` is counted as a link and never
entered, so junction loops cannot happen. The result is a tree of sizes, file and folder counts,
the largest sub-folders per level, and file-type groups by extension; unreadable folders are
counted.

**Duplicate Scan** compares in three stages: files of exactly the same size; then SHA-256 over the
first and last 4 KB (a file of 8 KB or less is hashed whole here and needs no third stage); then a
full SHA-256. Hard links to one file count once, junctions and hidden or system files are skipped
unless asked for. Every copy in a result group is checked by PathGuard; protected copies are shown
but never removed. The keep rule (oldest or newest last write) picks the copy that stays; before a
removal the kept copy and each copy to remove are hashed again, and any that changed stays. The rest
goes through the Guardian as a `DuplicateClean` transaction, to the Recycle Bin by default.

**Secure Wipe** overwrites and deletes; it makes no backup and writes no Guardian journal. A file must
pass PathGuard, be a regular file (no folder, link, or compressed, encrypted or sparse file, whose
data moves when rewritten) with no other hard link, and open exclusively. Each pass rewrites the
whole length with write-through and flushes to the disk:

| Method | Passes |
|---|---|
| `ZeroFill` | `00` |
| `Dod522022M` | `00`, `FF`, random |
| `Gutmann` | 4 random, the 27 fixed patterns (`55`, `AA`, three `92 49 24` phases, `00` to `FF` in steps of `11`, three again, three `6D B6 DB` phases), 4 random |

Multi-byte patterns keep their phase across chunks. The file is then cut to zero length, its times
set to 2000-01-01, renamed three times to random names and deleted. A free-space wipe needs a fixed
local drive: it creates a new folder at the drive's root, fills the free space with 1 GB pattern
files until the drive is full, rewrites them for every further pass and deletes the folder, also
when cancelled. Solid-state drives remap blocks, so neither kind of wipe can promise that an old
copy in a spare block is gone; the CLI and the page say so.

## Uninstall execution

`JobTracker` starts the resolved command with `CreateProcessW` and
`PROC_THREAD_ATTRIBUTE_JOB_LIST`, so the process is in the job before its first instruction.
The job has no `KILL_ON_JOB_CLOSE` limit: closing Niskala never kills an uninstaller. The working
directory is System32, so no handle keeps the install folder open. Job notifications arrive on an
I/O completion port.

`CompletionDetector` declares an uninstall finished when all of these hold:

1. the job is empty (`ACTIVE_PROCESS_ZERO`, confirmed with the job's process list), or only a
   browser outside the install folder is left (a survey page);
2. `Global\_MSIExecute` is free, because the Windows Installer service works outside the job;
3. the Uninstall key and the install folder stayed unchanged for 3 seconds (60 seconds at most).

`uninstall` runs the plan in this order:

1. resolve the command and run the preflight, as `inspect` does;
2. take the fingerprint and save it to `%LocalAppData%\Niskala\fingerprints`, so `leftovers` still
   has it once the application is gone;
3. ask the Guardian for a checkpoint: a restore point and a backup of the keys the fingerprint
   names (see Guardian);
4. run the preflight again, then start the uninstaller in a job and wait for completion, or remove
   a package with `PackageManager.RemovePackageAsync` for the current user;
5. verify: the Uninstall key, Windows Installer's product state (`MsiQueryProductState`), the
   package and the files left in the install folder.

The outcome comes from the verification first and from the exit code second: an entry that is gone
is `Removed` whatever the exit code. Windows Installer and Burn codes are mapped (1602 cancelled,
1605 stale entry, 1618 installer busy, 3010 and 1641 restart); NSIS 1 is a cancel. NSIS relaunches
itself from `%TEMP%`, so its exit code is the launcher's. `--dry-run` runs nothing. Without `--yes`
the command asks for confirmation, and with redirected input it refuses instead. Ctrl+C stops the
wait, never the uninstaller. Exit codes: 0 removed (also pending restart, stale entry, or success
that could not be verified), 1 blocked or not removed, 2 no single match or no confirmation, 130
declined or stopped waiting.

## Leftover scanner

`leftovers` changes nothing. It takes an installed application (with a fingerprint taken now) or
one removed by `uninstall` (with the saved fingerprint).

Names are normalized: NFKC, lower case, without ™®©, version numbers, architecture and locale labels
and trailing company suffixes. The product tokens are the display name, the name without the
publisher in front ("Mozilla Firefox" -> "firefox") and the install folder's name, compared without
spaces. Common words (`update`, `tools`, `data`, ...) never count alone. Tokens shorter than four
characters count only as an exact name below the publisher's folder or as the install folder's own
name.

Where it looks:

- **files**: one and two levels (`root\Product`, `root\Publisher\Product`) below Program Files,
  Common Files, ProgramData, every profile's Roaming, Local, LocalLow, `Local\Programs`,
  `Documents\My Games` and `Saved Games`, the Start menus, the desktops and `%TEMP%`; plus every
  fingerprint path that still exists, every shortcut that points into the install folder, and the
  Prefetch traces (`%SystemRoot%\Prefetch\<EXE>-<hash>.pf`) of the programs the fingerprint found
  there. The fingerprint leaves out redistributables and crash reporters that many programs ship
  under the same name (`vc_redist*`, `vcredist*`, `dxsetup*`, `UnityCrashHandler*`,
  `CrashReportClient*`, ...), whose traces would match other programs;
- **registry by name**: `SOFTWARE\Product` and `SOFTWARE\Publisher\Product` (HKLM 64 and 32-bit,
  HKCU), the app's own Uninstall entry, Windows Installer keys named after the ProductCode or
  UpgradeCode (plain and packed) once Windows Installer no longer lists the product;
- **registry by path**: every registration that names a path in the install folder, matched as a
  path prefix: services, `Run`/`RunOnce`, `App Paths`, `SharedDLLs`, `Installer\Folders`,
  `AppCompatFlags\Layers` and `Compatibility Assistant\Store`, the shell `MuiCache`, ProgIDs and URL
  protocols in `HKLM\SOFTWARE\Classes` and `HKCU\Software\Classes` whose open command or icon lies
  there, `Applications\<exe>`, the file types that open with those ProgIDs (only the default value
  and the `OpenWithProgids` entry, never the shared extension key), COM classes, AppIDs and type
  libraries whose server lies there, and the shell handlers and approvals of those classes;
- **firewall rules** (`INetFwPolicy2`) for a program in the install folder or a recorded service.
  Rules are removed by name and several rules may share one, so a name is proposed only when every
  rule carrying it belongs to the application;
- **Steam records** of a Steam game, by AppId (see Steam games);
- **scheduled tasks** (`ITaskService`, hidden tasks included, never below `\Microsoft\`): a task
  that starts a program in the install folder, or names one in its arguments, and a task named like
  the application, or in a folder named like it, whose program is gone. Task Scheduler keeps each
  task as a file in `%SystemRoot%\System32\Tasks` and an entry under `Schedule\TaskCache`; its API
  reads and removes both together;
- **Store app data**: `%LocalAppData%\Packages\<PackageFamilyName>`, which removing the package
  normally deletes.

| Signal | Weight |
|---|---|
| ProductCode or UpgradeCode, the app's own Uninstall entry | +70 |
| Path recorded in the fingerprint | +60 |
| Value, shortcut or COM server points into the fingerprint | +50 |
| Name equals a product token | +40 |
| `Publisher\Product` | +25 |
| Created (keys: last written) within a day of the install date | +10 |
| Name only contains a token (ignored when fingerprint or installer evidence exists) | -40 |
| Name matches an application that is still installed | -50 |
| Kept by Steam under the game's AppId | +90 |
| Data folder Windows keeps for the Store app's package family | +90 |
| Scheduled task named like the app whose program is gone | +15 |

Scores are clamped to 0-100: 80 and above is **Safe**, 50 to 79 **Review**, below 50 **Low** (shown
with `--low`). Personal data is never selected by default: a match below `Documents\My Games` or
`Saved Games`, below AppData for a Steam game, or a Steam Cloud folder in `userdata` is placed in
**Review** (50 to 79) when it scores 40 or more, so it is listed but left unchecked; a weaker match
stays where it is. A file or folder inside a proposed folder goes with it, and its fingerprint evidence
moves up. Never proposed: anything PathGuard or RegistryGuard denies, files and folders owned by
TrustedInstaller, folders whose program files are all Microsoft-signed (`WinVerifyTrust`, embedded
signatures, Microsoft root policy), registry entries whose file is Microsoft-signed, and folders that
are, hold or lie in another installed application's install folder. Lying inside another
application's folder is accepted only with evidence that names the application itself (its
installer identity or its Steam AppId), as a game's caches lie inside Steam's folder. Empty publisher folders are
offered after the clean (see Guardian).

A found folder or key can be expanded one level at a time (`LeftoverTree`): subfolders with their
file count and size, files with size and date, subkeys, and values with their `REG_*` type and data
as Regedit shows it, at most 5,000 entries per level. Junctions and symbolic links are leaves. A
checked part inside a leftover keeps the leftover's score and reasons, and force accepts such parts
when the scan after stopping the processes finds the leftover holding them again.
`leftovers --tree <depth>` prints the same tree.

## Steam games

A Steam game's Uninstall entry is `Steam App <AppId>`, and its uninstall command opens the Steam
client. Niskala removes such a game directly instead. It reads the client's folder from
`HKCU\Software\Valve\Steam` (`SteamPath`), the libraries from `steamapps\libraryfolders.vdf` and
the game from `steamapps\appmanifest_<AppId>.acf` (`installdir`, `SizeOnDisk`). A manifest whose
`installdir` is not a single folder name, or an Uninstall entry that names another folder than the
manifest, gets no plan. The removal is one Guardian `Force` transaction of what exists:

- `steamapps\common\<installdir>` and `appmanifest_<AppId>.acf`;
- in every library: `shadercache\<AppId>`, `workshop\content\<AppId>`,
  `workshop\appworkshop_<AppId>.acf`, `downloading\<AppId>` and `temp\<AppId>`;
- in the client folder: the library art in `appcache\librarycache` (the folder `<AppId>` and files
  named `<AppId>_*` or `<AppId>.*`, nothing else);
- `HKCU\Software\Valve\Steam\Apps\<AppId>`, Steam's install state (left behind, it makes Steam
  write the Uninstall entry again), and `Steam App <AppId>` in the 64-bit and 32-bit Uninstall keys;
- the `.url` shortcuts on the desktops and in the Start menus that start `steam://rungameid/<AppId>`,
  the icon Steam made for them in `steam\games` when no other shortcut uses it, and the shortcut's
  Start menu state (`Start\TileProperties\W~steam://rungameid/<AppId>`).

When the manifest is gone but Steam still lists the game (Steam then writes the Uninstall entry
again, without `InstallLocation`), the plan holds Steam's records alone, and `--via-steam` is
refused because Steam cannot uninstall a game whose files are gone.

Steam's own apps (the client, 7, and the screenshot store, 760) are never matched. Before anything is
removed, the fingerprint (the program names of the install folder, without redistributables, the
folder, the name and the publisher) is saved like any uninstall's, so `leftovers` and `clean` work
on the game afterwards. After the removal the same fingerprint drives a leftover scan, in the
desktop app's review trees and in `niskala uninstall`, which offers the Safe items and lists the rest:
the game's folders in AppData, LocalLow, ProgramData, `Documents\My Games` and `Saved Games`, its
Prefetch traces, compatibility and MUI cache records, firewall rules and `SOFTWARE` keys. The
Steam Cloud copies in `userdata\<account>\<AppId>` (every local account) are never part of the
removal: the scan lists them for review.

While Steam runs, the files are not touched. The desktop app offers, and the CLI takes:

- `--close-steam`: `steam.exe -shutdown`, wait up to 60 s for Steam to exit, then remove directly;
- `--via-steam`: open `steam://uninstall/<AppId>` and let Steam remove the game, for example to sync
  Steam Cloud first; the leftover scan can run once Steam has finished.

Steam is started with the desktop shell's unelevated token. `uninstall` without either flag stops
with exit code 2 while Steam runs.

## Batch Remove

`niskala uninstall` with several applications, or Batch Remove in the desktop app, plans every
application first and lists what cannot run (no uninstall command, a blocking preflight check, a
Steam game while Steam is running). After one confirmation the queue runs in order, each app with
its own uninstaller, package removal or direct Steam removal, without leaving the flow; only the
first app that runs requests a System Restore point. Once the queue is done, the inventory is read
again and every removed app is scanned with its fingerprint, so the apps of the queue no longer
shield each other's folders. The scans become one review: an item that several apps left is listed
once, with its highest score, and the chosen items are removed as one Guardian transaction per app
that left them. In a batch the leftovers always come from the scan, also for apps with an install
log.

## Guardian

The Guardian makes every removal a journaled transaction in
`%LocalAppData%\Niskala\transactions\<id>`: `transaction.json`, replaced atomically after each
item, `registry.json.gz`, a gzip JSON backup with every value's type and bytes exactly as
stored, and `registry.reg`, the same backup in Registry Editor format (UTF-16, version 5.00) for use
without Niskala. Strings and DWORDs that fit the plain notation are written as text; every other
value is written as `hex(type):` so an import restores the bytes exactly. Unlike `restore`, an
import overwrites values that exist again; a value whose name holds a line break exists only in
the JSON backup. `clean` and `force` run in this order:

1. record the intent: every item as `Pending`;
2. create a restore point through WMI (`root\default:SystemRestore.CreateRestorePoint`) when
   elevated. Windows skips it silently when one was made recently (24 hours by default,
   `SystemRestorePointCreationFrequency`), and a new point reaches the list a second or so after the
   call returns, so the list is read again every 250 ms for up to 10 seconds before the result
   counts as no restore point;
3. back up the registry keys and values, the full definition of each firewall rule and the
   definition XML of each scheduled task, and save the backup before anything is removed;
4. check each item again and remove it.

The check before removal opens the object and binds every decision to that handle:

- **files and folders**: PathGuard on the path; the handle (`FILE_FLAG_OPEN_REPARSE_POINT`) must
  resolve to the same physical path and still be a file or a folder as scanned; owner not
  TrustedInstaller; `WinVerifyTrust` reads program files through their handles.
- **permanent removal**: every child is opened relative to its parent's handle (`NtCreateFile`
  with `RootDirectory` and `FILE_OPEN_REPARSE_POINT`) and deleted with
  `SetFileInformationByHandle(FileDispositionInfoEx)` (POSIX semantics, read-only ignored). A folder
  swapped for a junction after the check, or a junction inside the tree, is removed as a link; its
  target is never visited. Files in use are scheduled with `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)`,
  which needs elevation. When elevated, `SeBackupPrivilege` and `SeRestorePrivilege` are enabled for
  the removal loop only, so the handles opened with `FILE_OPEN_FOR_BACKUP_INTENT` reach items whose
  access list shuts out administrators; PathGuard, the owner check and the signature check still
  decide what may go.
- **Recycle Bin**, the default: `IFileOperation` on a dedicated STA thread. Its progress sink cancels
  any item the shell would delete permanently (no `TSF_DELETE_RECYCLE_IF_POSSIBLE`). The checked
  handle stays open during the move, so afterwards it must point at the reported `$R` item.
- **registry**: RegistryGuard again; every key of the tree is opened with `REG_OPTION_OPEN_LINK`,
  and a tree with a registry symbolic link is refused as a whole. Keys are deleted bottom up with
  `NtDeleteKey` on those handles. A service key of a registered service goes through the SCM
  (`ControlService` stop, `DeleteService`).
- **firewall**: every rule carrying the name must still belong to the application.
- **scheduled tasks**: the task is read again, and each of its programs must still be one of the
  application's or be gone; it is then deleted through `ITaskFolder::DeleteTask`.

Afterwards the Guardian offers the topmost folders below a known root (Program Files, ProgramData,
the profile folders, the Start menus) that held nothing but removed items, such as
`%AppData%\Publisher`; they go only once confirmed and only while still empty.

`restore <id>` puts back what can come back without overwriting anything that exists now: missing
registry keys and values, Recycle Bin items (the `$R` item moves back and its `$I` record goes),
empty folders, firewall rules and scheduled tasks, registered again from their XML with the
principal they had (`RegisterTaskDefinition`, missing task folders created). A task whose path is
taken again stays as it is, and one that kept a stored password cannot come back. Permanently
deleted files cannot come back; the restore point can.
`uninstall` also writes a transaction: the restore point plus a backup of the Uninstall entry, App
Paths, service keys and autostart values in the fingerprint.

## Force Remove and the Target Picker

`force` removes an application without running its uninstaller: an inventory entry, a bare install
folder (named after the folder, its parent as publisher unless that is a known root), a program or
shortcut of it, or the window, desktop icon or tray icon under a screen point. It ends processes whose image lies in the app's folders (the image is read
again through the handle that terminates, so a reused process id is never hit, and a process
Windows marks critical, `IsProcessCritical`, is never ended; `SeDebugPrivilege` is enabled only
for the `OpenProcess` call), stops its services,
scans again and hands the Safe leftovers and the app's own Uninstall entry to the Guardian as one
`Force` transaction. Packages are refused, and Windows Installer products get a warning: force does
not remove Windows Installer's own registration.

Target Picker (`WindowPicker`) maps a point to the top-level window the user sees there, then to its
process (`GetWindowThreadProcessId`, `QueryFullProcessImageName`, `GetPackageFullName`; for UWP
frames the hosted `CoreWindow`) and an inventory entry: package family first, then the deepest
install folder holding the image, then a `DisplayIcon` that names it. It does not use
`WindowFromPoint`, which stops at the transparent overlays of other processes. It walks the
z-order (`GetTopWindow`, `GW_HWNDNEXT`, a set of seen handles ends a walk the changing z-order
loops) and passes over Niskala's own windows, hidden, minimized and cloaked windows (`DWMWA_CLOAKED`),
click-through windows (`WS_EX_TRANSPARENT`) and layered windows whose constant alpha is 0, so game,
recording and chat overlays such as the GeForce, Steam, Discord and Xbox Game Bar overlays are looked
through. Overlays drawn with `UpdateLayeredWindow` keep their alpha per pixel, which Win32 does not
expose, so a layered tool window that never activates (`WS_EX_LAYERED`, `WS_EX_NOACTIVATE`,
`WS_EX_TOOLWINDOW`), without a caption or sizing frame and spread over a whole monitor
(`MonitorFromWindow`), is looked through as well. Each window is tested against its DWM frame bounds
(`DWMWA_EXTENDED_FRAME_BOUNDS`).

On the desktop (`Progman` or any `WorkerW`, or no window at all), the point goes to the list view
that draws the icons: `SHELLDLL_DefView` and `SysListView32` in Progman or in whichever WorkerW
holds them. Its items are read once per session through Explorer's memory (`VirtualAllocEx`,
`LVM_GETITEMCOUNT`, `LVM_GETITEMRECT` with `LVIR_BOUNDS`, which spans the picture and the label,
and `LVM_GETITEMTEXTW`); the pointer is converted with `ScreenToClient` and tested against those
rectangles, which works from an elevated process where the shell's COM windows answer nothing.
When Explorer's memory cannot be opened, the list view's own Active Accessibility object
(`AccessibleObjectFromWindow`, `accHitTest`, `accLocation`) names the icon instead; asking the list
view directly keeps the overlay out of the way. A label leads to the file on the user's or the
shared desktop that Explorer shows under it.

On the taskbar, the classic notification area is a `ToolbarWindow32` whose buttons carry the owner
window of each icon (`TB_GETBUTTON`, the icon record read from Explorer's memory). The Windows 11
taskbar draws its icons in XAML, so each icon remembered in `HKCU\Control Panel\NotifyIconSettings`
(program path with its known-folder prefix expanded, uID or GUID) whose program runs is asked for
its rectangle with `Shell_NotifyIconGetRect`. Icons hidden in the overflow area all report the
rectangle of the button that opens it, so they are named only while the overflow window is open.

A file leads to an app the same way a desktop icon does: a `.lnk` to its stored target (never
`Resolve`d) or, advertised, to its Windows Installer product; a `.url` to `steam://rungameid/<AppId>`
and its Steam game, or to a `file://` path; any other file stands for itself.

## Install Tracker

`track` needs elevation: the kernel providers can only be traced from an elevated session. It starts
a real-time ETW session (`Microsoft-Windows-Kernel-Process`, `-Kernel-File` and `-Kernel-Registry`) in
`Niskala.EtwHelper.dll`, waits until the first buffer arrives, then starts the installer suspended
inside a job object, tracks it and resumes it.

- **Process tree.** The helper tracks every process whose parent is tracked and forwards only their
  file and registry events. Real-time buffers arrive per processor, so a child's first events can
  come before its start event; events of processes it has not seen start wait (up to 15 s) until
  that start event decides. Windows Installer works in its service, which the tree does not start:
  once the tree runs `msiexec.exe`, the running service, or one services.exe starts later, is traced
  too. Recording ends when the job is empty, or on Enter for setups that leave a program running.
- **Changes.** File creations (`CreateNewFile`), first writes per open, deletes and renames; keys
  created new (the provider's disposition), deleted keys, set and deleted values with the value's
  previous type and data when the provider captured them whole. The first event on a path says
  whether it existed before; the machine's state after the install gives the net change, so a
  temporary file or a value set back to what it was leaves nothing.
- **Key names.** A registry event names its key relative to a handle (`BaseObject`), often one
  opened before the change, such as the process's `HKEY_CURRENT_USER`. The helper therefore also
  takes the provider's key-open events and keeps the full name of every key a tracked process opens
  or creates, so a change relative to any of them resolves to a `\REGISTRY\...` path. A change
  whose base it never saw counts as unnamed and is reported in the log's notes.
- **Brokered changes.** The Service Control Manager and the firewall service write for the installer;
  services and firewall rules that appeared during the install and point into files it created are
  added to the log.
- **Noise.** Left out and counted by reason: temporary files, Prefetch, internet, browser and shader
  caches, shell state (MRU lists, UserAssist, bags), crypto state, network tracing, Windows
  Installer bookkeeping, error reports, and anything PathGuard or RegistryGuard refuses. The one
  exception is a revert-only value: `Path` under `HKCU\Environment` and under
  `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Environment` stays in the log, marked
  `revertOnly`. The kernel names keys by their real path, so `ControlSetNNN` is mapped back to
  `CurrentControlSet` first.

The log is `%LocalAppData%\Niskala\logs\<id>.nsklog`: JSON (`format: nsklog`, schema 1) with the
processes, files, keys, values (data after the install and, when known, before), firewall rules,
the filtered counts, trace statistics (events lost, unresolved names) and notes. The Uninstall entry
the install created links the log to the inventory entry.

`logs rollback` undoes a log as one `Rollback` transaction through the same Guardian path as `clean`:
the outermost created folders, files, keys and values are removed, and values the install changed or
deleted are written back to their earlier data, each only while it still holds what the install
left. A revert-only value is written back only from and to `REG_SZ` or `REG_EXPAND_SZ` data, keeps
the type it had before the install, and is never removed: one the install created is listed as kept.
Explorer and running programs are told the environment changed (`WM_SETTINGCHANGE`). Changed files,
deleted files and values whose earlier data was not captured are listed as not undoable. `uninstall` of an app with a log runs its uninstaller first, then offers the rollback of
what is left. `restore` undoes the rollback like any transaction; values written back stay.

## Safety layer

Every removal must pass a guard twice: when the scanner proposes it and again right before the
Guardian acts.

`PathGuard` checks, in order:

1. syntax: UNC, device namespace, alternate data streams, `.`/`..`, trailing dots or spaces, DOS device names;
2. protection rules on the lexical path;
3. physical resolution through a handle (`CreateFileW` + `GetFinalPathNameByHandleW`): junctions, symbolic links, SUBST, 8.3 names, network drives;
4. protection rules again on the physical path.

The rules come in two kinds. `Tree` protects a location and everything below it. `Self`
protects only the location itself, so its children may be removed. The deepest matching rule
wins, and a path that contains a protected location is denied as well. Inside the Windows folder
one `Self` rule opens `Prefetch` for its trace files; the folder, `ReadyBoot` and `Layout.ini` stay
protected. The cleaner builds its own PathGuard with a few more `Self` rules for the caches its
rules name (see [Cleaner](#cleaner)); the uninstaller never sees them. Niskala's own folders are protected too: the program's folder, the folder above a
release's `lib\`, and `%LocalAppData%\Niskala`.

The Edge and WebView2 runtime folders below `Program Files (x86)\Microsoft` are protected as
trees: other applications depend on WebView2.

`RegistryGuard` applies the same rule model to keys. All of `HKLM` is a tree, with named rules for
`SYSTEM`, `SYSTEM\CurrentControlSet`, `SAM`, `SECURITY`, `BCD00000000` and `HARDWARE`, so a denial
names the hive that blocked it. It folds `WOW6432Node` into the 32-bit
view and refuses `HKCR`, because `HKCR` merges two hives and a removal must name the physical
one. It also protects named values such as `HKCU\Environment\Path`. Inside `Software\Microsoft`
the `Shell Extensions\Blocked` key is open for values, which is how Resource Control switches a
context-menu handler off. `Local Settings` in the user's
classes is protected as a tree, except the shell `MuiCache`, whose values (one per program) may go,
and in the user's `Software\Microsoft` tree the Start menu's `TileProperties` key, whose subkeys
(one per shortcut) may go. A service key may be removed
only when the service does not start at boot or system start and its image lies outside the
Windows folder; third-party drivers are removed through `pnputil`, never by deleting their keys.
The guard is lexical: the Guardian opens keys with `REG_OPTION_OPEN_LINK` to refuse registry
symbolic links.

## Logs

Diagnostic logs are JSON lines in `%LocalAppData%\Niskala\logs` (`*.jsonl`, next to the `*.nsklog`
install logs). Event ids are grouped by module: Core
1xxx, Scanner 2xxx, Uninstall 3xxx, Guardian 4xxx, Tracker 5xxx, Repair 6xxx, Cleaner 7xxx, UI 8xxx,
CLI 9xxx, Optimizer 10xxx.

## License

Copyright (c) 2026 Adriansyah101

Niskala Utilities is free software: you can redistribute it and/or modify it under the terms of
the GNU General Public License as published by the Free Software Foundation, version 3
(`GPL-3.0-only`). The full text is in [LICENSE](LICENSE); every release package carries a copy
in `lib\LICENSE`.

Niskala Utilities is distributed in the hope that it will be useful, but it is provided "AS IS",
WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
PARTICULAR PURPOSE. See sections 15 and 16 of the license for the full disclaimer of warranty and
limitation of liability.
