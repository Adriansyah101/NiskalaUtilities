using System.IO.Enumeration;
using Niskala.Core.Models;
using Niskala.Core.Safety;
using Niskala.Scanner.Fingerprints;

namespace Niskala.Scanner.Leftovers;

public enum FileRootKind
{
    Other,
    Profile,
    Saves,
}

public sealed record FileSearchRoot(string Path, bool Shortcuts = false, FileRootKind Kind = FileRootKind.Other);

public sealed class FileLeftoverProbe(IReadOnlyList<FileSearchRoot> roots, ISystemTraceSource system, string? prefetch = null)
{
    private const int MaxMeasuredEntries = 200_000;

    private const int PrefetchNameLength = 29;
    private const int PrefetchHashLength = 8;

    private static readonly EnumerationOptions ChildOptions = new()
    {
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    private static readonly EnumerationOptions TreeOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };

    public static IReadOnlyList<FileSearchRoot> DefaultRoots(MachineLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var roots = new List<FileSearchRoot>();
        roots.AddRange(layout.ProgramFilesDirectories.Select(p => new FileSearchRoot(p)));
        roots.AddRange(layout.CommonProgramFilesDirectories.Select(p => new FileSearchRoot(p)));
        roots.Add(new FileSearchRoot(layout.ProgramDataDirectory));
        foreach (string profile in layout.UserProfiles)
        {
            roots.Add(new FileSearchRoot(Path.Combine(profile, @"AppData\Roaming"), Kind: FileRootKind.Profile));
            roots.Add(new FileSearchRoot(Path.Combine(profile, @"AppData\Local"), Kind: FileRootKind.Profile));
            roots.Add(new FileSearchRoot(Path.Combine(profile, @"AppData\LocalLow"), Kind: FileRootKind.Profile));
            roots.Add(new FileSearchRoot(Path.Combine(profile, @"AppData\Local\Programs")));
            roots.Add(new FileSearchRoot(Path.Combine(profile, @"Documents\My Games"), Kind: FileRootKind.Saves));
            roots.Add(new FileSearchRoot(Path.Combine(profile, "Saved Games"), Kind: FileRootKind.Saves));
        }

        roots.Add(new FileSearchRoot(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"), Kind: FileRootKind.Saves));

        roots.Add(new FileSearchRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), Shortcuts: true));
        roots.Add(new FileSearchRoot(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Shortcuts: true));
        roots.Add(new FileSearchRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Shortcuts: true));
        roots.Add(new FileSearchRoot(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Shortcuts: true));
        roots.Add(new FileSearchRoot(Path.GetTempPath().TrimEnd('\\')));
        return [.. roots.Where(r => r.Path.Length > 0).DistinctBy(r => r.Path, StringComparer.OrdinalIgnoreCase)];
    }

    public static (int Files, long Bytes) Measure(string folder)
    {
        int files = 0;
        long bytes = 0;
        var walk = new FileSystemEnumerable<long>(folder, static (ref FileSystemEntry entry) => entry.IsDirectory ? -1 : entry.Length, TreeOptions)
        {
            ShouldRecursePredicate = static (ref FileSystemEntry entry) => (entry.Attributes & FileAttributes.ReparsePoint) == 0,
        };

        foreach (long length in walk.Take(MaxMeasuredEntries))
        {
            if (length >= 0)
            {
                files++;
                bytes += length;
            }
        }

        return (files, bytes);
    }

    internal void Find(ScanContext context, CandidateSet candidates, CancellationToken cancellationToken)
    {
        foreach (FileSearchRoot root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? personal = root.Kind switch
            {
                FileRootKind.Saves => "below a save-game folder: may hold the player's saves",
                FileRootKind.Profile when context.App.InstallerKind == InstallerKind.Steam => "a game's profile folder: may hold the player's saves",
                _ => null,
            };
            foreach (Child first in Children(root.Path, root.Shortcuts))
            {
                NameMatch match = TokenNormalizer.MatchProduct(first.Name, context.Tokens);
                if (match.Kind == NameMatchKind.Exact && match.Token!.IsShort)
                {
                    continue;
                }

                bool publisher = match.Kind != NameMatchKind.Exact && TokenNormalizer.MatchesPublisher(first.Name, context.Tokens);
                if (match.Kind != NameMatchKind.None && !publisher)
                {
                    AddNamed(candidates, first, match, publisherDetail: null, personal);
                }
                else if (publisher && first.IsDirectory && !first.IsLink)
                {
                    foreach (Child second in Children(first.Path, root.Shortcuts))
                    {
                        NameMatch inner = TokenNormalizer.MatchProduct(second.Name, context.Tokens);
                        if (inner.Kind != NameMatchKind.None)
                        {
                            AddNamed(candidates, second, inner, publisherDetail: $@"{first.Name}\{second.Name}", personal);
                        }
                    }
                }
            }
        }

        AddFingerprintPaths(context, candidates);
        AddShortcuts(context, candidates);
        AddPrefetch(context, candidates);
    }

    private void AddPrefetch(ScanContext context, CandidateSet candidates)
    {
        if (prefetch is null || context.Fingerprint.ExecutableNames.Count == 0 || !Directory.Exists(prefetch))
        {
            return;
        }

        Dictionary<string, string> programs = context.Fingerprint.ExecutableNames
            .DistinctBy(PrefetchName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(PrefetchName, n => n, StringComparer.OrdinalIgnoreCase);
        foreach (string trace in Directory.EnumerateFiles(prefetch, "*.pf", ChildOptions))
        {
            string stem = Path.GetFileNameWithoutExtension(trace);
            int dash = stem.LastIndexOf('-');
            if (dash > 0 && stem.Length - dash - 1 == PrefetchHashLength && programs.TryGetValue(stem[..dash], out string? program))
            {
                candidates.Add(
                    LeftoverKind.File,
                    trace,
                    Path.GetFileNameWithoutExtension(program),
                    File.GetCreationTimeUtc(trace),
                    (LeftoverSignal.PointsToFingerprint, $"Prefetch trace of {program}, a program of the install folder"));
            }
        }
    }

    private static string PrefetchName(string program)
    {
        string name = program.ToUpperInvariant();
        return name.Length > PrefetchNameLength ? name[..PrefetchNameLength] : name;
    }

    private static void AddNamed(CandidateSet candidates, Child child, NameMatch match, string? publisherDetail, string? personal = null)
    {
        LeftoverKind kind = child.IsDirectory ? LeftoverKind.Folder : LeftoverKind.File;
        (LeftoverSignal, string) name = match.Kind == NameMatchKind.Exact
            ? (LeftoverSignal.ExactName, $"\"{child.Name}\" is the product name \"{match.Token!.Text}\"")
            : (LeftoverSignal.PartialName, $"\"{child.Name}\" only contains \"{match.Token!.Text}\"");
        if (publisherDetail is null)
        {
            candidates.Add(kind, child.Path, child.Name, child.Created, name);
        }
        else
        {
            candidates.Add(kind, child.Path, child.Name, child.Created, name, (LeftoverSignal.PublisherAndProduct, publisherDetail));
        }

        if (personal is not null)
        {
            candidates.MarkPersonal(kind, child.Path, personal);
        }
    }

    private static void AddFingerprintPaths(ScanContext context, CandidateSet candidates)
    {
        if (context.Fingerprint.Roots.Count > 0 && context.Fingerprint.Roots[^1] is { } root && Directory.Exists(root))
        {
            Child folder = new(root, Path.GetFileName(root), true, false, Directory.GetCreationTimeUtc(root));
            candidates.Add(LeftoverKind.Folder, root, folder.Name, folder.Created, (LeftoverSignal.InFingerprint, "install folder recorded before the uninstall"));

            if (TokenNormalizer.MatchProduct(folder.Name, context.Tokens) is { Kind: NameMatchKind.Exact } match)
            {
                AddNamed(candidates, folder, match, publisherDetail: null);
            }
        }

        foreach (ShortcutTrace shortcut in context.Fingerprint.Shortcuts)
        {
            if (File.Exists(shortcut.LinkPath))
            {
                candidates.Add(
                    LeftoverKind.File,
                    shortcut.LinkPath,
                    Path.GetFileNameWithoutExtension(shortcut.LinkPath),
                    File.GetCreationTimeUtc(shortcut.LinkPath),
                    (LeftoverSignal.InFingerprint, "shortcut recorded before the uninstall"));
            }
        }
    }

    private void AddShortcuts(ScanContext context, CandidateSet candidates)
    {
        Guid? productCode = context.Fingerprint.MsiProductCode;
        if (context.Fingerprint.Roots.Count == 0 && productCode is null)
        {
            return;
        }

        foreach (ShortcutTrace shortcut in system.Shortcuts())
        {
            string name = Path.GetFileNameWithoutExtension(shortcut.LinkPath);
            DateTime created = File.GetCreationTimeUtc(shortcut.LinkPath);
            if (productCode is not null && shortcut.MsiProductCode == productCode)
            {
                candidates.Add(LeftoverKind.File, shortcut.LinkPath, name, created, (LeftoverSignal.InstallerIdentity, "advertised shortcut of the product"));
            }
            else if (context.Owner.OwnsPath(shortcut.Target) || context.Owner.Mentions(shortcut.Arguments))
            {
                candidates.Add(LeftoverKind.File, shortcut.LinkPath, name, created, (LeftoverSignal.PointsToFingerprint, $"points into the install folder: {shortcut.Target} {shortcut.Arguments}".TrimEnd()));
            }
        }
    }

    private static IEnumerable<Child> Children(string folder, bool shortcuts)
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return new FileSystemEnumerable<Child>(
            folder,
            static (ref FileSystemEntry entry) => new Child(
                entry.ToFullPath(),
                entry.IsDirectory ? entry.FileName.ToString() : Path.GetFileNameWithoutExtension(entry.FileName.ToString()),
                entry.IsDirectory,
                (entry.Attributes & FileAttributes.ReparsePoint) != 0,
                entry.CreationTimeUtc.UtcDateTime),
            ChildOptions)
        {
            ShouldIncludePredicate = shortcuts
                ? static (ref FileSystemEntry entry) => entry.IsDirectory || entry.FileName.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                : static (ref FileSystemEntry entry) => entry.IsDirectory,
        };
    }

    private readonly record struct Child(string Path, string Name, bool IsDirectory, bool IsLink, DateTime Created);
}
