using System.Globalization;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Repair;
using Niskala.Scanner.Leftovers;
using Niskala.Tracker;
using Niskala.Tracker.Logs;
using Niskala.Uninstall;
using Niskala.Uninstall.Picker;

namespace Niskala.UI.Services;

public sealed partial class Strings
{
    private Strings(bool indonesian)
    {
        Indonesian = indonesian;
        Culture = CultureInfo.GetCultureInfo(indonesian ? "id-ID" : "en-US");
    }

    public static Strings Current { get; private set; } = new(false);

    public bool Indonesian { get; }

    public CultureInfo Culture { get; }

    public string AppTitle => "Niskala Utilities";

    public string Administrator => T("Administrator", "Administrator");
    public string PaletteLauncher => T("Search apps or run a command", "Cari aplikasi atau jalankan perintah");
    public string PalettePlaceholder => T("Type an app name or a command…", "Ketik nama aplikasi atau perintah…");
    public string ThemeLight => T("Light theme", "Tema terang");
    public string ThemeDark => T("Dark theme", "Tema gelap");
    public string SwitchLanguage => T("Bahasa Indonesia", "English");

    public string NavApps => T("Apps", "Aplikasi");
    public string NavTracker => T("Install Tracker", "Pemantau Instalasi");
    public string NavPicker => T("Target Picker", "Mode Bidik");
    public string NavResources => T("Resource Control", "Kendali Kinerja");
    public string NavRepair => T("System Repair", "Perbaikan Sistem");
    public string NavHistory => T("History & backups", "Riwayat & Cadangan");

    public string Unknown => T("Unknown", "Tidak diketahui");
    public string Estimated => T("estimated", "perkiraan");
    public string DefaultValue => T("(default)", "(bawaan)");
    public string Untitled => T("Untitled window", "Jendela tanpa judul");
    public string NotRun => T("Not run", "Tidak dijalankan");
    public string Collecting => T("Reading what the app owns…", "Membaca apa yang dimiliki aplikasi…");
    public string Scanning => T("Looking for leftovers…", "Mencari sisa…");
    public string Blocked => T("Blocked by the checks below.", "Diblokir oleh pemeriksaan di bawah.");
    public string NeedsElevation => T("Machine-wide entries need Niskala to run as administrator.", "Entri untuk seluruh mesin membutuhkan Niskala sebagai administrator.");
    public string PickInstallerTitle => T("Choose an installer to record", "Pilih installer yang akan direkam");
    public string InstallerFiles => T("Installers", "Installer");
    public string PickFolderTitle => T("Choose the folder of the app to remove", "Pilih folder aplikasi yang akan dihapus");
    public string Refresh => T("Refresh", "Muat ulang");
    public string Close => T("Close", "Tutup");
    public string Cancel => T("Cancel", "Batal");

    public string AppsTitle => NavApps;
    public string AppsSearch => T("Filter the list", "Saring daftar");
    public string AppsLoading => T("Reading the inventory…", "Membaca inventaris…");
    public string AppsEmpty => T("No app matches this filter.", "Tidak ada aplikasi yang cocok dengan saringan ini.");
    public string FilterAll => T("All", "Semua");
    public string FilterWin32 => "Win32";
    public string FilterStore => T("Store apps", "Aplikasi Store");
    public string FilterLarge => T("Large", "Besar");
    public string FilterRecent => T("Recently installed", "Baru dipasang");
    public string FilterBroken => T("Broken", "Rusak");
    public string SortBy => T("Sort", "Urutkan");
    public string SortName => T("Name", "Nama");
    public string SortSize => T("Size", "Ukuran");
    public string SortDate => T("Install date", "Tanggal pasang");

    public string LabelLocation => T("Install folder", "Folder instalasi");
    public string LabelSize => T("Size", "Ukuran");
    public string LabelInstalled => T("Installed", "Dipasang");
    public string LabelCommand => T("Uninstall command", "Perintah uninstall");
    public string LabelWebsite => T("Website", "Situs web");
    public string OpenFolder => T("Open folder", "Buka folder");
    public string OpenWebsite => T("Open website", "Buka situs");
    public string Silent => T("Silent mode", "Mode senyap");
    public string SilentHint => T("Adds the installer's quiet switches when Niskala knows them.", "Menambahkan sakelar senyap installer bila Niskala mengenalnya.");
    public string RestorePointToggle => T("Create a restore point", "Buat titik pemulihan");
    public string Uninstall => T("Uninstall", "Copot");
    public string Force => T("Force Remove", "Copot Paksa");
    public string ForceHint => T("Without the app's uninstaller: stops its processes and services, then removes what it owns.", "Tanpa uninstaller aplikasi: menghentikan proses dan layanannya, lalu menghapus miliknya.");
    public string ScopeUser => T("This user", "Pengguna ini");
    public string ScopeMachine => T("All users", "Semua pengguna");
    public string ProtectedApp => T("Windows needs this app; Niskala will not remove it.", "Windows membutuhkan aplikasi ini; Niskala tidak akan menghapusnya.");
    public string OrphanApp => T("This entry has no uninstall command. Force Remove can still remove what it owns.", "Entri ini tidak punya perintah uninstall. Copot Paksa tetap bisa menghapus miliknya.");
    public string UninstallerMissing => T("The uninstaller file is missing. Force Remove can still remove what the app owns.", "Berkas uninstaller hilang. Copot Paksa tetap bisa menghapus milik aplikasi.");
    public string NotInInventory => T("This folder is not an installed app; only Force Remove applies.", "Folder ini bukan aplikasi terpasang; hanya Copot Paksa yang berlaku.");
    public string NoUninstallCommand => T("No uninstall command", "Tidak ada perintah uninstall");
    public string FinishRunningFirst => T("Another removal is still running; finish it first.", "Penghapusan lain masih berjalan; selesaikan dulu.");

