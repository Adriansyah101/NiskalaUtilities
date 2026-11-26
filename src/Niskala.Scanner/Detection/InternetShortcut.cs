namespace Niskala.Scanner.Detection;

public sealed record InternetShortcut(string Url, string? IconFile)
{
    private const int MaxBytes = 64 * 1024;

    public static InternetShortcut? Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            if (new FileInfo(path).Length > MaxBytes)
            {
                return null;
            }

            string? url = null;
            string? icon = null;
            foreach (string line in File.ReadLines(path))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    url ??= line[4..].Trim();
                }
                else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase) && Path.IsPathFullyQualified(line[9..].Trim()))
                {
                    icon ??= Path.GetFullPath(line[9..].Trim());
                }
            }

            return url is null ? null : new InternetShortcut(url, icon);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public string? LocalPath =>
        Uri.TryCreate(Url, UriKind.Absolute, out Uri? uri) && uri.IsFile ? uri.LocalPath : null;
}
