using Niskala.Cleaner.Execution;
using Niskala.Cleaner.Registry;
using Niskala.Cleaner.Rules;
using Niskala.Cleaner.Scanning;
using Niskala.Core.Models;
using Niskala.Interop.Services;
using Niskala.Optimizer.Processes;
using Niskala.Optimizer.Resources;
using Niskala.Toolkit.Duplicates;
using Niskala.Toolkit.Wipe;
using Niskala.UI.ViewModels;

namespace Niskala.UI.Services;

public sealed partial class Strings
{
    public string NavGroupApps => T("Apps", "Aplikasi");
    public string NavGroupClean => T("Cleaning", "Pembersihan");
    public string NavGroupPerformance => T("Performance", "Kinerja");
    public string NavGroupTools => T("Maintenance", "Pemeliharaan");
    public string NavQuickScan => T("Quick Scan", "Pindai Cepat");
    public string NavSelectiveClean => T("Selective Clean", "Pembersih Selektif");
    public string NavRegistry => T("Registry Integrity", "Integritas Registri");
    public string NavSuspend => T("Process Suspend", "Jeda Proses");
    public string NavStorage => T("Storage Map", "Peta Penyimpanan");
    public string NavDuplicates => T("Duplicate Scan", "Pindai Duplikat");
    public string NavWipe => T("Secure Wipe", "Timpa Permanen");
    public string NavTogglePane => T("Collapse or expand the menu", "Ciutkan atau bentangkan menu");

    public string NavTitle(Section section) => section switch
    {
        Section.Tracker => NavTracker,
        Section.Picker => NavPicker,
        Section.QuickScan => NavQuickScan,
        Section.SelectiveClean => NavSelectiveClean,
        Section.Registry => NavRegistry,
        Section.Resources => NavResources,
        Section.Suspend => NavSuspend,
        Section.Storage => NavStorage,
        Section.Duplicates => NavDuplicates,
        Section.Wipe => NavWipe,
        Section.Repair => NavRepair,
        Section.History => NavHistory,
        Section.Updates => NavUpdates,
        _ => NavApps,
    };

    public string Scan => T("Scan", "Pindai");
    public string CleanNow => T("Clean", "Bersihkan");
    public string Cancelled => T("Cancelled; nothing after that point was changed.", "Dibatalkan; tidak ada yang diubah setelah titik itu.");
    public string Reading => T("Reading…", "Membaca…");
    public string Removing => T("Removing…", "Menghapus…");
    public string Repairing => T("Repairing…", "Memperbaiki…");
    public string MakingRestorePoint => T("Making a System Restore point…", "Membuat titik System Restore…");
    public string MeasuringCpu => T("Measuring CPU use…", "Mengukur pemakaian CPU…");
    public string ScanFirst => T("Scan first, then choose what to change.", "Pindai dulu, lalu pilih yang akan diubah.");
    public string NotScanned => T("Not scanned yet", "Belum dipindai");
    public string LeftAlone => T("protected, left alone", "dilindungi, dibiarkan");
    public string RestorePointOption => T("Make a restore point first", "Buat titik pemulihan dulu");
    public string PermanentOption => T("Delete traces permanently (no Recycle Bin)", "Hapus jejak permanen (tanpa Recycle Bin)");
    public string SelectQuick => T("Quick Scan set", "Set Pindai Cepat");
    public string SelectAll => T("All", "Semua");
    public string SelectNone => T("None", "Kosongkan");
    public string PickWipeTitle => T("Choose a file to wipe", "Pilih berkas yang akan ditimpa");
    public string PickScanFolderTitle => T("Choose a folder", "Pilih folder");
    public string AllFiles => T("All files", "Semua berkas");

