using Niskala.Optimizer.Drives;

namespace Niskala.UI.Services;

public sealed partial class Strings
{
    public string NoValue => "—";
    public string ColumnDrive => T("Drive", "Drive");
    public string ColumnStatus => T("Status", "Status");
    public string ColumnLastAnalysis => T("Last analysis", "Analisis terakhir");
    public string ColumnFiles => T("Total files", "Total berkas");
    public string ColumnFragmentedFiles => T("Fragmented files", "Berkas terfragmentasi");
    public string ColumnFreeFragments => T("Free-space fragments", "Fragmen ruang kosong");
    public string ColumnFragmentation => T("Fragmentation %", "Fragmentasi %");
    public string ColumnAssessment => T("Assessment", "Penilaian");
    public string ColumnMedia => T("Media", "Media");
    public string ColumnFileSystem => T("File system", "Sistem berkas");
    public string ColumnCapacity => T("Capacity", "Kapasitas");
    public string StatusDetecting => T("Detecting…", "Mendeteksi…");
    public string StatusWaiting => T("Waiting for the drive…", "Menunggu drive…");
    public string StatusNoResponse => T("No response", "Tidak merespons");
    public string StatusAnalyzed => T("Analyzed", "Sudah dianalisis");
    public string StatusWorking => T("Working…", "Sedang diproses…");
    public string StatusHealthWarning => T("Health warning", "Peringatan kesehatan");
    public string TabMap => T("Cluster map", "Peta klaster");
    public string LegendReserved => T("MFT / metadata reserved", "MFT / metadata cadangan");
    public string LegendCompressed => T("Compressed", "Terkompresi");
    public string SummaryFreeFragments => T("Free-space fragments", "Fragmen ruang kosong");
    public string SummaryCompressed => T("Compressed files", "Berkas terkompresi");
    public string SummaryReserved => T("MFT / metadata reserved", "MFT / metadata cadangan");
    public string GuideGoalLabel => T("Goal", "Tujuan");
    public string GuideAlternatives => T("Other strategies", "Strategi lain");
    public string GuideApplyRecommendation => T("Apply recommendation", "Terapkan rekomendasi");
    public string GuideClose => T("Close the Strategy Guide", "Tutup Panduan Strategi");
    public string GuideChoose => T("Choose", "Pilih");
    public string GuideTrimHint => T(
        "Tells the SSD which blocks are free so it can erase them in the background. No data is moved.",
        "Memberi tahu SSD blok mana yang kosong agar bisa dihapus di latar belakang. Tidak ada data yang dipindah.");
    public string GuideNoAction => T("Nothing to apply on this drive right now.", "Saat ini tidak ada yang perlu diterapkan pada drive ini.");
    public string ScheduleLabel => T("Scheduled optimization", "Optimasi terjadwal");
    public string ScheduleGuard => T("SSDs only get TRIM and hard disks only get Quick Defrag.", "SSD hanya mendapat TRIM dan hard disk hanya mendapat Defrag cepat.");
    public string ScheduleNeedsInstall => T(
        "Scheduling needs the installed copy of Niskala Utilities in Program Files.",
        "Penjadwalan memerlukan salinan Niskala Utilities yang terpasang di Program Files.");
    public string ScheduleReading => T("Reading the schedule…", "Membaca jadwal…");
    public string HealthInterface => T("Interface", "Antarmuka");
    public string HealthModel => T("Model", "Model");
    public string HealthSectors => T("Reallocated / pending / uncorrectable", "Dialihkan / tertunda / tak terkoreksi");
    public string HealthDataRead => T("Data read", "Data dibaca");
    public string HealthDataWritten => T("Data written", "Data ditulis");
    public string HealthPowerCycles => T("Power cycles", "Siklus daya");
    public string HealthUnsafeShutdowns => T("Unsafe shutdowns", "Mati mendadak");
    public string HealthMediaErrors => T("Media errors", "Galat media");
    public string AttributesTitle => T("S.M.A.R.T. attributes", "Atribut S.M.A.R.T.");
    public string AttributeId => "ID";
    public string AttributeName => T("Attribute", "Atribut");
    public string AttributeCurrent => T("Current", "Kini");
    public string AttributeWorst => T("Worst", "Terburuk");
    public string AttributeThreshold => T("Threshold", "Ambang");
    public string AttributeRaw => T("Raw", "Mentah");
    public string AttributeOk => T("OK", "OK");
    public string AttributeFailing => T("Failing", "Gagal");

    public string AssessmentName(DriveAssessment assessment) => assessment switch
    {
        DriveAssessment.HeavilyFragmented => T("Heavily fragmented", "Sangat terfragmentasi"),
        DriveAssessment.SlightlyFragmented => T("Slightly fragmented", "Sedikit terfragmentasi"),
        _ => T("Tidy", "Rapi"),
    };

    public string ScheduleName(MaintenanceSchedule schedule) => schedule switch
    {
        MaintenanceSchedule.Weekly => T("Weekly", "Mingguan"),
        MaintenanceSchedule.Monthly => T("Monthly", "Bulanan"),
        MaintenanceSchedule.WhenIdle => T("When idle", "Saat menganggur"),
        _ => T("Off", "Mati"),
    };

