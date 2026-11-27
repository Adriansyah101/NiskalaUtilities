using Niskala.Updater;

namespace Niskala.UI.Services;

public sealed partial class Strings
{
    public string NavUpdates => T("Updates", "Pembaruan");
    public string UpdateSubtitle => T(
        "Niskala reads its GitHub releases and installs a new version only after its SHA-256 checksum matches SHA256SUMS.txt.",
        "Niskala membaca rilis GitHub-nya dan memasang versi baru hanya setelah checksum SHA-256-nya cocok dengan SHA256SUMS.txt.");
    public string UpdateThisCopy => T("This copy", "Salinan ini");
    public string UpdateLatestLabel => T("Latest release", "Rilis terbaru");
    public string UpdateNow => T("Update now", "Perbarui sekarang");
    public string UpdateReview => T("Review first", "Tinjau dulu");
    public string UpdateLater => T("Later", "Nanti");
    public string UpdatePromptIntro => T(
        "A newer release is on GitHub. Updating downloads it, checks it against SHA256SUMS.txt and installs it.",
        "Rilis yang lebih baru ada di GitHub. Pembaruan mengunduhnya, mencocokkannya dengan SHA256SUMS.txt, lalu memasangnya.");
    public string UpdatePromptNoNotes => T("This release has no notes on GitHub.", "Rilis ini tidak punya catatan di GitHub.");
    public string UpdatePromptTitle(string latest) => T($"Niskala Utilities {latest} is available", $"Niskala Utilities {latest} tersedia");
    public string UpdatePromptWhatsNew(string latest) => T($"What's new in v{latest}", $"Yang baru di v{latest}");
    public string UpdatePromptMore(int count) => T(
        count == 1 ? "and 1 more change on the Updates page" : $"and {count} more changes on the Updates page",
        $"dan {count} perubahan lain di halaman Pembaruan");
    public string UpdateCheck => T("Check for updates", "Periksa Pembaruan");
    public string UpdateAutoCheck => T("Check when Niskala opens", "Periksa saat Niskala dibuka");
    public string UpdateNotesTitle => T("Release notes", "Catatan rilis");
    public string UpdateNoNotes => T("No release notes are available.", "Catatan rilis tidak tersedia.");
    public string UpdateNotesOnGitHub(string version) => T($"v{version} on GitHub", $"v{version} di GitHub");

    public string UpdateOpenPage => T("Open on GitHub", "Buka di GitHub");
    public string UpdateRestart => T("Update & restart", "Perbarui & Mulai Ulang");
    public string UpdateSavePackage => T("Download the new package", "Unduh paket baru");
    public string UpdateChecking => T("Checking GitHub…", "Memeriksa GitHub…");
    public string UpdateNotChecked => T("Not checked: GitHub could not be reached.", "Belum diperiksa: GitHub tidak terjangkau.");
    public string UpdateIncomplete => T(
        "The release lacks this copy's package or SHA256SUMS.txt, so it cannot be verified.",
        "Rilis ini tidak memuat paket untuk salinan ini atau SHA256SUMS.txt, jadi tidak bisa diverifikasi.");
    public string UpdateRateLimited => T("GitHub limits how often it can be asked; try again later.", "GitHub membatasi frekuensi permintaan; coba lagi nanti.");
    public string UpdateTimedOut => T("GitHub did not answer in time.", "GitHub tidak menjawab tepat waktu.");
    public string UpdateBadAnswer => T("GitHub sent an answer Niskala cannot read.", "GitHub mengirim jawaban yang tidak bisa dibaca Niskala.");
    public string UpdateDownloadCancelled => T("Download cancelled; nothing was changed.", "Unduhan dibatalkan; tidak ada yang diubah.");
    public string PaletteUpdates => UpdateCheck;
    public string PaletteUpdatesHint => T("GitHub releases, verified with SHA-256", "Rilis GitHub, diverifikasi dengan SHA-256");

    public string UpdateInstalledCopy(string root) =>
        T($"Installed in {root}; an update runs the installer.", $"Terpasang di {root}; pembaruan menjalankan installer.");

    public string UpdatePortableCopy(string root) =>
        T($"Portable copy in {root}; an update is saved to Downloads.", $"Salinan portabel di {root}; pembaruan disimpan ke folder Unduhan.");

    public string UpdateStatusText(UpdateStatus status, string? latest) => status switch
    {
        UpdateStatus.Available => T($"Version {latest} is available.", $"Versi {latest} tersedia."),
        UpdateStatus.UpToDate => T("This copy is up to date.", "Salinan ini sudah terbaru."),
        _ => T("No release is published yet.", "Belum ada rilis yang diterbitkan."),
    };

    public string UpdatePublished(string when) => T($"Published {when}.", $"Terbit {when}.");

    public string UpdateUnreachable(string reason) => T($"GitHub could not be reached: {reason}", $"GitHub tidak terjangkau: {reason}");

    public string UpdateRejected(string reason) => T($"The update was refused: {reason}", $"Pembaruan ditolak: {reason}");

    public string UpdateDownloading(string name, string received, string? total) => total is null
        ? T($"Downloading {name}: {received}", $"Mengunduh {name}: {received}")
        : T($"Downloading {name}: {received} of {total}", $"Mengunduh {name}: {received} dari {total}");

    public string UpdateVerified(string name) => T($"{name}: SHA-256 matches.", $"{name}: SHA-256 cocok.");

    public string UpdateSaved(string path, string root) =>
        T($"Saved to {path}. Close Niskala, then unzip it over {root}.", $"Disimpan ke {path}. Tutup Niskala, lalu ekstrak di atas {root}.");
}