    public string StepFingerprint => T("Fingerprint", "Sidik jari");
    public string StepRestorePoint => T("Restore point", "Titik pemulihan");
    public string StepProcessTree => T("Live process tree", "Pohon proses live");
    public string StepScan => T("Leftover scan", "Pindai sisa");
    public string StepReview => T("Review & clean", "Tinjau & bersihkan");
    public string StepFingerprintScan => T("Fingerprint & scan", "Sidik jari & pindai");
    public string StepStopProcesses => T("Stop processes & services", "Hentikan proses & layanan");
    public string StepRemove => T("Remove", "Hapus");
    public string StepSteamFind => T("Find in Steam", "Cari di Steam");
    public string Skipped => T("skipped", "dilewati");
    public string Finish => T("Finish", "Selesai");
    public string ScanLeftovers => T("Scan leftovers", "Pindai sisa");
    public string JobEmpty => T("All uninstaller processes ended; waiting for the registry and the folder to settle.", "Semua proses uninstaller selesai; menunggu registri dan folder stabil.");

    public string SteamDirectCommand => T("Removed directly, without opening Steam", "Dihapus langsung, tanpa membuka Steam");
    public string SteamLooking => T("Reading Steam's library folders and manifests…", "Membaca folder pustaka dan manifest Steam…");
    public string SteamStartTitle => T("Ready to remove the game", "Siap menghapus game");
    public string SteamRunning => T("Steam is running: choose to close it first or let Steam uninstall the game.", "Steam sedang berjalan: pilih menutupnya dulu atau biarkan Steam yang mencopot game.");
    public string SteamClosed => T("Steam is not running; the game can be removed directly.", "Steam tidak berjalan; game dapat dihapus langsung.");
    public string SteamDirect => T("Remove directly: the game's folder, Steam's caches and records of it, its shortcuts", "Hapus langsung: folder game, cache dan catatan Steam tentangnya, serta shortcut-nya");

    public string SteamDirectRecords => T("Remove what Steam still keeps of it: caches, records and shortcuts", "Hapus yang masih disimpan Steam: cache, catatan, dan shortcut");
    public string SteamCloseThenRemove => T("Close Steam, then remove directly", "Tutup Steam, lalu hapus langsung");
    public string SteamVia => T("Let Steam uninstall it (syncs Steam Cloud first)", "Biarkan Steam yang mencopot (sinkron Steam Cloud dulu)");
    public string SteamPermanent => T("Delete the files permanently (a game larger than the Recycle Bin is otherwise left in place)", "Hapus berkas secara permanen (game yang lebih besar dari Recycle Bin akan tertinggal jika tidak)");
    public string SteamClosing => T("Asking Steam to exit…", "Meminta Steam keluar…");
    public string SteamHandedOver => T("Steam was asked to uninstall the game and shows its own dialog.", "Steam diminta mencopot game dan menampilkan dialognya sendiri.");
    public string SteamScanLater => T("Once Steam has finished, scan for leftovers.", "Setelah Steam selesai, pindai sisa.");
    public string SteamStillRunning => T("Steam is still running; nothing was removed.", "Steam masih berjalan; tidak ada yang dihapus.");

    public string StartTitle => T("Ready to run the uninstaller", "Siap menjalankan uninstaller");
    public string Start => T("Start", "Mulai");
    public string StopWaiting => T("Stop waiting", "Berhenti menunggu");
    public string StopWaitingHint => T("The uninstaller keeps running; only Niskala stops following it.", "Uninstaller tetap berjalan; hanya Niskala yang berhenti mengikutinya.");
    public string Clean => T("Clean selected", "Bersihkan yang dipilih");
    public string SkipClean => T("Keep everything", "Biarkan semua");
    public string RemoveEmptyFolders => T("Remove empty folders", "Hapus folder kosong");
    public string ShowInHistory => T("Show in history", "Lihat di riwayat");
    public string Warnings => T("Checks and notes", "Pemeriksaan dan catatan");
    public string ChooseWhatGoes => T("Choose what goes.", "Pilih yang akan dihapus.");
    public string LeftoversKept => T("Nothing was removed.", "Tidak ada yang dihapus.");
    public string StoppedWaiting => T("Stopped waiting; the uninstaller keeps running.", "Berhenti menunggu; uninstaller tetap berjalan.");
    public string RestorePointSkipped => T("Windows skipped the restore point.", "Windows melewati titik pemulihan.");
    public string RestorePointOff => T("Turned off for this run.", "Dimatikan untuk proses ini.");
    public string RestorePointRequested => T("Requested from Windows.", "Diminta dari Windows.");
    public string CreatingRestorePoint => T("Creating a restore point…", "Membuat titik pemulihan…");
    public string BackingUp => T("Backing up registry keys…", "Mencadangkan kunci registri…");
    public string UninstallerRunning => T("The uninstaller is running.", "Uninstaller sedang berjalan.");
    public string Verifying => T("Checking what is left…", "Memeriksa yang tersisa…");
    public string StoppingProcesses => T("Stopping processes and services…", "Menghentikan proses dan layanan…");
    public string ProcessesStoppedShort => T("Processes and services stopped.", "Proses dan layanan dihentikan.");
    public string Processes => T("Processes", "Proses");