    public string QuickScanSubtitle => T(
        "One pass over system junk, browser caches and heavy startup items. Cleaning goes through Guardian: a restore point first, then a journal of every rule.",
        "Satu kali pindai untuk sampah sistem, cache peramban, dan item startup yang berat. Pembersihan lewat Guardian: titik pemulihan dulu, lalu jurnal tiap aturan.");
    public string SystemJunk => T("System junk", "Sampah sistem");
    public string BrowserCaches => T("Browser caches", "Cache peramban");
    public string HeavyStartup => T("Heavy startup items", "Item startup berat");
    public string SwitchOff => T("Switch off", "Matikan");
    public string CleanConfirm => T(
        "Clean the chosen items? Caches are deleted, personal traces go to the Recycle Bin, and the journal records every rule.",
        "Bersihkan item yang dipilih? Cache dihapus, jejak pribadi masuk Recycle Bin, dan jurnal mencatat setiap aturan.");
    public string CleanPermanentConfirm => T(
        "Clean the chosen items permanently? Nothing goes to the Recycle Bin, so these files cannot be restored.",
        "Bersihkan item yang dipilih secara permanen? Tidak ada yang masuk Recycle Bin, jadi berkas ini tidak bisa dipulihkan.");
    public string RepairRegistryConfirm => T(
        "Repair the chosen entries? A .reg backup and the journal are written first; History can put them back.",
        "Perbaiki entri yang dipilih? Cadangan .reg dan jurnal ditulis lebih dulu; Riwayat bisa mengembalikannya.");
    public string QuickScanIdle => T("Run a scan to see what can go.", "Jalankan pemindaian untuk melihat yang bisa dibersihkan.");

    public string SelectiveSubtitle => T(
        "Every rule from the rules folder, with real byte counts. Personal traces go to the Recycle Bin unless you choose otherwise.",
        "Setiap aturan dari folder rules, dengan jumlah byte sebenarnya. Jejak pribadi masuk Recycle Bin kecuali Anda memilih lain.");

    public string RegistrySubtitle => T(
        "File types, shared files, COM classes, firewall rules and services that point at programs that are gone. Repairs are backed up to a .reg file and the journal first.",
        "Tipe berkas, berkas bersama, kelas COM, aturan firewall, dan layanan yang menunjuk program yang sudah tidak ada. Perbaikan dicadangkan ke berkas .reg dan jurnal lebih dulu.");
    public string RepairSelected => T("Repair selected", "Perbaiki yang dipilih");
    public string RegistryIdle => T("Run a scan to check the registry.", "Jalankan pemindaian untuk memeriksa registri.");

    public string ResourcesSubtitle => T(
        "Startup entries, third-party services, scheduled tasks and Explorer menu handlers. Every switch is journaled; History puts the old state back.",
        "Entri startup, layanan pihak ketiga, tugas terjadwal, dan handler menu Explorer. Setiap perubahan dijurnal; Riwayat mengembalikan keadaan lama.");
    public string TabStartup => T("Startup", "Startup");
    public string TabServices => T("Services", "Layanan");
    public string TabTasks => T("Scheduled tasks", "Tugas terjadwal");
    public string TabContextMenu => T("Context menu", "Menu konteks");
    public string ExplorerRestartNeeded => T("Explorer picks this up after it restarts or you sign in again.", "Explorer menerapkannya setelah dimulai ulang atau Anda masuk lagi.");
    public string NothingHere => T("Nothing to show here.", "Tidak ada yang ditampilkan.");

    public string SuspendSubtitle => T(
        "Freeze idle background programs, put them in efficiency mode or cap their CPU. Windows components, other sessions, critical processes and Niskala itself are never touched.",
        "Bekukan program latar belakang yang menganggur, masukkan ke mode efisiensi, atau batasi CPU-nya. Komponen Windows, sesi lain, proses kritis, dan Niskala sendiri tidak pernah disentuh.");
    public string IdleOnly => T("Idle background only", "Hanya latar belakang yang menganggur");
    public string SuspendAction => T("Suspend", "Jeda");
    public string EfficiencyAction => T("Efficiency mode", "Mode efisiensi");
    public string CapAction => T($"Limit CPU to {SuspendViewModel.CapPercent}%", $"Batasi CPU ke {SuspendViewModel.CapPercent}%");
    public string ResumeAction => T("Resume", "Lanjutkan");
    public string ResumeAll => T("Resume all", "Lanjutkan semua");
    public string HasWindow => T("has a window", "punya jendela");
    public string Background => T("background", "latar belakang");
    public string Suspended => T("Suspended", "Dijeda");
    public string EfficiencyMode => T("Efficiency mode", "Mode efisiensi");
    public string CpuCapped => T("CPU limited", "CPU dibatasi");
    public string IdleBackground => T("Idle", "Menganggur");

