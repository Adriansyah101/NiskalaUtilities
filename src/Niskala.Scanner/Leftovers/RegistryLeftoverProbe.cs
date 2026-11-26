using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Commands;
using Niskala.Core.Safety.Registry;
using Niskala.Interop.Registry;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Inventory.Msi;

namespace Niskala.Scanner.Leftovers;

public sealed class RegistryLeftoverProbe(ISystemTraceSource system, ILogger<RegistryLeftoverProbe> logger)
{
    private const string CurrentVersion = @"SOFTWARE\Microsoft\Windows\CurrentVersion";

    private static readonly string[] SoftwareRoots = [@"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node", @"HKCU\Software"];

    private static readonly string[] SkippedSoftwareKeys = ["Classes", "WOW6432Node", "Microsoft", "Policies"];

    private static readonly string[] ClassesRoots =
    [
        @"HKLM\SOFTWARE\Classes", @"HKLM\SOFTWARE\WOW6432Node\Classes", @"HKCU\Software\Classes", @"HKCU\Software\Classes\WOW6432Node",
    ];

    private static readonly string[] ComServers = ["InprocServer32", "LocalServer32", "InprocHandler32"];

    private static readonly string[] ShellClasses = ["*", "AllFilesystemObjects", "Directory", @"Directory\Background", "Folder", "Drive"];

    private static readonly string[] ShellHandlers = ["ContextMenuHandlers", "DragDropHandlers", "PropertySheetHandlers", "CopyHookHandlers", "ColumnHandlers"];

    private const string AppCompat = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags";

    private static readonly string[] OwnClassesRoots = [@"HKLM\SOFTWARE\Classes", @"HKCU\Software\Classes"];

    private static readonly string[] SharedClasses =
    [
        "CLSID", "TypeLib", "AppID", "Interface", "Installer", "Local Settings", "WOW6432Node", "Applications", "SystemFileAssociations",
        "*", "AllFilesystemObjects", "Directory", "Folder", "Drive", "Unknown", "exefile", "lnkfile",
    ];

