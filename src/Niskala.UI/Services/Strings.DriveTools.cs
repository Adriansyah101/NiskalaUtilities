using Niskala.Optimizer.Drives;

namespace Niskala.UI.Services;

public sealed partial class Strings
{
    private const int HoursPerDay = 24;

    public string GuideButton => T("Strategy Guide", "Panduan Strategi");
    public string GuideTitle => T("Which strategy fits this drive?", "Strategi mana yang cocok untuk drive ini?");
    public string GuideApply => T("Use this strategy", "Pakai strategi ini");
    public string GuideActive => T("Selected", "Terpilih");
    public string TabInspector => T("Cluster inspector", "Inspektor klaster");
    public string InspectorEmpty => T("Click a block on the drive map to see which files occupy it.", "Klik satu blok di peta drive untuk melihat berkas yang menempatinya.");
    public string InspectorBusy => T("Reading the selected block…", "Membaca blok yang dipilih…");
    public string InspectorNoFiles => T("No scanned file uses this block.", "Tidak ada berkas terpindai yang memakai blok ini.");
    public string InspectorClear => T("Clear", "Kosongkan");
    public string MapHint => T("Click a block, or focus the map and use the arrow keys, to inspect its clusters.", "Klik satu blok, atau fokus ke peta lalu pakai tombol panah, untuk memeriksa klasternya.");
    public string HealthTitle => T("Hardware telemetry (S.M.A.R.T.)", "Telemetri perangkat keras (S.M.A.R.T.)");
    public string SummaryTitle => T("Volume summary", "Ringkasan volume");
    public string HealthReading => T("Reading hardware telemetry…", "Membaca telemetri perangkat keras…");
    public string HealthStatusLabel => T("Physical health", "Kesehatan fisik");
    public string HealthTemperature => T("Temperature", "Suhu");
    public string HealthHours => T("Power-on hours", "Jam menyala");
    public string HealthWear => T("Wear level", "Tingkat keausan");
    public string HealthSpare => T("Available spare", "Cadangan tersedia");
    public string HealthSourceLabel => T("Read from", "Dibaca dari");
    public string HealthIssues => T("Attention", "Perhatian");
    public string QueueHint => T("Checked SSDs only get TRIM; hard disks use the selected mode.", "SSD yang dicentang hanya mendapat TRIM; hard disk memakai mode yang dipilih.");
    public string QueueConfirm => T(
        "Optimize the checked drives one after another? Hard disks are defragmented with the selected mode and SSDs only get TRIM.",
        "Optimalkan drive yang dicentang satu per satu? Hard disk didefrag dengan mode yang dipilih dan SSD hanya mendapat TRIM.");

    public string GoalName(StrategyGoal goal) => goal switch
    {
        StrategyGoal.MaximumSpeed => T("Maximum Speed", "Kecepatan Maksimal"),
        StrategyGoal.ReclaimSpace => T("Reclaim Contiguous Space", "Satukan Ruang Kosong"),
        _ => T("Low Impact", "Dampak Ringan"),
    };

    public string GoalHint(StrategyGoal goal) => goal switch
    {
        StrategyGoal.MaximumSpeed => T(
            "Joins fragmented files and places programs and libraries on the fastest part of the hard disk.",
            "Menyatukan berkas terfragmentasi dan menaruh program serta pustaka di bagian hard disk yang paling cepat."),
        StrategyGoal.ReclaimSpace => T(
            "Packs data together so small free gaps become one large free block at the end of the partition.",
            "Merapatkan data sehingga celah kosong kecil menjadi satu blok kosong besar di ujung partisi."),
        _ => T(
            "Quick tidy-up with as few writes as possible, so the PC stays responsive while you work.",
            "Penataan cepat dengan penulisan sesedikit mungkin, agar PC tetap responsif saat Anda bekerja."),
    };

