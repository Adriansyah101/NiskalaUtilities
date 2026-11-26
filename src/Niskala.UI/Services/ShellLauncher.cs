using Niskala.Interop.Processes;

namespace Niskala.UI.Services;

internal static class ShellLauncher
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorInvalidParameter = 87;

    private static readonly string Explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    public static int OpenFolder(string folder) =>
        Directory.Exists(folder) ? Start($"\"{Path.TrimEndingDirectorySeparator(folder)}\"") : ErrorFileNotFound;

    public static int ShowItem(string path)
    {
        if (File.Exists(path))
        {
            return Start($"/select,\"{path}\"");
        }

        return Directory.Exists(path) ? OpenFolder(path)
            : Path.GetDirectoryName(path) is { } parent && Directory.Exists(parent) ? OpenFolder(parent)
            : ErrorFileNotFound;
    }

    public static int OpenLink(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? Start($"\"{uri.AbsoluteUri}\"")
            : ErrorInvalidParameter;

    public static bool IsLink(string? url) =>
        url is not null && Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static int Start(string arguments) => UnelevatedShell.TryStart(Explorer, $"\"{Explorer}\" {arguments}");
}