    public string StorageSubtitle => T(
        "Folder sizes, file counts and the biggest file types. Junctions and links are counted, never followed.",
        "Ukuran folder, jumlah berkas, dan tipe berkas terbesar. Junction dan link dihitung, tidak pernah ditelusuri.");
    public string MapAction => T("Map", "Petakan");
    public string Up => T("Up", "Naik");
    public string Extensions => T("File types", "Tipe berkas");
    public string ShowInExplorer => T("Show in Explorer", "Tampilkan di Explorer");
    public string StorageIdle => T("Choose a folder or drive and map it.", "Pilih folder atau drive lalu petakan.");

    public string DuplicatesSubtitle => T(
        "Same size, then the first and last 4 KB, then a full SHA-256. Protected files are shown but never removed; removed copies go to the Recycle Bin.",
        "Ukuran sama, lalu 4 KB awal dan akhir, lalu SHA-256 penuh. Berkas yang dilindungi ditampilkan tetapi tidak pernah dihapus; salinan yang dihapus masuk Recycle Bin.");
    public string AddFolder => T("Add folder…", "Tambah folder…");
    public string KeepOldest => T("Keep the oldest copy", "Pertahankan salinan terlama");
    public string KeepNewest => T("Keep the newest copy", "Pertahankan salinan terbaru");
    public string RemoveCopies => T("Remove copies", "Hapus salinan");
    public string RemoveCopiesConfirm => T("Move the copies marked Remove to the Recycle Bin? History can put them back.", "Pindahkan salinan bertanda Hapus ke Recycle Bin? Riwayat bisa mengembalikannya.");
    public string Keep => T("Keep", "Simpan");
    public string Protected => T("Protected", "Dilindungi");
    public string RemoveMark => T("Remove", "Hapus");
    public string DuplicatesIdle => T("Add folders and scan them.", "Tambah folder lalu pindai.");

    public string WipeSubtitle => T(
        "Overwrite files or a drive's free space, scramble the names and delete. There is no backup and no undo.",
        "Timpa berkas atau ruang kosong drive, acak namanya, lalu hapus. Tidak ada cadangan dan tidak bisa dibatalkan.");
    public string AddFile => T("Add file…", "Tambah berkas…");
    public string WipeFiles => T("Wipe files", "Timpa berkas");
    public string WipeFreeSpace => T("Wipe free space", "Timpa ruang kosong");
    public string FreeSpaceTitle => T("Free space", "Ruang kosong");
    public string WipeFilesConfirm => T("Overwrite and delete these files for good? This cannot be undone.", "Timpa dan hapus berkas ini selamanya? Ini tidak bisa dibatalkan.");
    public string WipeFreeConfirm => T("Fill the drive's free space with patterns, pass by pass? The drive is full while it runs.", "Isi ruang kosong drive dengan pola, per lintasan? Drive penuh selama proses berjalan.");
    public string SsdNote => T("Solid-state drives may keep old copies in spare blocks that no overwrite reaches.", "SSD bisa menyimpan salinan lama di blok cadangan yang tidak terjangkau penimpaan.");
    public string MethodZero => T("Zero fill (1 pass)", "Isi nol (1 lintasan)");
    public string MethodDod => T("DoD 5220.22-M (3 passes)", "DoD 5220.22-M (3 lintasan)");
    public string MethodGutmann => T("Gutmann (35 passes)", "Gutmann (35 lintasan)");

    public string PaletteQuickScan => NavQuickScan;
    public string PaletteQuickScanHint => T("System junk, browser caches and heavy startup items", "Sampah sistem, cache peramban, dan item startup berat");
    public string PaletteStorage => NavStorage;
    public string PaletteStorageHint => T("See what fills a drive", "Lihat apa yang memenuhi drive");

    public static string CategoryGlyph(CleanCategory category) => category switch
    {
        CleanCategory.BrowserCache or CleanCategory.BrowserHistory or CleanCategory.BrowserCookies or CleanCategory.BrowserSessions or CleanCategory.BrowserForms => "",
        CleanCategory.DatabaseCompaction => "",
        CleanCategory.CrashDumps or CleanCategory.ErrorReports => "",
        CleanCategory.ThumbnailCache => "",
        CleanCategory.DeliveryOptimization => "",
        _ => "",
    };

