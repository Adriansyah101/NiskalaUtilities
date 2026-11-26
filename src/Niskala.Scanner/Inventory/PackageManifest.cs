using System.Globalization;
using System.Xml;

namespace Niskala.Scanner.Inventory;

internal sealed record PackageManifest(bool AllowsExternalContent, string? AppListLogo, string? LogoBackground)
{
    public static readonly PackageManifest Empty = new(false, null, null);

    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
    };

    public static PackageManifest Read(string packageFolder)
    {
        try
        {
            using XmlReader reader = XmlReader.Create(Path.Combine(packageFolder, "AppxManifest.xml"), Settings);
            bool external = false;
            string? logo = null;
            string? background = null;

            reader.MoveToContent();
            while (!reader.EOF)
            {
                if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "AllowExternalContent")
                {
                    external = string.Equals(reader.ReadElementContentAsString().Trim(), "true", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (logo is null && reader.NodeType == XmlNodeType.Element && reader.LocalName == "VisualElements")
                {
                    logo = reader.GetAttribute("Square44x44Logo");
                    background = NormalizeColor(reader.GetAttribute("BackgroundColor"));
                }

                reader.Read();
            }

            return new PackageManifest(external, logo is null ? null : Path.Combine(packageFolder, logo), background);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return Empty;
        }
    }

    internal static string? NormalizeColor(string? value)
    {
        string text = value?.Trim() ?? string.Empty;
        if (text.Length == 7 && text[0] == '#' && int.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
        {
            return text.ToUpperInvariant();
        }

        return text.Length == 0 || text.Equals("transparent", StringComparison.OrdinalIgnoreCase) || !text.All(char.IsAsciiLetter)
            ? null
            : text.ToLowerInvariant();
    }
}
