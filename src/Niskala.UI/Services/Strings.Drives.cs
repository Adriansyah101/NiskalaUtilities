using Niskala.Optimizer.Drives;

namespace Niskala.UI.Services;

public sealed partial class Strings
{
    public static readonly string[] DriveKeywords = ["Drive Optimizer", "Optimasi Drive", "Defrag", "Defragment", "Defragmentasi", "TRIM", "SSD", "HDD", "Optimize", "Optimalkan"];

    public string DrivesTitle => NavDrives;
    public string DrivesSubtitle => T(
        "Hard disks are defragmented with the file system's own move calls, in small steps at background priority; solid-state drives get TRIM instead and are never defragmented.",
        "Hard disk didefrag dengan panggilan pemindahan milik sistem berkas, sedikit demi sedikit dengan prioritas latar belakang; SSD mendapat TRIM dan tidak pernah didefrag.");
    public string DriveAnalyze => T("Analyze", "Analisis");
    public string DriveReanalyze => T("Re-analyze", "Analisis ulang");
    public string DriveStopping => T("Stopping after current file…", "Menghentikan setelah berkas aktif selesai…");
    public string DriveDefragment => T("Defragment", "Defragmentasi");
    public string DriveTrim => T("Optimize (TRIM)", "Optimalkan (TRIM)");
    public string DriveAnalyzeOnly => T("Analyze only", "Hanya analisis");
    public string DriveModeLabel => T("Mode", "Mode");
    public string ModeQuick => T("Quick Defrag", "Defrag cepat");
    public string ModeConsolidate => T("Consolidate Free Space", "Defrag & rapikan ruang kosong");
    public string ModeFullZone => T("Full Zone Optimization", "Optimasi zona penuh");
    public string MediaHdd => "HDD";
    public string MediaSsd => "SSD";
    public string MediaUnknown => T("Unknown", "Tidak diketahui");
    public string DriveNotAnalyzed => T("Not analyzed", "Belum dianalisis");
    public string TabFragmented => T("Fragmented files", "Berkas terfragmentasi");
    public string TabHealth => T("Drive health & summary", "Kesehatan & ringkasan drive");
    public string DefragChecked => T("Defrag checked", "Defrag yang dicentang");
    public string CheckAll => T("Check all", "Centang semua");
    public string CheckNone => T("Clear", "Kosongkan");
    public string LegendFree => T("Free space", "Ruang kosong");
    public string LegendUsed => T("Used, contiguous", "Terpakai rapi");
    public string LegendFragmented => T("Fragmented", "Terfragmentasi");
    public string LegendArchive => T("Archive zone", "Zona arsip");
    public string LegendSystem => T("System / locked", "Sistem / Terkunci");
    public string DriveMapEmpty => T("Analyze the drive to draw its cluster map.", "Analisis drive untuk menggambar peta klasternya.");
    public string DriveSummaryEmpty => T("Analyze the drive to see its health and summary.", "Analisis drive untuk melihat kesehatan dan ringkasannya.");
    public string FragmentsEmpty => T("No fragmented files were found.", "Tidak ada berkas terfragmentasi.");
    public string FragmentsIdle => T("Analyze a hard disk to list its fragmented files.", "Analisis hard disk untuk melihat berkas yang terfragmentasi.");
    public string SsdNote => T(
        "Solid-state drives are never defragmented: moving clusters only wears them. TRIM tells the drive which blocks are free so it can keep writing fast.",
        "SSD tidak pernah didefrag: memindahkan klaster hanya mengausnya. TRIM memberi tahu drive blok mana yang kosong agar penulisan tetap cepat.");
    public string NoDrives => T("No fixed drives were found.", "Tidak ada drive tetap yang ditemukan.");
    public string DriveDefragConfirm => T(
        "Defragment this drive now? Files in use are skipped, and the work pauses whenever you use the disk.",
        "Defrag drive ini sekarang? Berkas yang sedang dipakai dilewati, dan proses berhenti sejenak setiap kali Anda memakai disk.");
    public string SummaryClusters => T("Clusters", "Klaster");
    public string SummaryFree => T("Free space", "Ruang kosong");
    public string SummaryFragmentedFiles => T("Fragmented files", "Berkas terfragmentasi");
    public string SummaryExcess => T("Excess fragments", "Fragmen berlebih");
    public string SummaryShare => T("Fragmented data", "Data terfragmentasi");
    public string SummaryLargestFree => T("Largest free block", "Blok kosong terbesar");
    public string SummaryZones => T("Speed zones", "Zona kecepatan");
    public string SummarySkipped => T("Left alone", "Dibiarkan");
    public string SummaryMedia => T("Media", "Media");
    public string SummaryAnalyzed => T("Analyzed", "Dianalisis");
    public string PaletteDrives => NavDrives;
    public string PaletteDrivesHint => T("Defragment hard disks or send TRIM to SSDs", "Defrag hard disk atau kirim TRIM ke SSD");