    public static string IssueGlyph(IntegrityIssueKind kind) => kind switch
    {
        IntegrityIssueKind.FileAssociation => "",
        IntegrityIssueKind.SharedDll => "",
        IntegrityIssueKind.ComRegistration => "",
        IntegrityIssueKind.FirewallRule => "",
        _ => "",
    };

    public static string ResourceGlyph(ResourceKind kind) => kind switch
    {
        ResourceKind.StartupFolder => "",
        ResourceKind.Service => "",
        ResourceKind.ScheduledTask => "",
        ResourceKind.ContextMenu => "",
        _ => "",
    };

    public string RuleTraits(CleanRule rule) => string.Join(" · ", new[]
    {
        rule.Action == RuleAction.Compact ? T("VACUUM after an integrity check", "VACUUM setelah pemeriksaan integritas")
            : rule.Removal == RemovalMode.RecycleBin ? T("to the Recycle Bin", "ke Recycle Bin") : T("deleted", "dihapus"),
        rule.RequiresElevation ? T("needs administrator", "perlu administrator") : null,
        rule.Quick ? NavQuickScan : null,
    }.OfType<string>());

    public string PreviewSummary(RulePreview preview) => preview.State switch
    {
        PreviewState.Ready when preview.Rule.Action == RuleAction.Compact => T($"{preview.Count} databases · {Format.Bytes(preview.Bytes)}", $"{preview.Count} basis data · {Format.Bytes(preview.Bytes)}"),
        PreviewState.Ready => T($"{Format.Number(preview.Count)} files · {Format.Bytes(preview.Bytes)}", $"{Format.Number(preview.Count)} berkas · {Format.Bytes(preview.Bytes)}"),
        PreviewState.NeedsElevation => T("needs administrator", "perlu administrator"),
        PreviewState.Locked => T($"browser open ({string.Join(", ", preview.LockedProfiles)})", $"peramban terbuka ({string.Join(", ", preview.LockedProfiles)})"),
        _ => T("nothing to clean", "tidak ada yang dibersihkan"),
    };

    public string CloseBrowsers(IReadOnlyList<string> browsers) =>
        T($"Close {string.Join(", ", browsers)} and scan again to include their caches.", $"Tutup {string.Join(", ", browsers)} lalu pindai lagi untuk menyertakan cache-nya.");

    public string CleanFinished(CleanRun run) => T(
        $"Freed {Format.Bytes(run.Bytes)} from {Format.Number(run.Files)} files. Transaction {run.Transaction.Id} is in History.",
        $"Membebaskan {Format.Bytes(run.Bytes)} dari {Format.Number(run.Files)} berkas. Transaksi {run.Transaction.Id} ada di Riwayat.");

    public string Cleaning(string rule, int done, int total) => total == 0
        ? T("Cleaning…", "Membersihkan…")
        : T($"Cleaning {rule}: {done} of {total}", $"Membersihkan {rule}: {done} dari {total}");

    public string SwitchFailed(string name, string? detail) => T($"{name} was not changed: {detail}", $"{name} tidak diubah: {detail}");

    public string RulesSummary(int rules, int problems) => problems == 0
        ? T($"{rules} rules", $"{rules} aturan")
        : T($"{rules} rules · {problems} rule problems (see the log)", $"{rules} aturan · {problems} masalah aturan (lihat log)");

    public string SelectionSummary(int ready, long bytes) => T($"{ready} rules ready · {Format.Bytes(bytes)}", $"{ready} aturan siap · {Format.Bytes(bytes)}");

    public string Impact(BootImpact impact) => impact switch
    {
        BootImpact.High => T("High impact", "Dampak tinggi"),
        BootImpact.Medium => T("Medium impact", "Dampak sedang"),
        BootImpact.Low => T("Low impact", "Dampak rendah"),
        BootImpact.None => T("No boot impact", "Tanpa dampak boot"),
        _ => T("Impact unknown", "Dampak tidak diketahui"),
    };