    public string FitName(StrategyFit fit) => fit switch
    {
        StrategyFit.Recommended => T("Recommended", "Direkomendasikan"),
        StrategyFit.Limited => T("Limited room", "Ruang terbatas"),
        StrategyFit.NotApplicable => T("Not applicable", "Tidak berlaku"),
        _ => T("Suitable", "Cocok"),
    };

    public string GuideVerdict(StrategyAdvice advice, DriveVolume volume, DriveSummary? summary)
    {
        ArgumentNullException.ThrowIfNull(advice);
        ArgumentNullException.ThrowIfNull(volume);
        string fragmented = summary is null ? string.Empty : Format.Percent(summary.FragmentedPercent);
        string free = summary is null ? string.Empty : Format.Percent(StrategyGuide.FreePercent(summary));
        string share = summary is null ? string.Empty : Format.Percent(StrategyGuide.FreeShare(summary) * 100);
        return advice.Reason switch
        {
            StrategyReason.NotAnalyzed => T(
                "Analyze this drive first; the guide then weighs its fragmentation and free space.",
                "Analisis drive ini dulu; panduan lalu menimbang fragmentasi dan ruang kosongnya."),
            StrategyReason.HeavyFragmentation => T(
                $"{fragmented} of the data is fragmented, so Maximum Speed pays off most.",
                $"{fragmented} data terfragmentasi, jadi Kecepatan Maksimal paling bermanfaat."),
            StrategyReason.LightFragmentation => T(
                $"Only {fragmented} of the data is fragmented, so Low Impact is enough.",
                $"Hanya {fragmented} data yang terfragmentasi, jadi Dampak Ringan sudah cukup."),
            StrategyReason.ScatteredFreeSpace => T(
                $"Free space is split into small gaps (the largest block holds {share} of it), so Reclaim Contiguous Space helps most.",
                $"Ruang kosong terpecah menjadi celah kecil (blok terbesar hanya {share}), jadi Satukan Ruang Kosong paling membantu."),
            StrategyReason.LowFreeSpace => T(
                $"Only {free} of the drive is free: Low Impact needs the least room, and Maximum Speed may not place every file.",
                $"Hanya {free} drive yang kosong: Dampak Ringan butuh ruang paling sedikit, dan Kecepatan Maksimal mungkin tidak bisa menaruh semua berkas."),
            StrategyReason.AlreadyTidy => T(
                "This drive is already in good shape; no optimization is needed right now.",
                "Drive ini sudah rapi; saat ini tidak perlu dioptimalkan."),
            StrategyReason.SolidState => T(
                "Solid-state drive: moving clusters does not apply here and only adds wear. Use Optimize (TRIM) instead.",
                "SSD: memindahkan klaster tidak berlaku di sini dan hanya menambah keausan. Pakai Optimalkan (TRIM)."),
            StrategyReason.UnsupportedFileSystem => T(
                $"{volume.FileSystem} does not allow cluster moves, so Niskala only analyzes this drive.",
                $"{volume.FileSystem} tidak mengizinkan pemindahan klaster, jadi Niskala hanya menganalisis drive ini."),
            _ => T(
                "The media type could not be confirmed, so Niskala only analyzes this drive.",
                "Jenis media tidak dapat dipastikan, jadi Niskala hanya menganalisis drive ini."),
        };
    }

    public string GuideApplied(StrategyGoal goal) => T(
        $"{GoalName(goal)} selected ({ModeName(StrategyGuide.ModeFor(goal))}). Press {DriveDefragment} to start.",
        $"{GoalName(goal)} dipilih ({ModeName(StrategyGuide.ModeFor(goal))}). Tekan {DriveDefragment} untuk memulai.");

    public string OccupantName(OccupantState state) => state switch
    {
        OccupantState.Fragmented => LegendFragmented,
        OccupantState.Archive => LegendArchive,
        OccupantState.SystemLocked => LegendSystem,
        _ => T("Contiguous", "Rapi"),
    };