    public string ModeName(DefragMode mode) => mode switch
    {
        DefragMode.Consolidate => ModeConsolidate,
        DefragMode.FullZone => ModeFullZone,
        _ => ModeQuick,
    };

    public string ModeHint(DefragMode mode) => mode switch
    {
        DefragMode.Consolidate => T(
            "Joins fragmented files and fills the gaps left by small files, so free space ends up in large runs.",
            "Menyatukan berkas terpecah dan mengisi celah berkas kecil, sehingga ruang kosong menjadi blok besar."),
        DefragMode.FullZone => T(
            "Joins fragmented files, puts programs and libraries on the fast outer tracks, archives on the inner tracks, and compacts free space.",
            "Menyatukan berkas terpecah, menaruh program dan pustaka di lintasan luar yang cepat, arsip di lintasan dalam, lalu memadatkan ruang kosong."),
        _ => T(
            "Joins fragmented files into free runs that fit them, with as few writes as possible.",
            "Menyatukan berkas terpecah ke ruang kosong yang muat, dengan penulisan sesedikit mungkin."),
    };

    public string MediaName(MediaKind media) => media switch
    {
        MediaKind.Hdd => MediaHdd,
        MediaKind.Ssd => MediaSsd,
        _ => MediaUnknown,
    };

    public string ActionName(DriveAction action) => action switch
    {
        DriveAction.Defragment => DriveDefragment,
        DriveAction.Trim => DriveTrim,
        _ => DriveAnalyzeOnly,
    };

    public string ZoneName(SpeedZone zone) => zone switch
    {
        SpeedZone.Fast => T("Fast", "Cepat"),
        SpeedZone.Archive => T("Archive", "Arsip"),
        _ => T("Standard", "Standar"),
    };

    public string DriveCapacity(string total, string free) => T($"{total} · {free} free", $"{total} · {free} kosong");

    public string DriveFragmented(double percent) => T($"{Format.Percent(percent)} fragmented", $"{Format.Percent(percent)} terfragmentasi");

    public string FragmentCount(int fragments) => T($"{Format.Number(fragments)} fragments", $"{Format.Number(fragments)} fragmen");

    public string FragmentListTitle(int shown, int total) => shown == total
        ? T($"{Format.Number(total)} fragmented files", $"{Format.Number(total)} berkas terfragmentasi")
        : T($"{Format.Number(shown)} most fragmented of {Format.Number(total)}", $"{Format.Number(shown)} paling terpecah dari {Format.Number(total)}");

    public string DrivePhaseText(DrivePhase phase, string current) => phase switch
    {
        DrivePhase.Opening => T($"Opening {current}…", $"Membuka {current}…"),
        DrivePhase.ReadingMap => T("Reading the cluster map…", "Membaca peta klaster…"),
        DrivePhase.ReadingFileTable => T("Reading the file table…", "Membaca tabel berkas…"),
        DrivePhase.Stopping => T($"Finishing {current}", $"Menyelesaikan {current}"),
        DrivePhase.Scanning => T($"Scanning {current}", $"Memindai {current}"),
        DrivePhase.Moving => T($"Moving {current}", $"Memindahkan {current}"),
        DrivePhase.Compacting => T($"Compacting {current}", $"Memadatkan {current}"),
        DrivePhase.Trimming => T("Sending TRIM…", "Mengirim TRIM…"),
        _ => string.Empty,
    };

    public string ClusterCount(long clusters, string size) => T($"{Format.Number(clusters)} × {size}", $"{Format.Number(clusters)} × {size}");

    public string FreeShare(string bytes, double percent) => $"{bytes} ({Format.Percent(percent)})";

    public string ZoneSpread(int fast, int standard, int archive) =>
        $"{ZoneName(SpeedZone.Fast)} {Format.Number(fast)} · {ZoneName(SpeedZone.Standard)} {Format.Number(standard)} · {ZoneName(SpeedZone.Archive)} {Format.Number(archive)}";

    public string SkippedCounts(int protectedFiles, int locked, int unreadable) => T(
        $"{Format.Number(protectedFiles)} protected · {Format.Number(locked)} in use · {Format.Number(unreadable)} unreadable",
        $"{Format.Number(protectedFiles)} dilindungi · {Format.Number(locked)} dipakai · {Format.Number(unreadable)} tak terbaca");