    private static readonly (string Key, string Detail)[] PathNamedValues =
    [
        ($@"HKCU\{AppCompat}\Compatibility Assistant\Store", "Program Compatibility Assistant record of a program in the install folder"),
        ($@"HKCU\{AppCompat}\Layers", "compatibility mode set for a program in the install folder"),
        ($@"HKLM\{AppCompat}\Layers", "compatibility mode set for a program in the install folder"),
        (@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache", "shell display name cached for a program in the install folder"),
    ];

    private static readonly string[] UninstallRoots =
    [
        $@"HKLM\{CurrentVersion}\Uninstall", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall",
    ];

    internal void Find(ScanContext context, CandidateSet candidates, List<string> notes, CancellationToken cancellationToken)
    {
        Software(context, candidates);
        cancellationToken.ThrowIfCancellationRequested();
        OwnEntry(context, candidates);
        WindowsInstaller(context, candidates, notes);
        cancellationToken.ThrowIfCancellationRequested();
        Traces(context, candidates);
        if (context.Fingerprint.Roots.Count > 0)
        {
            PathLists(context, candidates);
            cancellationToken.ThrowIfCancellationRequested();
            FileTypes(context, candidates, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Com(context, candidates, cancellationToken);
        }
    }

    private void Software(ScanContext context, CandidateSet candidates)
    {
        foreach (string root in SoftwareRoots)
        {
            foreach (string name in SubKeyNames(root).Where(n => !SkippedSoftwareKeys.Contains(n, StringComparer.OrdinalIgnoreCase)))
            {
                string path = $@"{root}\{name}";
                NameMatch match = TokenNormalizer.MatchProduct(name, context.Tokens);
                if (match.Kind == NameMatchKind.Exact && match.Token!.IsShort)
                {
                    continue;
                }

                bool publisher = match.Kind != NameMatchKind.Exact && TokenNormalizer.MatchesPublisher(name, context.Tokens);
                if (match.Kind != NameMatchKind.None && !publisher)
                {
                    candidates.Add(LeftoverKind.RegistryKey, path, name, LastWrite(path), NameEvidence(name, match));
                }
                else if (publisher)
                {
                    foreach (string inner in SubKeyNames(path))
                    {
                        NameMatch innerMatch = TokenNormalizer.MatchProduct(inner, context.Tokens);
                        if (innerMatch.Kind != NameMatchKind.None)
                        {
                            string innerPath = $@"{path}\{inner}";
                            candidates.Add(
                                LeftoverKind.RegistryKey,
                                innerPath,
                                inner,
                                LastWrite(innerPath),
                                NameEvidence(inner, innerMatch),
                                (LeftoverSignal.PublisherAndProduct, $@"{name}\{inner}"));
                        }
                    }
                }
            }
        }
    }

    private void OwnEntry(ScanContext context, CandidateSet candidates)
    {
        if (context.App.RegistryKey is not { } entry)
        {
            return;
        }

        Read(entry, key =>
        {
            string name = entry[(entry.LastIndexOf('\\') + 1)..];
            candidates.Add(LeftoverKind.RegistryKey, entry, null, name, LastWrite(entry), LeftoverSignal.InstallerIdentity, "the application's own Uninstall entry");
            foreach (string value in (string[])["UninstallString", "QuietUninstallString", "DisplayIcon", "InstallLocation"])
            {
                if (key.GetValue(value, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string data && context.Owner.OwnsCommand(data))
                {
                    candidates.Add(LeftoverKind.RegistryKey, entry, null, name, null, LeftoverSignal.PointsToFingerprint, $"{value} = {data}");
                    break;
                }
            }
        });
    }

    private void WindowsInstaller(ScanContext context, CandidateSet candidates, List<string> notes)
    {
        if ((context.Fingerprint.MsiProductCode ?? context.App.MsiProductCode) is not { } code)
        {
            return;
        }

        if (system.IsProductRegistered(code) != false)
        {
            notes.Add("Windows Installer still lists the product, so its registration is not treated as a leftover.");
            return;
        }

        string packed = MsiPackedGuid.Pack(code);
        string braced = code.ToString("B").ToUpperInvariant();
        string detail = $"named after the ProductCode {braced}";
        string packedDetail = $"named after the packed ProductCode {packed}";

        foreach (string uninstall in UninstallRoots)
        {
            AddIfExists($@"{uninstall}\{braced}", detail);
        }

        foreach (string branch in (string[])["Products", "Features"])
        {
            AddIfExists($@"HKLM\SOFTWARE\Classes\Installer\{branch}\{packed}", packedDetail);
            AddIfExists($@"HKCU\Software\Microsoft\Installer\{branch}\{packed}", packedDetail);
        }

        string userData = $@"HKLM\{CurrentVersion}\Installer\UserData";
        foreach (string account in SubKeyNames(userData))
        {
            AddIfExists($@"{userData}\{account}\Products\{packed}", packedDetail);
        }

        if (context.Fingerprint.MsiUpgradeCode is { } upgrade)
        {
            string packedUpgrade = MsiPackedGuid.Pack(upgrade);
            foreach (string root in (string[])[@"HKLM\SOFTWARE\Classes\Installer\UpgradeCodes", @"HKCU\Software\Microsoft\Installer\UpgradeCodes"])
            {
                string path = $@"{root}\{packedUpgrade}";
                Read(path, key =>
                {
                    string[] products = key.GetValueNames();
                    string upgradeDetail = $"UpgradeCode {upgrade.ToString("B").ToUpperInvariant()} lists only this product";
                    if (products.All(p => p.Length == 0 || p.Equals(packed, StringComparison.OrdinalIgnoreCase)))
                    {
                        candidates.Add(LeftoverKind.RegistryKey, path, null, null, null, LeftoverSignal.InstallerIdentity, upgradeDetail);
                    }
                    else if (products.Contains(packed, StringComparer.OrdinalIgnoreCase))
                    {
                        candidates.Add(LeftoverKind.RegistryValue, path, packed, null, null, LeftoverSignal.InstallerIdentity, packedDetail);
                    }
                });
            }
        }

        void AddIfExists(string path, string evidence) =>
            Read(path, _ => candidates.Add(LeftoverKind.RegistryKey, path, null, null, LastWrite(path), LeftoverSignal.InstallerIdentity, evidence));
    }

    private void Traces(ScanContext context, CandidateSet candidates)
    {
        Fingerprint fingerprint = context.Fingerprint;
        foreach (ServiceTrace service in system.Services())
        {
            bool recorded = fingerprint.Services.Any(s => s.Name.Equals(service.Name, StringComparison.OrdinalIgnoreCase));
            bool owned = context.Owner.OwnsPath(service.ImagePath) || context.Owner.OwnsPath(service.ServiceDll);
            string path = $@"HKLM\SYSTEM\CurrentControlSet\Services\{service.Name}";
            string image = service.ServiceDll ?? service.ImagePath;
            if (recorded)
            {
                candidates.Add(LeftoverKind.RegistryKey, path, null, service.Name, null, LeftoverSignal.InFingerprint, "service recorded before the uninstall", image);
            }

            if (owned)
            {
                candidates.Add(LeftoverKind.RegistryKey, path, null, service.Name, null, LeftoverSignal.PointsToFingerprint, $"service image {image}", image);
            }
        }

        foreach (RegistryValueTrace run in system.RunEntries())
        {
            bool recorded = fingerprint.RunEntries.Any(r => SameValue(r, run));
            if (recorded)
            {
                candidates.Add(LeftoverKind.RegistryValue, run.Key, run.ValueName ?? string.Empty, null, null, LeftoverSignal.InFingerprint, "autostart entry recorded before the uninstall");
            }

            if (context.Owner.OwnsCommand(run.Data))
            {
                candidates.Add(LeftoverKind.RegistryValue, run.Key, run.ValueName ?? string.Empty, null, null, LeftoverSignal.PointsToFingerprint, $"runs {run.Data}");
            }
        }

        foreach (RegistryValueTrace appPath in system.AppPaths().Where(v => v.ValueName is null))
        {
            string name = appPath.Key[(appPath.Key.LastIndexOf('\\') + 1)..];
            if (fingerprint.AppPaths.Any(a => a.Key.Equals(appPath.Key, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(LeftoverKind.RegistryKey, appPath.Key, null, name, null, LeftoverSignal.InFingerprint, "App Paths entry recorded before the uninstall");
            }

            if (context.Owner.OwnsCommand(appPath.Data))
            {
                candidates.Add(LeftoverKind.RegistryKey, appPath.Key, null, name, null, LeftoverSignal.PointsToFingerprint, $"App Paths entry for {appPath.Data}");
            }
        }
    }

    private void PathLists(ScanContext context, CandidateSet candidates)
    {
        foreach ((string key, string detail) in (ReadOnlySpan<(string, string)>)
        [
            ($@"HKLM\{CurrentVersion}\SharedDLLs", "shared DLL count for a file in the install folder"),
            (@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\SharedDLLs", "shared DLL count for a file in the install folder"),
            ($@"HKLM\{CurrentVersion}\Installer\Folders", "Windows Installer reference to a folder in the install folder"),
        ])
        {
            Read(key, root =>
            {
                foreach (string name in root.GetValueNames().Where(context.Owner.OwnsPath))
                {
                    candidates.Add(LeftoverKind.RegistryValue, key, name, null, null, LeftoverSignal.InFingerprint, detail);
                }
            });
        }

        foreach ((string key, string detail) in PathNamedValues)
        {
            Read(key, root =>
            {
                foreach (string name in root.GetValueNames().Where(context.Owner.Mentions))
                {
                    candidates.Add(LeftoverKind.RegistryValue, key, name, null, null, LeftoverSignal.PointsToFingerprint, detail);
                }
            });
        }
    }

    private void FileTypes(ScanContext context, CandidateSet candidates, CancellationToken cancellationToken)
    {
        foreach (string root in OwnClassesRoots)
        {
            Read(root, classes =>
            {
                var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var extensions = new List<string>();
                foreach (string name in classes.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (name.StartsWith('.'))
                    {
                        extensions.Add(name);
                    }
                    else if (!SharedClasses.Contains(name, StringComparer.OrdinalIgnoreCase) && OwnedHandler(context, classes.Handle, name) is { } target)
                    {
                        owned.Add(name);
                        string kind = RegistryNative.TryReadString(classes.Handle, name, "URL Protocol") is null ? "ProgID" : "URL protocol";
                        candidates.Add(LeftoverKind.RegistryKey, $@"{root}\{name}", null, name, null, LeftoverSignal.PointsToFingerprint, $"{kind} {name} runs {target}", target);
                    }
                }

                Read($@"{root}\Applications", applications =>
                {
                    foreach (string name in applications.GetSubKeyNames())
                    {
                        if (OwnedHandler(context, applications.Handle, name) is { } target)
                        {
                            candidates.Add(LeftoverKind.RegistryKey, $@"{root}\Applications\{name}", null, name, null, LeftoverSignal.PointsToFingerprint, $"Open with entry for {target}", target);
                        }
                    }
                });

                if (owned.Count > 0)
                {
                    Associations(root, classes, extensions, owned, candidates);
                }
            });
        }
    }

    private void Associations(string root, RegistryKey classes, List<string> extensions, HashSet<string> owned, CandidateSet candidates)
    {
        foreach (string extension in extensions)
        {
            try
            {
                using RegistryKey? key = classes.OpenSubKey(extension);
                if (key is null)
                {
                    continue;
                }

                string path = $@"{root}\{extension}";
                if (key.GetValue(null) is string progId && owned.Contains(progId))
                {
                    candidates.Add(LeftoverKind.RegistryValue, path, string.Empty, null, null, LeftoverSignal.PointsToFingerprint, $"{extension} files open with {progId}");
                }

                using RegistryKey? openWith = key.OpenSubKey("OpenWithProgids");
                foreach (string name in openWith?.GetValueNames().Where(owned.Contains) ?? [])
                {
                    candidates.Add(LeftoverKind.RegistryValue, $@"{path}\OpenWithProgids", name, null, null, LeftoverSignal.PointsToFingerprint, $"{extension} files offer {name}");
                }
            }
            catch (Exception ex) when (IsAccessError(ex))
            {
                ScannerLog.TraceKeyUnreadable(logger, $@"{root}\{extension}", ex);
            }
        }
    }

    private static string? OwnedHandler(ScanContext context, SafeRegistryHandle parent, string name)
    {
        string? command = RegistryNative.TryReadString(parent, $@"{name}\shell\open\command", null);
        if (context.Owner.OwnsCommand(command))
        {
            return CommandLineParser.Parse(command!, context.Environment).Executable ?? command;
        }

        string? icon = RegistryNative.TryReadString(parent, $@"{name}\DefaultIcon", null);
        return context.Owner.Mentions(icon) ? icon : null;
    }

    private void Com(ScanContext context, CandidateSet candidates, CancellationToken cancellationToken)
    {
        var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in ClassesRoots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Read($@"{root}\CLSID", clsidRoot =>
            {
                foreach (string clsid in clsidRoot.GetSubKeyNames())
                {
                    ComClass(context, candidates, root, clsidRoot, clsid, classes);
                }
            });

            Read($@"{root}\TypeLib", typeLibs =>
            {
                foreach (string library in typeLibs.GetSubKeyNames())
                {
                    if (TypeLibFile(typeLibs, library, context.Owner) is { } file)
                    {
                        candidates.Add(LeftoverKind.RegistryKey, $@"{root}\TypeLib\{library}", null, null, null, LeftoverSignal.PointsToFingerprint, $"type library {file}", file);
                    }
                }
            });
        }

        if (classes.Count > 0)
        {
            ShellRegistrations(context, candidates, classes);
        }
    }

    private void ComClass(ScanContext context, CandidateSet candidates, string root, RegistryKey clsidRoot, string clsid, HashSet<string> classes)
    {
        try
        {
            string? server = null;
            foreach (string kind in ComServers)
            {
                if (RegistryNative.TryReadString(clsidRoot.Handle, $@"{clsid}\{kind}", null) is { } value && context.Owner.Mentions(value))
                {
                    server = value;
                    break;
                }
            }

            if (server is null)
            {
                return;
            }

            using RegistryKey? key = clsidRoot.OpenSubKey(clsid);
            if (key is null)
            {
                return;
            }

            classes.Add(clsid);
            string clsidPath = $@"{root}\CLSID\{clsid}";
            string evidence = $"COM class {clsid} served by {server}";
            candidates.Add(LeftoverKind.RegistryKey, clsidPath, null, null, null, LeftoverSignal.PointsToFingerprint, evidence, CommandLineParser.Parse(server, context.Environment).Executable);

            foreach (string progIdKind in (string[])["ProgID", "VersionIndependentProgID"])
            {
                using RegistryKey? progIdKey = key.OpenSubKey(progIdKind);
                if (progIdKey?.GetValue(null) is string { Length: > 0 } progId)
                {
                    string progIdPath = $@"{root}\{progId}";
                    Read($@"{progIdPath}\CLSID", link =>
                    {
                        if (string.Equals(link.GetValue(null) as string, clsid, StringComparison.OrdinalIgnoreCase))
                        {
                            candidates.Add(LeftoverKind.RegistryKey, progIdPath, null, progId, null, LeftoverSignal.PointsToFingerprint, $"ProgID of {evidence}");
                        }
                    });
                }
            }

            if (key.GetValue("AppID") is string { Length: > 0 } appId && !appId.Equals(clsid, StringComparison.OrdinalIgnoreCase))
            {
                string appIdPath = $@"{root}\AppID\{appId}";
                Read(appIdPath, _ => candidates.Add(LeftoverKind.RegistryKey, appIdPath, null, null, null, LeftoverSignal.PointsToFingerprint, $"AppID of {evidence}"));
            }
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            ScannerLog.TraceKeyUnreadable(logger, $@"{root}\CLSID\{clsid}", ex);
        }
    }

    private static string? TypeLibFile(RegistryKey typeLibs, string library, OwnershipMatcher owner)
    {
        try
        {
            using RegistryKey? libraryKey = typeLibs.OpenSubKey(library);
            foreach (string version in libraryKey?.GetSubKeyNames() ?? [])
            {
                using RegistryKey? versionKey = libraryKey!.OpenSubKey(version);
                foreach (string locale in versionKey?.GetSubKeyNames() ?? [])
                {
                    using RegistryKey? localeKey = versionKey!.OpenSubKey(locale);
                    foreach (string platform in (string[])["win32", "win64"])
                    {
                        using RegistryKey? platformKey = localeKey?.OpenSubKey(platform);
                        if (platformKey?.GetValue(null, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string file && owner.OwnsPath(file))
                        {
                            return file;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            return null;
        }

        return null;
    }

    private void ShellRegistrations(ScanContext context, CandidateSet candidates, HashSet<string> classes)
    {
        foreach (string overlays in (string[])[$@"HKLM\{CurrentVersion}\Explorer\ShellIconOverlayIdentifiers", @"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers"])
        {
            HandlerKeys(context, candidates, overlays, classes, "icon overlay handler");
        }

        foreach (string root in ClassesRoots)
        {
            foreach (string shellClass in ShellClasses)
            {
                foreach (string handler in ShellHandlers)
                {
                    HandlerKeys(context, candidates, $@"{root}\{shellClass}\shellex\{handler}", classes, $"{shellClass} {handler}");
                }
            }
        }

        foreach (string approved in (string[])[$@"HKLM\{CurrentVersion}\Shell Extensions\Approved", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved"])
        {
            Read(approved, key =>
            {
                foreach (string name in key.GetValueNames().Where(classes.Contains))
                {
                    candidates.Add(LeftoverKind.RegistryValue, approved, name, null, null, LeftoverSignal.PointsToFingerprint, $"approval of shell extension {name}");
                }
            });
        }
    }

    private void HandlerKeys(ScanContext context, CandidateSet candidates, string parent, HashSet<string> classes, string detail)
    {
        Read(parent, key =>
        {
            foreach (string name in key.GetSubKeyNames())
            {
                using RegistryKey? handler = key.OpenSubKey(name);
                string? clsid = classes.Contains(name) ? name : handler?.GetValue(null) as string;
                if (clsid is not null && classes.Contains(clsid))
                {
                    string path = $@"{parent}\{name}";
                    candidates.Add(LeftoverKind.RegistryKey, path, null, name, null, LeftoverSignal.PointsToFingerprint, $"{detail} for {clsid}");
                    if (TokenNormalizer.MatchProduct(name, context.Tokens) is { Kind: NameMatchKind.Exact } match)
                    {
                        candidates.Add(LeftoverKind.RegistryKey, path, name, null, NameEvidence(name, match));
                    }
                }
            }
        });
    }

    private static bool SameValue(RegistryValueTrace a, RegistryValueTrace b) =>
        a.Key.Equals(b.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(a.ValueName, b.ValueName, StringComparison.OrdinalIgnoreCase);

    private static (LeftoverSignal, string) NameEvidence(string name, NameMatch match) => match.Kind == NameMatchKind.Exact
        ? (LeftoverSignal.ExactName, $"\"{name}\" is the product name \"{match.Token!.Text}\"")
        : (LeftoverSignal.PartialName, $"\"{name}\" only contains \"{match.Token!.Text}\"");

    private string[] SubKeyNames(string path)
    {
        string[] names = [];
        Read(path, key => names = key.GetSubKeyNames());
        return names;
    }

    private DateTime? LastWrite(string path)
    {
        DateTime? written = null;
        Read(path, key => written = RegistryNative.TryGetLastWriteTime(key.Handle, out DateTime value) == 0 ? value : null);
        return written;
    }

    private void Read(string path, Action<RegistryKey> read)
    {
        try
        {
            using RegistryKey? key = RegistryPath.OpenReadOnly(path);
            if (key is not null)
            {
                read(key);
            }
        }
        catch (Exception ex) when (IsAccessError(ex))
        {
            ScannerLog.TraceKeyUnreadable(logger, path, ex);
        }
    }

    private static bool IsAccessError(Exception ex) =>
        ex is System.Security.SecurityException or UnauthorizedAccessException or IOException;
}