    public string ScheduleNote(ScheduleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        string next = state.NextRun is { } when ? T($" Next run: {Format.When(when)}.", $" Berikutnya: {Format.When(when)}.") : string.Empty;
        string text = state.Schedule switch
        {
            MaintenanceSchedule.Weekly => T("Every Sunday at 03:00 through Windows Task Scheduler.", "Setiap Minggu pukul 03.00 lewat Windows Task Scheduler.") + next,
            MaintenanceSchedule.Monthly => T("On the 1st of every month at 03:00 through Windows Task Scheduler.", "Setiap tanggal 1 pukul 03.00 lewat Windows Task Scheduler.") + next,
            MaintenanceSchedule.WhenIdle => T(
                "During Windows idle maintenance, at most once a day.",
                "Saat pemeliharaan menganggur Windows, paling banyak sekali sehari."),
            _ => T("No scheduled task; drives are optimized only when you start it.", "Tidak ada tugas terjadwal; drive hanya dioptimalkan saat Anda memulainya."),
        };
        string failed = state.Error != 0
            ? T($" The schedule could not be changed (error 0x{state.Error:X8}).", $" Jadwal tidak dapat diubah (galat 0x{state.Error:X8}).")
            : string.Empty;
        return state.Schedule == MaintenanceSchedule.Off ? text + failed : $"{text} {ScheduleGuard}{failed}";
    }

    public string ProtocolName(HealthProtocol protocol) => protocol switch
    {
        HealthProtocol.Nvme => "NVMe",
        HealthProtocol.Sata => "SATA / SAT",
        HealthProtocol.Scsi => "SCSI",
        _ => MediaUnknown,
    };

    public string InterfaceText(DriveHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        string protocol = ProtocolName(health.Protocol);
        if (!health.Bridged)
        {
            return protocol;
        }

        string bridge = health.Bridge is { Length: > 0 } name ? $" {name}" : string.Empty;
        return T($"{protocol} · USB bridge{bridge}", $"{protocol} · bridge USB{bridge}");
    }

    public string SectorCounts(DriveHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        return $"{Count(health.ReallocatedSectors)} / {Count(health.PendingSectors)} / {Count(health.UncorrectableSectors)}";

        string Count(long? value) => value is { } number ? Format.Number(number) : NoValue;
    }

    public string InspectorReserved(long bytes) => T(
        $"{Format.Bytes(bytes)} here lies in the MFT / metadata reserved area.",
        $"{Format.Bytes(bytes)} di sini berada di area cadangan MFT / metadata.");

    public string AttributeTitle(byte id) => id switch
    {
        1 => T("Read error rate", "Laju galat baca"),
        2 => T("Throughput performance", "Kinerja throughput"),
        3 => T("Spin-up time", "Waktu putar awal"),
        4 => T("Start/stop count", "Jumlah mulai/berhenti"),
        5 => T("Reallocated sectors", "Sektor dialihkan"),
        7 => T("Seek error rate", "Laju galat pencarian"),
        8 => T("Seek time performance", "Kinerja waktu pencarian"),
        9 => T("Power-on hours", "Jam menyala"),
        10 => T("Spin retry count", "Percobaan ulang putaran"),
        11 => T("Calibration retries", "Percobaan ulang kalibrasi"),
        12 => T("Power cycle count", "Jumlah siklus daya"),
        171 => T("Program fail count", "Jumlah gagal program"),
        172 => T("Erase fail count", "Jumlah gagal hapus"),
        173 or 177 => T("Wear leveling count", "Jumlah perataan keausan"),
        174 => T("Unexpected power loss", "Kehilangan daya mendadak"),
        179 => T("Used reserved blocks", "Blok cadangan terpakai"),
        180 => T("Unused reserved blocks", "Blok cadangan tersisa"),
        181 => T("Program fail count", "Jumlah gagal program"),
        182 => T("Erase fail count", "Jumlah gagal hapus"),
        183 => T("Runtime bad blocks", "Blok rusak saat berjalan"),
        184 => T("End-to-end errors", "Galat ujung ke ujung"),
        187 => T("Reported uncorrectable errors", "Galat tak terkoreksi yang dilaporkan"),
        188 => T("Command timeouts", "Perintah kehabisan waktu"),
        189 => T("High fly writes", "Penulisan head terlalu tinggi"),
        190 => T("Airflow temperature", "Suhu aliran udara"),
        191 => T("Shock events", "Kejadian guncangan"),
        192 => T("Power-off retracts", "Penarikan head saat mati"),
        193 => T("Load/unload cycles", "Siklus muat/lepas head"),
        194 => T("Temperature", "Suhu"),
        195 => T("Hardware ECC recovered", "Dipulihkan ECC perangkat keras"),
        196 => T("Reallocation events", "Kejadian pengalihan"),
        197 => T("Pending sectors", "Sektor tertunda"),
        198 => T("Offline uncorrectable", "Tak terkoreksi saat luring"),
        199 => T("Interface CRC errors", "Galat CRC antarmuka"),
        200 => T("Write error rate", "Laju galat tulis"),
        231 => T("Life left", "Sisa umur"),
        233 => T("Media wearout", "Keausan media"),
        241 => T("Total LBAs written", "Total LBA ditulis"),
        242 => T("Total LBAs read", "Total LBA dibaca"),
        _ => T("Vendor specific", "Khusus vendor"),
    };
}
