using Microsoft.Win32;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Scanner.Inventory.Msi;

namespace Niskala.Scanner.Icons;

public enum IconSourceKind
{
    Resource,
    Image,
}

public sealed record IconSource(IconSourceKind Kind, string Path, int Index);

public sealed class IconSourceResolver(ICommandEnvironment environment, Func<Guid, InstallScope, string?> productIcon)
{
    public static IconSourceResolver ForCurrentMachine() => new(SystemCommandEnvironment.Instance, ReadProductIcon);

    public IconSource? Resolve(InstalledApp app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.Package?.LogoPath is { } logo)
        {
            return AppxLogoLocator.FindBest(logo) is { } best ? new IconSource(IconSourceKind.Image, best, 0) : null;
        }

        if (Existing(ParseIconLocation(app.DisplayIcon)) is { } display)
        {
            return display;
        }

        if (app.MsiProductCode is { } code && Existing(ParseIconLocation(productIcon(code, app.Scope))) is { } msi)
        {
            return msi;
        }

        return app.InstallerKind is not (InstallerKind.Msi or InstallerKind.WingetPortable or InstallerKind.Msix)
            && app.UninstallCommand is { ExecutableExists: true, Executable: { } executable }
                ? new IconSource(IconSourceKind.Resource, executable, 0)
                : null;
    }

    internal IconSource? ParseIconLocation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string text = environment.ExpandVariables(value.Trim());
        string path;
        string indexText = string.Empty;

        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            path = close < 0 ? text[1..] : text[1..close];
            indexText = close < 0 ? string.Empty : text[(close + 1)..].TrimStart().TrimStart(',');
        }
        else
        {
            int comma = text.LastIndexOf(',');
            bool hasIndex = comma > 0 && int.TryParse(text.AsSpan(comma + 1).Trim(), out _);
            path = hasIndex ? text[..comma] : text;
            indexText = hasIndex ? text[(comma + 1)..] : string.Empty;
        }

        path = path.Trim();
        int index = int.TryParse(indexText.Trim(), out int parsed) ? parsed : 0;
        IconSourceKind kind = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? IconSourceKind.Image : IconSourceKind.Resource;
        return path.Length == 0 ? null : new IconSource(kind, path, index);
    }

    private IconSource? Existing(IconSource? source) =>
        source is not null && Path.IsPathFullyQualified(source.Path) && environment.FileExists(source.Path) ? source : null;

    private static string? ReadProductIcon(Guid productCode, InstallScope scope)
    {
        (RegistryHive hive, string root) = scope == InstallScope.Machine
            ? (RegistryHive.LocalMachine, @"SOFTWARE\Classes\Installer\Products")
            : (RegistryHive.CurrentUser, @"Software\Microsoft\Installer\Products");
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey($@"{root}\{MsiPackedGuid.Pack(productCode)}");
            return key?.GetValue("ProductIcon") as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