    public string IssueKind(IntegrityIssueKind kind) => kind switch
    {
        IntegrityIssueKind.FileAssociation => T("File type", "Tipe berkas"),
        IntegrityIssueKind.SharedDll => T("Shared file", "Berkas bersama"),
        IntegrityIssueKind.ComRegistration => T("COM class", "Kelas COM"),
        IntegrityIssueKind.FirewallRule => T("Firewall rule", "Aturan firewall"),
        _ => T("Service", "Layanan"),
    };

    public string IntegritySummary(int total, int fixable) => T($"{total} issues · {fixable} repairable", $"{total} masalah · {fixable} bisa diperbaiki");

    public string IntegrityFixed(int fixedCount, int failed, int healed, string transaction) => T(
        $"{fixedCount} entries repaired, {failed} failed, {healed} had healed on their own. The .reg backup is in transaction {transaction}.",
        $"{fixedCount} entri diperbaiki, {failed} gagal, {healed} sudah pulih sendiri. Cadangan .reg ada di transaksi {transaction}.");

    public string ResourceWhere(ResourceItem item) => item.Kind switch
    {
        ResourceKind.RunEntry => item.Scope == InstallScope.User ? T("Run · this user", "Run · pengguna ini") : T("Run · all users", "Run · semua pengguna"),
        ResourceKind.RunOnceEntry => T("Runs once at the next sign-in", "Berjalan sekali saat masuk berikutnya"),
        ResourceKind.StartupFolder => item.Scope == InstallScope.User ? T("Startup folder · this user", "Folder Startup · pengguna ini") : T("Startup folder · all users", "Folder Startup · semua pengguna"),
        ResourceKind.Service => item.StartType switch
        {
            ServiceStartType.Automatic when item.DelayedStart => T("Automatic (delayed)", "Otomatis (tertunda)"),
            ServiceStartType.Automatic => T("Automatic", "Otomatis"),
            ServiceStartType.Manual => T("Manual", "Manual"),
            _ => T("Disabled", "Nonaktif"),
        },
        ResourceKind.ScheduledTask => item.RunsAtSignIn ? T("At sign-in or start-up", "Saat masuk atau mulai") : T("On a schedule", "Sesuai jadwal"),
        _ => item.Location,
    };

    public string LockedBy(string reason) => T($"Protected: {reason}", $"Dilindungi: {reason}");

    public string ResourceSummary(int total, int on, int heavy) =>
        T($"{total} items · {on} on · {heavy} with a medium or high boot impact", $"{total} item · {on} aktif · {heavy} berdampak boot sedang atau tinggi");

    public string ProcessRefusal(ProcessRefusal refusal) => refusal switch
    {
        Optimizer.Processes.ProcessRefusal.SystemProcess => T("system process", "proses sistem"),
        Optimizer.Processes.ProcessRefusal.OtherSession => T("another session or a service", "sesi lain atau layanan"),
        Optimizer.Processes.ProcessRefusal.Critical => T("critical to Windows", "kritis bagi Windows"),
        Optimizer.Processes.ProcessRefusal.CriticalUnknown => T("cannot be inspected", "tidak dapat diperiksa"),
        Optimizer.Processes.ProcessRefusal.WindowsComponent => T("part of Windows", "bagian dari Windows"),
        Optimizer.Processes.ProcessRefusal.Niskala or Optimizer.Processes.ProcessRefusal.NiskalaAncestor => T("Niskala or what started it", "Niskala atau yang menjalankannya"),
        Optimizer.Processes.ProcessRefusal.Exited => T("has exited", "sudah berhenti"),
        _ => T("program unknown", "program tidak diketahui"),
    };

    public string RestraintFailed(string name, RestraintResult result) => result.Outcome == RestraintOutcome.Refused
        ? T($"{name} is off limits: {ProcessRefusal(result.Refusal)}.", $"{name} tidak boleh disentuh: {ProcessRefusal(result.Refusal)}.")
        : T($"{name}: Windows refused (error {result.Error}).", $"{name}: Windows menolak (galat {result.Error}).");

    public string ProcessSummary(int total, int idle, int held) =>
        T($"{total} processes · {idle} idle in the background · {held} restrained", $"{total} proses · {idle} menganggur di latar belakang · {held} ditahan");

    public string Mapping(int folders, int files, string bytes) =>
        T($"Mapping: {Format.Number(folders)} folders, {Format.Number(files)} files, {bytes}", $"Memetakan: {Format.Number(folders)} folder, {Format.Number(files)} berkas, {bytes}");