    public string TierSafe => T("Safe", "Aman");
    public string TierReview => T("Review", "Tinjau");
    public string TierLow => T("Low", "Rendah");
    public string TreeFiles => T("Files & folders", "Berkas & folder");
    public string TreeRegistry => T("Registry & firewall", "Registri & firewall");
    public string ValueData => T("Data", "Data");
    public string Modified => T("Modified", "Diubah");
    public string CheckVisible => T("Select shown", "Pilih yang tampil");
    public string ClearVisible => T("Clear shown", "Kosongkan yang tampil");
    public string Permanent => T("Delete permanently instead of the Recycle Bin", "Hapus permanen, bukan ke Recycle Bin");
    public string Score => T("Confidence", "Keyakinan");
    public string Reasons => T("Why Niskala thinks it belongs to the app", "Mengapa Niskala menilai ini milik aplikasi");
    public string SelectLeftover => T("Select an item to see why it was found.", "Pilih item untuk melihat alasannya.");
    public string NothingFound => T("Nothing left behind.", "Tidak ada yang tertinggal.");

    public string TrackerTitle => NavTracker;
    public string TrackerSubtitle => T("Record what an installer changes, then undo exactly that.", "Rekam yang diubah installer, lalu batalkan tepat itu saja.");
    public string LabelInstaller => T("Installer (.exe or .msi)", "Installer (.exe atau .msi)");
    public string Browse => T("Browse…", "Telusuri…");
    public string LabelArguments => T("Arguments", "Argumen");
    public string LabelName => T("Name in the log", "Nama di log");
    public string Record => T("Start recording", "Mulai merekam");
    public string RecordHint => T("The installer runs in a job under a kernel trace that follows every child process, Windows Installer included.", "Installer berjalan dalam job di bawah jejak kernel yang mengikuti setiap proses anak, termasuk Windows Installer.");
    public string FinishNow => T("Finish now", "Selesai sekarang");
    public string FinishNowHint => T("For installers that leave their app running.", "Untuk installer yang membiarkan aplikasinya berjalan.");
    public string Abandon => T("Abandon", "Batalkan");
    public string LogsTitle => T("Install logs", "Log instalasi");
    public string NoLogs => T("No install has been recorded yet.", "Belum ada instalasi yang direkam.");
    public string RolledBack => T("rolled back", "dibatalkan");
    public string Rollback => T("Roll back…", "Rollback…");
    public string RollbackNow => T("Roll back selected", "Rollback yang dipilih");
    public string DeleteLog => T("Delete log", "Hapus log");
    public string DeleteLogConfirm => T("Delete this log? The changes it recorded stay on the machine.", "Hapus log ini? Perubahan yang direkam tetap ada di mesin.");
    public string Delete => T("Delete", "Hapus");
    public string Changes => T("Recorded changes", "Perubahan terekam");
    public string KeptTitle => T("Not undone, and why", "Tidak dibatalkan, dan alasannya");
    public string TrackerStarting => T("Starting the trace…", "Memulai jejak…");
    public string RecordingAbandoned => T("Recording abandoned; no log was saved. The installer keeps running.", "Perekaman dibatalkan; tidak ada log yang disimpan. Installer tetap berjalan.");
    public string RevertOnly => T("written back only", "hanya ditulis balik");

    public string PickerTitle => NavPicker;
    public string PickerSubtitle => T("Point at a window, a desktop icon or a tray icon to find the app behind it, or choose its file.", "Arahkan ke jendela, ikon desktop, atau ikon tray untuk menemukan aplikasi di baliknya, atau pilih berkasnya.");
    public string PickerStart => T("Start aiming", "Mulai membidik");
    public string PickerInstructions => T("Niskala steps aside and dims the screen. Move the pointer over a window, a desktop icon or a tray icon, click to pick it, press Esc to stop. Hidden tray icons: click the taskbar's arrow first.", "Niskala menyingkir dan meredupkan layar. Gerakkan penunjuk ke jendela, ikon desktop, atau ikon tray, klik untuk memilih, tekan Esc untuk berhenti. Ikon tray tersembunyi: klik dulu panah di taskbar.");
    public string PickerPointAt => T("Point at a window, a desktop icon or a tray icon", "Arahkan ke jendela, ikon desktop, atau ikon tray");
    public string PickerBrowse => T("Browse File / Shortcut…", "Pilih Berkas / Pintasan…");
    public string PickTargetTitle => T("Choose a program or a shortcut", "Pilih program atau pintasan");
    public string ProgramsAndShortcuts => T("Apps and shortcuts (*.exe;*.lnk;*.url)", "Aplikasi & Pintasan (*.exe;*.lnk;*.url)");
    public string PickerActions => T("Quick actions", "Aksi cepat");
    public string PickerStopProcesses => T("Stop process", "Matikan Proses");
    public string PickerDisableStartup => T("Disable from startup", "Nonaktifkan dari Startup");
    public string PickerNoProcess => T("Not running.", "Tidak sedang berjalan.");
    public string PickerNoStartup => T("Does not start at sign-in.", "Tidak berjalan saat masuk.");
    public string PickerStopConfirm => T("Stop these processes? Unsaved work in them is lost.", "Matikan proses ini? Pekerjaan yang belum disimpan akan hilang.");
    public string LabelRunning => T("Running", "Berjalan");
    public string LabelStartup => T("Starts at sign-in", "Berjalan saat masuk");
    public string LabelItem => T("File", "Berkas");
    public string PickerHint => T("Click to pick · Esc to stop", "Klik untuk memilih · Esc untuk berhenti");
    public string PickerResult => T("Last pick", "Pilihan terakhir");
    public string PickerNone => T("Nothing picked yet.", "Belum ada yang dipilih.");
    public string NoInventoryMatch => T("Not linked to an installed app", "Tidak terhubung ke aplikasi terpasang");
    public string ShowInApps => T("Show in Apps", "Lihat di Aplikasi");
    public string OpenLocation => T("Open file location", "Buka lokasi berkas");
    public string LabelProcess => T("Process", "Proses");
    public string LabelImage => T("Program", "Program");
    public string LabelApp => T("App", "Aplikasi");
    public string LabelMatch => T("Matched by", "Dicocokkan lewat");