    public string MediaFacts(DriveVolume volume)
    {
        ArgumentNullException.ThrowIfNull(volume);
        string trim = volume.Media != MediaKind.Ssd ? string.Empty : volume.TrimSupported ? T(" · TRIM on", " · TRIM aktif") : T(" · no TRIM", " · tanpa TRIM");
        string bus = BusName(volume.Bus) is { Length: > 0 } name ? $" · {name}" : string.Empty;
        string rotation = volume.Media == MediaKind.Hdd && MediaClassifier.IsRpm(volume.RotationRate) ? $" · {volume.RotationRate.ToString("N0", Culture)} rpm" : string.Empty;
        return $"{MediaName(volume.Media)} · {volume.FileSystem}{bus}{rotation}{trim}";
    }

    public static string BusName(StorageBus bus) => bus switch
    {
        StorageBus.Usb => "USB",
        StorageBus.Sata => "SATA",
        StorageBus.Ata or StorageBus.Atapi => "ATA",
        StorageBus.Nvme => "NVMe",
        StorageBus.Sas => "SAS",
        StorageBus.Scsi => "SCSI",
        StorageBus.Ieee1394 => "IEEE 1394",
        StorageBus.Sd => "SD",
        StorageBus.Mmc => "MMC",
        StorageBus.Raid => "RAID",
        StorageBus.Iscsi => "iSCSI",
        StorageBus.Virtual or StorageBus.FileBackedVirtual => "VHD",
        _ => string.Empty,
    };

    public string AnalyzeDone(string drive, double percent, int files, string elapsed) => T(
        $"{drive} analyzed in {elapsed}: {Format.Percent(percent)} of the data is fragmented across {Format.Number(files)} files.",
        $"{drive} dianalisis dalam {elapsed}: {Format.Percent(percent)} data terfragmentasi di {Format.Number(files)} berkas.");

    public string DefragDone(string drive, DefragResult result, string moved, string elapsed)
    {
        ArgumentNullException.ThrowIfNull(result);
        string head = result.Cancelled
            ? T($"Stopped on {drive} after {elapsed}: ", $"Dihentikan pada {drive} setelah {elapsed}: ")
            : T($"{ModeName(result.Mode)} finished on {drive} in {elapsed}: ", $"{ModeName(result.Mode)} selesai pada {drive} dalam {elapsed}: ");
        string body = T(
            $"{Format.Number(result.Moved)} files moved ({moved}), {Format.Number(result.Defragmented)} joined.",
            $"{Format.Number(result.Moved)} berkas dipindah ({moved}), {Format.Number(result.Defragmented)} disatukan.");
        string skipped = result.Locked + result.NoRoom + result.Failed == 0 ? string.Empty : T(
            $" Skipped: {Format.Number(result.Locked)} in use, {Format.Number(result.NoRoom)} without room, {Format.Number(result.Failed)} refused.",
            $" Dilewati: {Format.Number(result.Locked)} dipakai, {Format.Number(result.NoRoom)} tanpa ruang, {Format.Number(result.Failed)} ditolak.");
        return head + body + skipped;
    }

    public string TrimDone(string drive, TrimResult result, string elapsed)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Outcome switch
        {
            TrimOutcome.Trimmed when result.Method == TrimMethod.FileLevel => T(
                $"TRIM sent for {Format.Bytes(result.Bytes)} of free space on {drive} in {elapsed}.",
                $"TRIM dikirim untuk {Format.Bytes(result.Bytes)} ruang kosong di {drive} dalam {elapsed}."),
            TrimOutcome.Trimmed => T(
                $"Windows retrimmed the free space of {drive} in {elapsed}.",
                $"Windows memangkas ulang ruang kosong {drive} dalam {elapsed}."),
            TrimOutcome.NotSupported => T(
                $"{drive} does not accept TRIM, so nothing was sent.",
                $"{drive} tidak menerima TRIM, jadi tidak ada yang dikirim."),
            TrimOutcome.Cancelled => T("TRIM was cancelled; the drive is unchanged.", "TRIM dibatalkan; drive tidak berubah."),
            _ => T($"TRIM on {drive} failed (exit code {result.Error}).", $"TRIM pada {drive} gagal (kode keluar {result.Error})."),
        };
    }

    public string DriveNeedsAdmin(string drive) => T(
        $"{drive} can only be read cluster by cluster when Niskala runs as administrator.",
        $"{drive} hanya bisa dibaca per klaster bila Niskala berjalan sebagai administrator.");

    public string DriveFailed(string drive, string reason) => T($"{drive}: {reason}", $"{drive}: {reason}");

    public string DriveCancelled(string drive) => T($"{drive}: stopped; nothing after that point was changed.", $"{drive}: dihentikan; tidak ada yang diubah setelah titik itu.");
}
