using System.Text.RegularExpressions;
using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Scanner.Inventory;
using Niskala.Scanner.Steam;

namespace Niskala.Scanner.Detection;

public sealed record InstallerDetection(InstallerKind Kind, IReadOnlyList<string> Evidence, Guid? MsiProductCode);

public sealed partial class InstallerKindDetector(IExecutableProbe probe)
{
    private static readonly string[] BurnValues = ["BundleUpgradeCode", "BundleProviderKey", "BundleCachePath"];

    public InstallerDetection Detect(RawUninstallEntry entry, ParsedCommand? uninstall)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string uninstallText = entry.GetString("UninstallString") ?? string.Empty;
        string exeName = uninstall?.Executable is { } exe ? Path.GetFileName(exe) : string.Empty;

        if (entry.GetFlag("WindowsInstaller"))
        {
            Guid? productCode = FindProductCode(entry, uninstall);
            List<string> evidence = ["WindowsInstaller=1"];
            if (entry.KeyName.StartsWith("InstallShield_", StringComparison.OrdinalIgnoreCase)
                || uninstallText.Contains("InstallShield", StringComparison.OrdinalIgnoreCase))
            {
                evidence.Add("InstallShield-authored MSI");
            }

            return Msi(evidence, productCode);
        }

        if (entry.GetString("WinGetPackageIdentifier") is { } wingetId
            && (string.Equals(entry.GetString("WinGetInstallerType"), "portable", StringComparison.OrdinalIgnoreCase)
                || uninstallText.StartsWith("winget ", StringComparison.OrdinalIgnoreCase)))
        {
            return new InstallerDetection(InstallerKind.WingetPortable, [$"WinGetPackageIdentifier {wingetId}", "portable package"], null);
        }

        if (SteamLibrary.AppId(entry.KeyName) is { } appId)
        {
            List<string> steam = [$"key name {SteamLibrary.KeyPrefix}{appId}"];
            if (uninstallText.Contains("steam://uninstall/", StringComparison.OrdinalIgnoreCase))
            {
                steam.Add("uninstalls through steam://uninstall");
            }

            return new InstallerDetection(InstallerKind.Steam, steam, null);
        }

        List<string> inno = [];
        if (entry.KeyName.EndsWith("_is1", StringComparison.OrdinalIgnoreCase))
        {
            inno.Add("key name ends with _is1");
        }

        if (entry.Has("Inno Setup: App Path") || entry.Has("Inno Setup: Setup Version"))
        {
            inno.Add("Inno Setup values present");
        }

        if (inno.Count > 0)
        {
            return new InstallerDetection(InstallerKind.InnoSetup, inno, null);
        }

        List<string> burn = [.. BurnValues.Where(entry.Has).Select(name => $"{name} value present")];
        if (burn.Count > 0)
        {
            return new InstallerDetection(InstallerKind.WixBurn, burn, null);
        }

        List<string> installShield = [];
        if (entry.KeyName.StartsWith("InstallShield_", StringComparison.OrdinalIgnoreCase))
        {
            installShield.Add("key name starts with InstallShield_");
        }

        if (uninstallText.Contains("InstallShield Installation Information", StringComparison.OrdinalIgnoreCase))
        {
            installShield.Add("uninstaller in InstallShield Installation Information");
        }

        if (uninstallText.Contains("-removeonly", StringComparison.OrdinalIgnoreCase))
        {
            installShield.Add("-removeonly switch");
        }

        if (installShield.Count > 0)
        {
            return new InstallerDetection(InstallerKind.InstallShield, installShield, null);
        }

        if (exeName.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            return Msi(["uninstall command runs msiexec"], FindProductCode(entry, uninstall));
        }

        if (exeName.Equals("Update.exe", StringComparison.OrdinalIgnoreCase)
            && (uninstall?.Arguments.Contains("--uninstall", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return new InstallerDetection(InstallerKind.Squirrel, ["Update.exe --uninstall"], null);
        }

        if (uninstall is { ExecutableExists: true, Executable: { } executable } && probe.ProbeNsis(executable) is { IsNsis: true } nsis)
        {
            List<string> evidence = [$"NSIS header at offset {nsis.HeaderOffset}"];
            if (nsis.IsUninstaller)
            {
                evidence.Add("NSIS uninstaller flag set");
            }

            return new InstallerDetection(InstallerKind.Nsis, evidence, null);
        }

        return uninstall is not null && uninstall.Issue != CommandParseIssue.Empty
            ? new InstallerDetection(InstallerKind.CustomExe, ["uninstall command with no known installer marker"], null)
            : new InstallerDetection(InstallerKind.Unknown, [], FindProductCode(entry, uninstall));
    }

    internal static Guid? FindProductCode(RawUninstallEntry entry, ParsedCommand? uninstall)
    {
        if (entry.KeyName.StartsWith('{') && Guid.TryParseExact(entry.KeyName, "B", out Guid fromKey))
        {
            return fromKey;
        }

        Match match = MsiExecProductCode().Match(uninstall?.Arguments ?? entry.GetString("UninstallString") ?? string.Empty);
        return match.Success && Guid.TryParseExact(match.Groups["code"].Value, "B", out Guid fromCommand) ? fromCommand : null;
    }

    private static InstallerDetection Msi(List<string> evidence, Guid? productCode)
    {
        if (productCode is { } code)
        {
            evidence.Add($"ProductCode {code:B}");
        }

        return new InstallerDetection(InstallerKind.Msi, evidence, productCode);
    }

    [GeneratedRegex(@"/[XIxi]\s*(?<code>\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\})", RegexOptions.CultureInvariant)]
    private static partial Regex MsiExecProductCode();
}