    public string StartupTitle => T("Startup programs", "Program startup");
    public string Remove => T("Remove…", "Hapus…");
    public string RemoveStartupConfirm => T("Remove this entry? It goes into the journal and History can put it back.", "Hapus entri ini? Masuk ke jurnal dan Riwayat bisa mengembalikannya.");

    public string RepairTitle => NavRepair;
    public string RepairSubtitle => T("Windows' own repair tools from a fixed list; none of them deletes user data or removes update uninstall points.", "Alat perbaikan bawaan Windows dari daftar tetap; tidak ada yang menghapus data pengguna atau titik uninstall pembaruan.");
    public string RepairRun => T("Run", "Jalankan");
    public string RepairIdle => T("Pick a repair and run it; its output appears here as it happens.", "Pilih perbaikan lalu jalankan; keluarannya muncul di sini secara langsung.");
    public string RepairChangesSystem => T("Changes Windows", "Mengubah Windows");
    public string RepairNeedsRestart => T("Restart afterwards", "Perlu mulai ulang");
    public string RepairLongRunning => T("Can take a long time", "Bisa lama");
    public string RepairReadOnly => T("Reads only", "Hanya membaca");

    public string HistoryTitle => NavHistory;
    public string HistorySubtitle => T("Every change Niskala made, journaled before it happened.", "Setiap perubahan Niskala, dijurnal sebelum terjadi.");
    public string NoTransactions => T("Nothing has been changed yet.", "Belum ada yang diubah.");
    public string Restore => T("Restore", "Pulihkan");
    public string RestoreHint => T("Puts back registry backups, Recycle Bin items, empty folders, firewall rules and scheduled tasks. Nothing that exists now is overwritten.", "Mengembalikan cadangan registri, item Recycle Bin, folder kosong, aturan firewall, dan tugas terjadwal. Tidak ada yang ditimpa.");
    public string Restored => T("restored", "dipulihkan");
    public string Items => T("Items", "Item");
    public string LabelRestorePoint => T("Restore point", "Titik pemulihan");
    public string LabelMethod => T("Method", "Metode");
    public string MethodPermanent => T("Deleted permanently", "Dihapus permanen");
    public string MethodRecycleBin => T("Recycle Bin", "Recycle Bin");
    public string NoRestorePoint => T("None", "Tidak ada");
    public string TransactionMissing => T("The transaction is gone from the journal.", "Transaksi sudah tidak ada di jurnal.");

    public string PaletteOpenApp => T("Open in the inspector", "Buka di inspector");
    public string PaletteForceAppHint => T("Remove without its uninstaller", "Hapus tanpa uninstaller-nya");
    public string PalettePickerHint => T("Pick a window or desktop icon", "Pilih jendela atau ikon desktop");
    public string PaletteForceFolder => T("Force Remove a folder…", "Copot Paksa sebuah folder…");
    public string PaletteForceFolderHint => T("For apps that are not in the list", "Untuk aplikasi yang tidak ada di daftar");
    public string PaletteRestore => T("Restore a backup", "Pulihkan cadangan");
    public string PaletteRestoreHint => T("Open the newest transaction that can be restored", "Buka transaksi terbaru yang bisa dipulihkan");
    public string PaletteRecord => T("Record an install…", "Rekam instalasi…");
    public string PaletteRecordHint => T("Choose an installer for the install tracker", "Pilih installer untuk pemantau instalasi");
    public string PaletteStartup => StartupTitle;
    public string PaletteStartupHint => T("Switch programs that start at sign-in", "Atur program yang berjalan saat masuk");
    public string PaletteRepair => NavRepair;
    public string PaletteRepairHint => T("DISM, SFC, network resets and an online disk scan", "DISM, SFC, setel ulang jaringan, dan pindai disk online");
    public string PaletteTheme => T("Switch theme", "Ganti tema");
    public string PaletteThemeHint => T("Dark slate or light", "Dark slate atau terang");
    public string PaletteLanguage => T("Switch language", "Ganti bahasa");
    public string PaletteLanguageHint => T("English or Bahasa Indonesia", "Bahasa Indonesia atau English");

    public static void Use(bool indonesian) => Current = new Strings(indonesian);

    public static Strings For(bool indonesian) => new(indonesian);

    public string AppsSummary(int count, string size) => T($"{count} apps · {size}", $"{count} aplikasi · {size}");

    public string OpenFailed(int error) => T($"Explorer could not open it (Win32 error {error}).", $"Explorer tidak dapat membukanya (galat Win32 {error}).");

    public string FolderRefused(string folder, PathVerdict verdict) => T($"{folder} may not be removed ({verdict}).", $"{folder} tidak boleh dihapus ({verdict}).");

