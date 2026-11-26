using Niskala.Core.Commands;
using Niskala.Core.Models;
using Niskala.Scanner.Detection;

namespace Niskala.Scanner.Inventory;

public sealed class UninstallEntryMapper(InstallerKindDetector detector, ICommandEnvironment environment, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public InstalledApp Map(RawUninstallEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        string? displayName = entry.GetString("DisplayName");
        string? uninstallString = entry.GetString("UninstallString");
        string? quietUninstallString = entry.GetString("QuietUninstallString");
        ParsedCommand? uninstall = uninstallString is null ? null : CommandLineParser.Parse(uninstallString, environment);
        ParsedCommand? quiet = quietUninstallString is null ? null : CommandLineParser.Parse(quietUninstallString, environment);
        ParsedCommand? primary = uninstall ?? quiet;

        InstallerDetection detection = detector.Detect(entry, primary);
        (string? location, InstallLocationOrigin locationOrigin) = ReadInstallLocation(entry);
        (DateOnly? installDate, InstallDateOrigin dateOrigin) = ReadInstallDate(entry);
        long? sizeKb = entry.GetNumber("EstimatedSize");
        bool systemComponent = entry.GetFlag("SystemComponent");
        string? parentKeyName = entry.GetString("ParentKeyName");
        string? releaseType = entry.GetString("ReleaseType");

        return new InstalledApp
        {
            Id = BuildId(entry),
            Sources = AppSources.Registry,
            Scope = entry.Scope,
            View = entry.View,
            RegistryKey = entry.KeyPath,
            KeyName = entry.KeyName,
            KeyLastWriteUtc = entry.LastWriteTimeUtc,
            DisplayName = displayName,
            DisplayVersion = entry.GetString("DisplayVersion"),
            Publisher = entry.GetString("Publisher"),
            InstallLocation = location,
            InstallLocationOrigin = locationOrigin,
            InstallDate = installDate,
            InstallDateOrigin = dateOrigin,
            EstimatedSizeBytes = sizeKb is > 0 ? sizeKb * 1024 : null,
            DisplayIcon = entry.GetString("DisplayIcon"),
            UninstallString = uninstallString,
            QuietUninstallString = quietUninstallString,
            ModifyPath = entry.GetString("ModifyPath"),
            UninstallCommand = uninstall,
            QuietUninstallCommand = quiet,
            InstallerKind = detection.Kind,
            DetectionEvidence = detection.Evidence,
            MsiProductCode = detection.MsiProductCode,
            NoRemove = entry.GetFlag("NoRemove"),
            NoModify = entry.GetFlag("NoModify"),
            NoRepair = entry.GetFlag("NoRepair"),
            SystemComponent = systemComponent,
            IsOrphan = primary is null && detection.Kind != InstallerKind.Msi,
            IsUninstallerMissing = detection.Kind != InstallerKind.WingetPortable
                && primary is { ExecutableExists: false, Issue: not CommandParseIssue.Empty },
            ParentKeyName = parentKeyName,
            ReleaseType = releaseType,
            UrlInfoAbout = entry.GetString("URLInfoAbout"),
            HelpLink = entry.GetString("HelpLink"),
            HiddenReason = GetHiddenReason(displayName, systemComponent, parentKeyName, releaseType),
        };
    }

    internal static string BuildId(RawUninstallEntry entry)
    {
        string location = (entry.Scope, entry.View) switch
        {
            (InstallScope.Machine, RegistryViewKind.Registry32) => "hklm32",
            (InstallScope.Machine, _) => "hklm64",
            _ => "hkcu",
        };

        return $"reg:{location}:{entry.KeyName}";
    }

    internal static HiddenReason GetHiddenReason(string? displayName, bool systemComponent, string? parentKeyName, string? releaseType)
    {
        if (displayName is null)
        {
            return HiddenReason.NoDisplayName;
        }

        if (systemComponent)
        {
            return HiddenReason.SystemComponent;
        }

        if (parentKeyName is not null)
        {
            return HiddenReason.ChildEntry;
        }

        return releaseType is not null
            && (releaseType.Contains("Update", StringComparison.OrdinalIgnoreCase)
                || releaseType.Contains("Hotfix", StringComparison.OrdinalIgnoreCase)
                || releaseType.Contains("Service Pack", StringComparison.OrdinalIgnoreCase))
            ? HiddenReason.Update
            : HiddenReason.None;
    }

    internal string? CleanPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = environment.ExpandVariables(value.Trim().Trim('"').Trim()).TrimEnd('\\', '/');
        if (trimmed.Length == 2 && trimmed[1] == ':')
        {
            trimmed += "\\";
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    private (string? Location, InstallLocationOrigin Origin) ReadInstallLocation(RawUninstallEntry entry)
    {
        if (CleanPath(entry.GetString("InstallLocation")) is { } location)
        {
            return (location, InstallLocationOrigin.Registry);
        }

        return CleanPath(entry.GetString("Inno Setup: App Path")) is { } innoPath
            ? (innoPath, InstallLocationOrigin.InnoAppPath)
            : (null, InstallLocationOrigin.None);
    }

    private (DateOnly? Date, InstallDateOrigin Origin) ReadInstallDate(RawUninstallEntry entry)
    {
        DateOnly today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        if (InstallDates.Parse(entry.GetString("InstallDate"), today) is { } date)
        {
            return (date, InstallDateOrigin.Registry);
        }

        return entry.LastWriteTimeUtc is { } written
            ? (DateOnly.FromDateTime(written.ToLocalTime()), InstallDateOrigin.KeyLastWrite)
            : (null, InstallDateOrigin.None);
    }
}
