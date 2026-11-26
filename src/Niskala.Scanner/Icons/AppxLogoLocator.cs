using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace Niskala.Scanner.Icons;

public static partial class AppxLogoLocator
{
    public static string? FindBest(string logoPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(logoPath);
        string? directory = Path.GetDirectoryName(logoPath);
        if (directory is null || !Directory.Exists(directory))
        {
            return null;
        }

        string name = Qualifiers().Replace(Path.GetFileNameWithoutExtension(logoPath), string.Empty);
        string extension = Path.GetExtension(logoPath);

        return Directory.EnumerateFiles(directory, $"{name}*{extension}")
            .Where(file => IsVariantOf(Path.GetFileNameWithoutExtension(file), name))
            .Select(file => (File: file, Width: PngWidth(file)))
            .Where(candidate => candidate.Width > 0)
            .OrderByDescending(candidate => candidate.Width)
            .Select(candidate => candidate.File)
            .FirstOrDefault();
    }

    internal static int PngWidth(string file)
    {
        try
        {
            Span<byte> header = stackalloc byte[24];
            using FileStream stream = File.OpenRead(file);
            return stream.Read(header) == header.Length && header[..8].SequenceEqual(PngEncoder.Signature)
                ? BinaryPrimitives.ReadInt32BigEndian(header[16..20])
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static bool IsVariantOf(string fileName, string name) =>
        !fileName.Contains("contrast-", StringComparison.OrdinalIgnoreCase)
        && string.Equals(Qualifiers().Replace(fileName, string.Empty), name, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\.(scale|targetsize|contrast|altform|theme|lang|dxfeaturelevel)-[^.]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Qualifiers();
}