    public string Installer(InstallerKind kind) => kind switch
    {
        InstallerKind.Msi => "Windows Installer",
        InstallerKind.InnoSetup => "Inno Setup",
        InstallerKind.Nsis => "NSIS",
        InstallerKind.InstallShield => "InstallShield",
        InstallerKind.WixBurn => "WiX Burn",
        InstallerKind.Squirrel => "Squirrel",
        InstallerKind.WingetPortable => "winget portable",
        InstallerKind.Msix => "MSIX",
        InstallerKind.CustomExe => T("Custom installer", "Installer khusus"),
        InstallerKind.Steam => "Steam",
        _ => T("Unknown installer", "Installer tidak dikenal"),
    };

    public string FingerprintDetail(int files, int traces) => T($"{files} files and {traces} registrations recorded.", $"{files} berkas dan {traces} registrasi direkam.");

    public string InstallLogFound(string id) => T($"An install log exists ({id}); leftovers come from it exactly.", $"Ada log instalasi ({id}); sisa diambil tepat darinya.");

    public string RestorePointCreated(string description) => T($"Created: {description}", $"Dibuat: {description}");

    public string Outcome(UninstallOutcome outcome) => outcome switch
    {
        UninstallOutcome.Removed => T("Removed.", "Terhapus."),
        UninstallOutcome.PendingReboot => T("Removed; Windows finishes at the next restart.", "Terhapus; Windows menuntaskannya saat mulai ulang."),
        UninstallOutcome.Cancelled => T("The uninstaller was cancelled.", "Uninstaller dibatalkan."),
        UninstallOutcome.Failed => T("The uninstaller failed.", "Uninstaller gagal."),
        UninstallOutcome.StaleEntry => T("Windows Installer had already removed it; its entry was stale.", "Windows Installer sudah menghapusnya; entrinya basi."),
        UninstallOutcome.StillInstalled => T("The uninstaller ended, but the app is still installed.", "Uninstaller selesai, tetapi aplikasi masih terpasang."),
        UninstallOutcome.Unverified => T("The uninstaller reported success; nothing could confirm it.", "Uninstaller melaporkan berhasil; tidak ada yang bisa memastikannya."),
        UninstallOutcome.Blocked => T("Blocked by the checks.", "Diblokir oleh pemeriksaan."),
        _ => T("The uninstaller could not be started.", "Uninstaller tidak dapat dijalankan."),
    };

    public string ForcePlanDetail(int leftovers, int processes, int services) =>
        T($"{leftovers} items found; {processes} processes and {services} services to stop.", $"{leftovers} item ditemukan; {processes} proses dan {services} layanan akan dihentikan.");

    public string CleanSummary(int removed, int atReboot, int skipped, int failed, int reverted)
    {
        var parts = new List<string> { T($"{removed} removed", $"{removed} dihapus") };
        if (reverted > 0)
        {
            parts.Add(T($"{reverted} written back", $"{reverted} ditulis balik"));
        }

        if (atReboot > 0)
        {
            parts.Add(T($"{atReboot} at the next restart", $"{atReboot} saat mulai ulang"));
        }

        if (skipped > 0)
        {
            parts.Add(T($"{skipped} skipped", $"{skipped} dilewati"));
        }

        if (failed > 0)
        {
            parts.Add(T($"{failed} failed", $"{failed} gagal"));
        }

        return string.Join(" · ", parts) + ".";
    }

    public string EmptyFoldersRemoved(int count) => T($"{count} empty folders removed.", $"{count} folder kosong dihapus.");

    public string FromInstallLog(int items, int reverts) => T($"{items} items from the install log, {reverts} values to write back.", $"{items} item dari log instalasi, {reverts} nilai akan ditulis balik.");

    public string LeftoversFound(int items, int excluded) => T($"{items} found; {excluded} protected matches left out.", $"{items} ditemukan; {excluded} kecocokan terlindungi disisihkan.");

    public string Stage(CleanStage stage) => stage switch
    {
        CleanStage.RestorePoint => CreatingRestorePoint,
        CleanStage.Backup => BackingUp,
        _ => T("Removing…", "Menghapus…"),
    };

    public string ProcessesStopped(int processes, int services) => T($"{processes} processes and {services} services stopped.", $"{processes} proses dan {services} layanan dihentikan.");

    public string ProcessesRunning(int count) => T($"{count} processes running.", $"{count} proses berjalan.");

    public string Signal(LeftoverSignal signal) => signal switch
    {
        LeftoverSignal.InstallerIdentity => T("Installer identity", "Identitas installer"),
        LeftoverSignal.InFingerprint => T("Recorded before the uninstall", "Direkam sebelum uninstall"),
        LeftoverSignal.PointsToFingerprint => T("Points to the app's files", "Menunjuk ke berkas aplikasi"),
        LeftoverSignal.ExactName => T("Exact product name", "Nama produk persis"),
        LeftoverSignal.PublisherAndProduct => T("Publisher\\Product", "Penerbit\\Produk"),
        LeftoverSignal.NearInstallDate => T("Near the install date", "Dekat tanggal pasang"),
        LeftoverSignal.PartialName => T("Partial name", "Sebagian nama"),
        LeftoverSignal.OtherInstalledApp => T("Also matches another installed app", "Juga cocok dengan aplikasi lain"),
        LeftoverSignal.SteamAppId => T("Steam record of the game", "Catatan Steam untuk game ini"),
        LeftoverSignal.MissingProgram => T("Its program is gone", "Programnya sudah tidak ada"),
        LeftoverSignal.PackageFamily => T("Data of the Store app's package", "Data paket aplikasi Store"),
        LeftoverSignal.PersonalData => T("May hold saves: not selected", "Mungkin berisi save: tidak dicentang"),
        _ => T("Recorded by the install tracker", "Direkam pemantau instalasi"),
    };