    public string InspectorTitle(ClusterInspection inspection, long bytesPerCluster)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        long cluster = Math.Max(1, bytesPerCluster);
        double free = inspection.Clusters == 0 ? 0 : inspection.FreeClusters * 100.0 / inspection.Clusters;
        string range = $"{Format.Number(inspection.Start)}–{Format.Number(Math.Max(inspection.Start, inspection.End - 1))}";
        return T(
            $"Clusters {range} · {Format.Bytes(inspection.Clusters * cluster)} · {Format.Number(inspection.TotalFiles)} files · {Format.Percent(free)} free",
            $"Klaster {range} · {Format.Bytes(inspection.Clusters * cluster)} · {Format.Number(inspection.TotalFiles)} berkas · {Format.Percent(free)} kosong");
    }

    public string InspectorUnlisted(long bytes) => T(
        $"{Format.Bytes(bytes)} here belongs to system files, folders or files in use.",
        $"{Format.Bytes(bytes)} di sini milik berkas sistem, folder, atau berkas yang sedang dipakai.");

    public string InspectorMore(int shown, int total) => T(
        $"Showing the {Format.Number(shown)} largest of {Format.Number(total)} files.",
        $"Menampilkan {Format.Number(shown)} terbesar dari {Format.Number(total)} berkas.");

    public string InspectorClusters(long clusters) => T($"{Format.Number(clusters)} clusters here", $"{Format.Number(clusters)} klaster di sini");

    public string HealthStatusName(HealthStatus status) => status switch
    {
        HealthStatus.Healthy => T("Healthy", "Sehat"),
        HealthStatus.Warning => T("Warning", "Peringatan"),
        _ => T("Not reported", "Tidak dilaporkan"),
    };

    public string HealthSourceName(HealthSource source) => source switch
    {
        HealthSource.Nvme => T("NVMe health log", "Log kesehatan NVMe"),
        HealthSource.Ata => T("S.M.A.R.T. attributes", "Atribut S.M.A.R.T."),
        HealthSource.FailurePrediction => T("Windows failure prediction", "Prediksi kegagalan Windows"),
        HealthSource.Temperature => T("Temperature sensor only", "Hanya sensor suhu"),
        _ => T("Nothing", "Tidak ada"),
    };

    public string HealthUnavailable(bool bridged) => bridged
        ? T(
            "This drive's USB bridge does not pass S.M.A.R.T. data through, so its physical health cannot be read.",
            "Bridge USB drive ini tidak meneruskan data S.M.A.R.T., jadi kesehatan fisiknya tidak dapat dibaca.")
        : T(
            "This drive does not report S.M.A.R.T. data to Windows.",
            "Drive ini tidak melaporkan data S.M.A.R.T. ke Windows.");

    public string Celsius(int value) => $"{value.ToString("N0", Culture)} °C";

    public string PowerOnHours(long hours) => T(
        $"{Format.Number(hours)} h (about {Format.Number(hours / HoursPerDay)} days)",
        $"{Format.Number(hours)} jam (sekitar {Format.Number(hours / HoursPerDay)} hari)");

    public string WearText(int used, int remaining) => T($"{Format.Number(used)}% used · {Format.Number(remaining)}% life left", $"{Format.Number(used)}% terpakai · sisa umur {Format.Number(remaining)}%");

    public string HealthWarnings(HealthWarning warnings)
    {
        var parts = new List<string>();
        foreach (HealthWarning flag in Enum.GetValues<HealthWarning>())
        {
            if (flag != HealthWarning.None && warnings.HasFlag(flag))
            {
                parts.Add(HealthWarningName(flag));
            }
        }

        return string.Join(" · ", parts);
    }

    public string HealthWarningName(HealthWarning warning) => warning switch
    {
        HealthWarning.FailurePredicted => T("the drive predicts a failure", "drive memprediksi kegagalan"),
        HealthWarning.SpareLow => T("spare blocks are running low", "blok cadangan hampir habis"),
        HealthWarning.WornOut => T("rated write endurance is used up", "batas ketahanan tulis sudah habis"),
        HealthWarning.MediaErrors => T("media errors were recorded", "tercatat galat media"),
        HealthWarning.ReadOnly => T("the drive switched to read-only", "drive beralih ke mode hanya-baca"),
        HealthWarning.Overheating => T("temperature is over the warning limit", "suhu melewati batas peringatan"),
        HealthWarning.ReallocatedSectors => T("sectors were reallocated", "ada sektor yang dialihkan"),
        HealthWarning.PendingSectors => T("unstable sectors are waiting to be remapped", "ada sektor tidak stabil menunggu dipetakan ulang"),
        _ => string.Empty,
    };

    public string QueueAnalyze(int count) => T($"Analyze checked ({Format.Number(count)})", $"Analisis yang dicentang ({Format.Number(count)})");

    public string QueueOptimize(int count) => T($"Optimize checked ({Format.Number(count)})", $"Optimalkan yang dicentang ({Format.Number(count)})");

    public string QueueCheckName(string drive) => T($"Add {drive} to the queue", $"Tambahkan {drive} ke antrean");

    public string QueueActivity(int index, int count, string activity) => $"[{Format.Number(index + 1)}/{Format.Number(count)}] {activity}";

    public string QueueSkipName(QueueSkipReason reason) => reason switch
    {
        QueueSkipReason.NoTrim => T("does not accept TRIM", "tidak menerima TRIM"),
        QueueSkipReason.UnsupportedFileSystem => T("file system cannot move clusters", "sistem berkas tidak dapat memindah klaster"),
        _ => T("media type unknown", "jenis media tidak diketahui"),
    };

    public string QueueOutcomeText(QueueResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string drive = result.Step.Volume.Name;
        string detail = result.Outcome switch
        {
            QueueOutcome.Skipped => T($"skipped, {QueueSkipName(result.Step.Skip)}", $"dilewati, {QueueSkipName(result.Step.Skip)}"),
            QueueOutcome.Cancelled => T("stopped", "dihentikan"),
            QueueOutcome.Refused => T("refused: clusters are never moved on an SSD", "ditolak: klaster SSD tidak pernah dipindah"),
            QueueOutcome.Failed => result.Error is { Length: > 0 } error ? error : T("failed", "gagal"),
            _ when result.Trim is { } trim => trim.Outcome == TrimOutcome.Trimmed ? T("TRIM sent", "TRIM terkirim") : T("TRIM not accepted", "TRIM tidak diterima"),
            _ when result.Defrag is { } defrag => T(
                $"{Format.Number(defrag.Defragmented)} joined, {Format.Number(defrag.Moved)} moved",
                $"{Format.Number(defrag.Defragmented)} disatukan, {Format.Number(defrag.Moved)} dipindah"),
            _ when result.Analysis is { } analysis => T(
                $"{Format.Percent(analysis.Summary().FragmentedPercent)} fragmented",
                $"{Format.Percent(analysis.Summary().FragmentedPercent)} terfragmentasi"),
            _ => T("done", "selesai"),
        };
        return $"{drive} {detail}";
    }

    public string QueueDone(IReadOnlyList<QueueResult> results, string elapsed)
    {
        ArgumentNullException.ThrowIfNull(results);
        int done = results.Count(r => r.Outcome is QueueOutcome.Done or QueueOutcome.Warning);
        string head = T(
            $"Queue finished in {elapsed}: {Format.Number(done)} of {Format.Number(results.Count)} drives done. ",
            $"Antrean selesai dalam {elapsed}: {Format.Number(done)} dari {Format.Number(results.Count)} drive selesai. ");
        return head + string.Join(" · ", results.Select(QueueOutcomeText));
    }
}