    public string FileCount(int files) => T($"{Format.Number(files)} files", $"{Format.Number(files)} berkas");

    public string MapSkipped(int inaccessible, int links) =>
        T($"{inaccessible} folders could not be read; {links} links and junctions were not followed.", $"{inaccessible} folder tidak dapat dibaca; {links} link dan junction tidak ditelusuri.");

    public string MapHeading(string path, string size, int files, int folders) =>
        T($"{path} · {size} in {Format.Number(files)} files and {Format.Number(folders)} folders", $"{path} · {size} dalam {Format.Number(files)} berkas dan {Format.Number(folders)} folder");

    public string DuplicateStage(DuplicateStage stage, int done, int total) => stage switch
    {
        Toolkit.Duplicates.DuplicateStage.Listing => T($"Listing files: {Format.Number(total)}", $"Mendaftar berkas: {Format.Number(total)}"),
        Toolkit.Duplicates.DuplicateStage.PartialHash => T($"Comparing first and last 4 KB: {done} of {total}", $"Membandingkan 4 KB awal dan akhir: {done} dari {total}"),
        _ => T($"Full SHA-256: {done} of {total}", $"SHA-256 penuh: {done} dari {total}"),
    };

    public string DuplicateGroupTitle(string size, int copies) => T($"{size} × {copies}", $"{size} × {copies}");

    public string DuplicateSummary(int scanned, int groups, int planned, string bytes) =>
        T($"{Format.Number(scanned)} files · {groups} groups · {planned} copies to remove ({bytes})", $"{Format.Number(scanned)} berkas · {groups} grup · {planned} salinan dihapus ({bytes})");

    public string DuplicatesRemoved(int removed, int changed, string? transaction) => T(
        $"{removed} copies moved to the Recycle Bin; {changed} changed since the scan and stayed.{(transaction is null ? string.Empty : $" Transaction {transaction} is in History.")}",
        $"{removed} salinan dipindah ke Recycle Bin; {changed} berubah sejak pemindaian dan tetap ada.{(transaction is null ? string.Empty : $" Transaksi {transaction} ada di Riwayat.")}");

    public string FilesWiped(int count, WipeMethod method) => T($"{count} files wiped ({Method(method)}).", $"{count} berkas ditimpa ({Method(method)}).");

    public string FreeSpaceWiped(string drive, string bytes, int passes) =>
        T($"Free space of {drive} overwritten: {bytes}, {passes} passes.", $"Ruang kosong {drive} ditimpa: {bytes}, {passes} lintasan.");

    public string WipePass(int pass, int passes, string file, string bytes) =>
        T($"Pass {pass} of {passes} · {file} · {bytes}", $"Lintasan {pass} dari {passes} · {file} · {bytes}");

    public string WipeOutcomeText(WipeOutcome outcome) => outcome switch
    {
        WipeOutcome.Refused => T("refused", "ditolak"),
        WipeOutcome.Missing => T("not found", "tidak ditemukan"),
        WipeOutcome.InUse => T("in use", "sedang dipakai"),
        WipeOutcome.Cancelled => T("cancelled", "dibatalkan"),
        WipeOutcome.Wiped => T("wiped", "ditimpa"),
        _ => T("failed", "gagal"),
    };

    public string Method(WipeMethod method) => method switch
    {
        WipeMethod.Dod522022M => MethodDod,
        WipeMethod.Gutmann => MethodGutmann,
        _ => MethodZero,
    };

    public string SettingStatusText(Guardian.Settings.SettingStatus status) => status switch
    {
        Guardian.Settings.SettingStatus.Applied => T("Changed", "Diubah"),
        Guardian.Settings.SettingStatus.Unchanged => T("Already so", "Sudah begitu"),
        Guardian.Settings.SettingStatus.Restored => T("Restored", "Dipulihkan"),
        Guardian.Settings.SettingStatus.Skipped => T("Skipped", "Dilewati"),
        Guardian.Settings.SettingStatus.Failed => T("Failed", "Gagal"),
        _ => T("Pending", "Tertunda"),
    };

    public string AlreadyGone(string name) => T($"{name} is already gone.", $"{name} sudah tidak ada.");
}