    public string Kind(LeftoverKind kind) => kind switch
    {
        LeftoverKind.Folder => T("Folder", "Folder"),
        LeftoverKind.File => T("File", "Berkas"),
        LeftoverKind.RegistryKey => T("Registry key", "Kunci registri"),
        LeftoverKind.RegistryValue => T("Registry value", "Nilai registri"),
        LeftoverKind.ScheduledTask => T("Scheduled task", "Tugas terjadwal"),
        _ => T("Firewall rule", "Aturan firewall"),
    };

    public string Tier(ConfidenceTier tier) => tier switch
    {
        ConfidenceTier.Safe => TierSafe,
        ConfidenceTier.Review => TierReview,
        _ => TierLow,
    };

    public string FilesAndBytes(int files, string bytes) => T($"{files} files · {bytes}", $"{files} berkas · {bytes}");

    public string SelectedItems(int items, int leftovers, int total, string? bytes) =>
        T($"{items} items from {leftovers} of {total} leftovers", $"{items} item dari {leftovers} dari {total} sisa") + (bytes is null ? string.Empty : $" · {bytes}");

    public string TreeTruncated(int shown) => T($"Only the first {shown:N0} entries are listed.", $"Hanya {shown:N0} entri pertama yang ditampilkan.");

    public string TreeUnreadable(string error) => T($"Could not be read: {error}", $"Tidak dapat dibaca: {error}");

    public string SteamNoPlan(string problem) => T($"Cannot remove it directly: {problem}.", $"Tidak dapat dihapus langsung: {problem}.");

    public string SteamFound(string library, string size) => T($"In {library}, {size}.", $"Di {library}, {size}.");

    public string SteamFilesGone => T("Its files are gone; Steam still keeps records of it.", "Berkasnya sudah tidak ada; Steam masih menyimpan catatannya.");

    public string SteamNotStarted(int error) => T($"steam.exe could not be started (Win32 error {error}).", $"steam.exe tidak dapat dijalankan (galat Win32 {error}).");

    public string ProcessStarted(string name, int id) => T($"Started {name} (PID {id})", $"Dimulai {name} (PID {id})");

    public string ProcessEnded(string name, int id, bool abnormally) =>
        abnormally ? T($"{name} (PID {id}) ended abnormally", $"{name} (PID {id}) berhenti tidak normal") : T($"Ended {name} (PID {id})", $"Selesai {name} (PID {id})");

    public string ProcessToStop(string name, int id) => T($"Running and to be stopped: {name} (PID {id})", $"Berjalan dan akan dihentikan: {name} (PID {id})");

    public string ProcessStopped(string name, int id, string outcome) => T($"{name} (PID {id}): {outcome}", $"{name} (PID {id}): {outcome}");

    public string ProcessMetric(int running, int seen) => T($"{running} of {seen} processes running", $"{running} dari {seen} proses berjalan");

    public string LeftoverMetric(int found) => T($"{found} leftovers found", $"{found} sisa ditemukan");

    public string EmptyFolderLeft(string folder) => T($"Now empty: {folder}", $"Kini kosong: {folder}");

    public string CheckedSummary(int chosen, int total, string? bytes) =>
        T($"{chosen} of {total} selected", $"{chosen} dari {total} dipilih") + (bytes is null ? string.Empty : $" · {bytes}");

    public string LogCounts(int files, int keys, int values) => T($"{files} files · {keys} keys · {values} values", $"{files} berkas · {keys} kunci · {values} nilai");

    public string LogSummary(string installer, int processes, long events, int filtered) =>
        T($"{Path.GetFileName(installer)} · {processes} processes · {events} events seen · {filtered} noise entries left out",
          $"{Path.GetFileName(installer)} · {processes} proses · {events} event terlihat · {filtered} entri noise disisihkan");

    public string Change(ChangeKind change) => change switch
    {
        ChangeKind.Created => T("created", "dibuat"),
        ChangeKind.Modified => T("changed", "diubah"),
        _ => T("deleted", "dihapus"),
    };

    public string LogSaved(int files, int keys, int values) => T($"Log saved: {files} files, {keys} keys, {values} values.", $"Log disimpan: {files} berkas, {keys} kunci, {values} nilai.");

    public string WillWriteBack(string key, string name) => T($"{key} → \"{name}\": earlier data written back", $"{key} → \"{name}\": data sebelumnya ditulis balik");

    public string Recording(TrackStage stage) => stage switch
    {
        Niskala.Tracker.TrackStage.Running => T("Recording the installer…", "Merekam installer…"),
        Niskala.Tracker.TrackStage.Settling => T("The installer finished; letting late writes land…", "Installer selesai; menunggu penulisan terakhir…"),
        _ => T("Comparing with the machine now…", "Membandingkan dengan keadaan mesin…"),
    };

    public string BatchRemove(int count) => T($"Batch Remove ({count})", $"Copot Massal ({count})");

    public string BatchTitle(int count) => T($"Batch Remove · {count} apps", $"Copot Massal · {count} aplikasi");

    public string BatchStep(string name) => T($"Uninstall {name}", $"Copot {name}");

    public string BatchStartTitle(int count) => T($"Uninstall {count} apps one after another?", $"Copot {count} aplikasi berurutan?");

    public string BatchSteamDirect => T("removed directly, without Steam", "dihapus langsung, tanpa Steam");

    public string BatchBlocked(Niskala.Uninstall.Batch.BatchBlock block, string? detail) => block switch
    {
        Niskala.Uninstall.Batch.BatchBlock.NoCommand => NoUninstallCommand,
        Niskala.Uninstall.Batch.BatchBlock.SteamRunning => T("Steam is running; close it first, or remove this game on its own.", "Steam sedang berjalan; tutup dulu, atau copot game ini sendiri."),
        Niskala.Uninstall.Batch.BatchBlock.NoSteamPlan => SteamNoPlan(detail ?? string.Empty),
        _ => detail ?? Blocked,
    };

    public string ClearSelection => T("Clear selection", "Hapus pilihan");

    public string PickerStopped(int stopped, int total) => T($"{stopped} of {total} processes stopped.", $"{stopped} dari {total} proses dimatikan.");

    public string PickerStartupDisabled(int count) => T($"{count} startup entries switched off.", $"{count} entri startup dimatikan.");

    public string StartupFailed(string name, int error) => T($"{name}: Win32 error {error}.", $"{name}: galat Win32 {error}.");

    public string TargetKind(PickerTargetKind kind) => kind switch
    {
        PickerTargetKind.TrayIcon => T("tray icon", "ikon tray"),
        PickerTargetKind.DesktopIcon => T("desktop icon", "ikon desktop"),
        PickerTargetKind.File => T("file", "berkas"),
        _ => T("window", "jendela"),
    };

    public string Match(PickerMatch match) => match switch
    {
        PickerMatch.SteamAppId => T("its Steam shortcut", "shortcut Steam-nya"),
        PickerMatch.Package => T("its package", "paketnya"),
        PickerMatch.InstallFolder => T("its install folder", "folder instalasinya"),
        PickerMatch.DisplayIcon => T("its icon file", "berkas ikonnya"),
        PickerMatch.ShortcutProduct => T("its Windows Installer shortcut", "shortcut Windows Installer-nya"),
        _ => T("nothing", "tidak ada"),
    };

    public string StartupRemoved(string name) => T($"{name} removed. History can put it back.", $"{name} dihapus. Riwayat bisa mengembalikannya.");

    public string StartupNotRemoved(string name, string? detail) => T($"{name} was not removed: {detail}", $"{name} tidak dihapus: {detail}");

    public string KindOf(TransactionKind kind) => kind switch
    {
        Niskala.Guardian.Journal.TransactionKind.Uninstall => T("Uninstall checkpoint", "Checkpoint uninstall"),
        Niskala.Guardian.Journal.TransactionKind.Clean => T("Leftover cleanup", "Pembersihan sisa"),
        Niskala.Guardian.Journal.TransactionKind.Force => T("Force Remove", "Copot Paksa"),
        Niskala.Guardian.Journal.TransactionKind.SystemClean => T("System cleanup", "Pembersihan sistem"),
        Niskala.Guardian.Journal.TransactionKind.RegistryIntegrity => NavRegistry,
        Niskala.Guardian.Journal.TransactionKind.ResourceControl => NavResources,
        Niskala.Guardian.Journal.TransactionKind.DuplicateClean => NavDuplicates,
        _ => T("Install rollback", "Rollback instalasi"),
    };

    public string TransactionCounts(int done, int total) => T($"{done} of {total} items", $"{done} dari {total} item");

    public string Status(RemovalStatus status) => status switch
    {
        RemovalStatus.Pending => T("Pending", "Tertunda"),
        RemovalStatus.Removed => T("Removed", "Dihapus"),
        RemovalStatus.ScheduledAtReboot => T("At next restart", "Saat mulai ulang"),
        RemovalStatus.Skipped => T("Skipped", "Dilewati"),
        RemovalStatus.Failed => T("Failed", "Gagal"),
        RemovalStatus.Restored => T("Restored", "Dipulihkan"),
        RemovalStatus.Compacted => T("Compacted", "Dipadatkan"),
        _ => T("Written back", "Ditulis balik"),
    };

    public string RestoreSummary(int items, int written, int kept, int rules) =>
        T($"{items} items back, {written} registry values written, {kept} kept as they are now, {rules} firewall rules back.",
          $"{items} item kembali, {written} nilai registri ditulis, {kept} dibiarkan seperti sekarang, {rules} aturan firewall kembali.");

    public string RepairName(RepairCommandId id) => id switch
    {
        RepairCommandId.DismCheckHealth => T("Check the component store", "Periksa penyimpanan komponen"),
        RepairCommandId.DismRestoreHealth => T("Repair the component store", "Perbaiki penyimpanan komponen"),
        RepairCommandId.SfcScanNow => T("Scan system files", "Pindai berkas sistem"),
        RepairCommandId.DismComponentCleanup => T("Clean up superseded components", "Bersihkan komponen usang"),
        RepairCommandId.NetshWinsockReset => T("Reset Winsock", "Setel ulang Winsock"),
        RepairCommandId.NetshIpReset => T("Reset TCP/IP", "Setel ulang TCP/IP"),
        RepairCommandId.FlushDns => T("Flush the DNS cache", "Kosongkan cache DNS"),
        _ => T("Scan the system drive", "Pindai drive sistem"),
    };

    public string RepairSummary(RepairCommandId id) => id switch
    {
        RepairCommandId.DismCheckHealth => T("Reports whether the component store is marked corrupt; changes nothing.", "Melaporkan apakah penyimpanan komponen ditandai rusak; tidak mengubah apa pun."),
        RepairCommandId.DismRestoreHealth => T("Repairs the component store from Windows Update.", "Memperbaiki penyimpanan komponen dari Windows Update."),
        RepairCommandId.SfcScanNow => T("Checks protected system files and replaces damaged ones from the component store.", "Memeriksa berkas sistem yang dilindungi dan mengganti yang rusak dari penyimpanan komponen."),
        RepairCommandId.DismComponentCleanup => T("Removes superseded components; installed updates can still be uninstalled.", "Menghapus komponen yang sudah digantikan; pembaruan terpasang tetap bisa di-uninstall."),
        RepairCommandId.NetshWinsockReset => T("Resets the Winsock catalog to its default.", "Mengembalikan katalog Winsock ke bawaan."),
        RepairCommandId.NetshIpReset => T("Resets the TCP/IP stack settings to their default.", "Mengembalikan pengaturan TCP/IP ke bawaan."),
        RepairCommandId.FlushDns => T("Empties the DNS resolver cache.", "Mengosongkan cache resolver DNS."),
        _ => T("Scans the system volume while it stays online; fixes nothing offline.", "Memindai volume sistem selagi tetap online; tidak memperbaiki apa pun secara offline."),
    };

    public string RepairConfirm(string name, string commandLine, bool restart) =>
        restart
            ? T($"Run {name} ({commandLine})? Windows needs a restart afterwards.", $"Jalankan {name} ({commandLine})? Windows perlu dimulai ulang setelahnya.")
            : T($"Run {name} ({commandLine})? It changes Windows files or settings.", $"Jalankan {name} ({commandLine})? Ini mengubah berkas atau pengaturan Windows.");

    public string RepairStatus(RepairResult result, string elapsed)
    {
        ArgumentNullException.ThrowIfNull(result);
        string? diagnostic = RepairDiagnosticText(result.Diagnostic);
        return result.Outcome switch
        {
            RepairOutcome.Succeeded => T($"Finished · {elapsed}", $"Selesai · {elapsed}"),
            RepairOutcome.RestartRequired => T($"Finished · {elapsed}. Restart Windows to complete it.", $"Selesai · {elapsed}. Mulai ulang Windows untuk menuntaskannya."),
            RepairOutcome.IssuesFound => Join(T($"Finished with problems found · {elapsed}.", $"Selesai dengan masalah ditemukan · {elapsed}."), diagnostic),
            RepairOutcome.Cancelled => T($"Cancelled · {elapsed}. Its processes were stopped.", $"Dibatalkan · {elapsed}. Prosesnya sudah dihentikan."),
            RepairOutcome.NotElevated => Join(T("Needs administrator rights, so nothing was started.", "Membutuhkan hak administrator, jadi tidak ada yang dijalankan."), diagnostic),
            RepairOutcome.StartFailed => diagnostic is null
                ? T($"Could not start the program (Windows error {result.StartError}).", $"Program tidak bisa dijalankan (galat Windows {result.StartError}).")
                : T($"Could not start. {diagnostic}", $"Tidak bisa dijalankan. {diagnostic}"),
            _ => Join(T($"Failed · {elapsed}.", $"Gagal · {elapsed}."), diagnostic ?? RepairFailureCode(result.ExitCode)),
        };
    }

    private string? RepairFailureCode(int? code)
    {
        if (code is not { } value)
        {
            return null;
        }

        string shown = value < 0 ? string.Create(CultureInfo.InvariantCulture, $"0x{value:X8}") : value.ToString(CultureInfo.InvariantCulture);
        return T($"The command stopped with error code {shown}.", $"Perintah berhenti dengan kode galat {shown}.");
    }

    private static string Join(string text, string? detail) => detail is null ? text : $"{text} {detail}";

    private string? RepairDiagnosticText(RepairDiagnostic diagnostic) => diagnostic switch
    {
        RepairDiagnostic.ElevationRequired => T("Run Niskala as administrator.", "Jalankan Niskala sebagai administrator."),
        RepairDiagnostic.AccessDenied => T("Windows denied access to a setting the tool resets.", "Windows menolak akses ke pengaturan yang disetel ulang."),
        RepairDiagnostic.InvalidParameter => T("This Windows version does not know one of the options.", "Versi Windows ini tidak mengenal salah satu opsinya."),
        RepairDiagnostic.SourceFilesMissing => T("DISM found no repair source; check that Windows Update is reachable.", "DISM tidak menemukan sumber perbaikan; pastikan Windows Update bisa dijangkau."),
        RepairDiagnostic.SourceDownloadFailed => T("DISM could not download the repair files from Windows Update.", "DISM tidak bisa mengunduh berkas perbaikan dari Windows Update."),
        RepairDiagnostic.BlockedByPolicy => T("A Group Policy keeps DISM from using Windows Update as a repair source.", "Group Policy mencegah DISM memakai Windows Update sebagai sumber perbaikan."),
        RepairDiagnostic.PendingOperations => T("Windows has servicing operations pending; restart and run it again.", "Windows punya operasi servis tertunda; mulai ulang lalu jalankan lagi."),
        RepairDiagnostic.OfflineRepairNeeded => T("The drive needs an offline repair; Windows offers it in the drive's properties under Tools.", "Drive perlu perbaikan offline; Windows menyediakannya di properti drive, tab Tools."),
        RepairDiagnostic.ProgramMissing => T("The program is missing from System32.", "Programnya tidak ada di System32."),
        _ => null,
    };

    public string PaletteForceApp(string name) => T($"Force Remove {name}", $"Copot Paksa {name}");

    private string T(string english, string indonesian) => Indonesian ? indonesian : english;
}
