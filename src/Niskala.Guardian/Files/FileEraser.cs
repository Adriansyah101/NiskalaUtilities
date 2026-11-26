using System.Globalization;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Safety;
using Niskala.Interop.FileSystem;
using Niskala.Interop.Security;
using Niskala.Scanner.Leftovers;

namespace Niskala.Guardian.Files;

public enum FileCheck
{
    Allowed,
    Missing,
    Protected,
    Changed,
    TrustedInstallerOwned,
    MicrosoftSigned,
    Locked,
    Failed,
}

public sealed class CheckedEntry(SafeFileHandle handle, OpenedEntry entry, string path) : IDisposable
{
    public SafeFileHandle Handle { get; } = handle;

    public OpenedEntry Entry { get; } = entry;

    public string Path { get; } = path;

    public void Dispose() => Handle.Dispose();
}

public readonly record struct EraseOutcome(int Deleted, int Scheduled, int Kept, string? Problem);

public sealed class FileEraser(PathGuard guard)
{
    private enum State
    {
        Gone,
        Scheduled,
        Kept,
    }

    public FileCheck Check(string path, bool expectDirectory, out CheckedEntry? entry, out string detail)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        entry = null;
        PathDecision decision = guard.Check(path);
        if (!decision.IsAllowed)
        {
            detail = decision.ToString();
            return FileCheck.Protected;
        }

        if (!decision.Exists)
        {
            detail = "already gone";
            return FileCheck.Missing;
        }

        int error = FileTreeNative.TryOpen(decision.CanonicalPath!, out SafeFileHandle handle, out OpenedEntry opened);
        if (error != 0)
        {
            handle.Dispose();
            (FileCheck check, detail) = error switch
            {
                FileSystemNative.ErrorFileNotFound or FileSystemNative.ErrorPathNotFound => (FileCheck.Missing, "already gone"),
                FileTreeNative.ErrorSharingViolation => (FileCheck.Locked, "in use by another program"),
                FileTreeNative.ErrorAccessDenied => (FileCheck.Failed, "access denied"),
                _ => (FileCheck.Failed, string.Create(CultureInfo.InvariantCulture, $"Win32 error {error}")),
            };
            return check;
        }

        var candidate = new CheckedEntry(handle, opened, decision.CanonicalPath!);
        FileCheck result = Inspect(candidate, expectDirectory, out detail);
        if (result == FileCheck.Allowed)
        {
            entry = candidate;
        }
        else
        {
            candidate.Dispose();
        }

        return result;
    }

    public static EraseOutcome Erase(CheckedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var counts = new Counts();
        Erase(entry.Handle, entry.Entry, entry.Path, counts);
        return new EraseOutcome(counts.Deleted, counts.Scheduled, counts.Kept, counts.Problem);
    }

    public static int TryRemoveEmptyFolder(CheckedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!entry.Entry.IsDirectory || entry.Entry.IsReparsePoint)
        {
            return FileTreeNative.ErrorDirNotEmpty;
        }

        return FileTreeNative.TryMarkForDeletion(entry.Handle);
    }

    private static FileCheck Inspect(CheckedEntry entry, bool expectDirectory, out string detail)
    {
        int error = FileTreeNative.TryGetFinalPath(entry.Handle, out string finalPath);
        if (error != 0 || !string.Equals(finalPath, entry.Path, StringComparison.OrdinalIgnoreCase))
        {
            detail = $"the path now resolves to {finalPath}";
            return FileCheck.Changed;
        }

        if (!entry.Entry.IsReparsePoint && entry.Entry.IsDirectory != expectDirectory)
        {
            detail = expectDirectory ? "a file replaced the folder" : "a folder replaced the file";
            return FileCheck.Changed;
        }

        if (IsTrustedInstallerOwned(entry.Handle))
        {
            detail = "owned by TrustedInstaller";
            return FileCheck.TrustedInstallerOwned;
        }

        detail = "Microsoft-signed program files";
        if (entry.Entry.IsReparsePoint)
        {
            return FileCheck.Allowed;
        }

        if (!entry.Entry.IsDirectory)
        {
            return SystemFileTrustProbe.IsProgram(entry.Path) && WinTrustNative.CheckFile(entry.Path, entry.Handle) == SignatureCheck.MicrosoftSigned
                ? FileCheck.MicrosoftSigned
                : FileCheck.Allowed;
        }

        return AllProgramsMicrosoftSigned(entry) ? FileCheck.MicrosoftSigned : FileCheck.Allowed;
    }

    private static bool AllProgramsMicrosoftSigned(CheckedEntry folder)
    {
        if (FileTreeNative.TryList(folder.Handle, out List<string> names) != 0)
        {
            return false;
        }

        int sampled = 0;
        foreach (string name in names.Where(SystemFileTrustProbe.IsProgram).Take(SystemFileTrustProbe.MaxFolderSamples))
        {
            if (FileTreeNative.TryOpenChild(folder.Handle, name, out SafeFileHandle child, out OpenedEntry entry) != 0)
            {
                child.Dispose();
                continue;
            }

            using (child)
            {
                if (entry.IsDirectory || entry.IsReparsePoint)
                {
                    continue;
                }

                if (WinTrustNative.CheckFile(Path.Combine(folder.Path, name), child) != SignatureCheck.MicrosoftSigned)
                {
                    return false;
                }

                sampled++;
            }
        }

        return sampled > 0;
    }

    private static bool IsTrustedInstallerOwned(SafeFileHandle handle) =>
        FileTreeNative.TryGetOwner(handle) is { } owner && owner.Equals(SystemFileTrustProbe.TrustedInstaller);

    private static State Erase(SafeFileHandle handle, OpenedEntry entry, string path, Counts counts)
    {
        if (entry.IsDirectory && !entry.IsReparsePoint)
        {
            int listed = FileTreeNative.TryList(handle, out List<string> names);
            if (listed != 0)
            {
                return counts.Keep(path, listed);
            }

            bool anyKept = false;
            bool anyScheduled = false;
            foreach (string name in names)
            {
                string childPath = Path.Combine(path, name);
                int error = FileTreeNative.TryOpenChild(handle, name, out SafeFileHandle child, out OpenedEntry childEntry);
                using (child)
                {
                    State state = error switch
                    {
                        0 when IsTrustedInstallerOwned(child) => counts.Keep(childPath, "owned by TrustedInstaller"),
                        0 => Erase(child, childEntry, childPath, counts),
                        FileTreeNative.ErrorSharingViolation => counts.Schedule(childPath),
                        _ => counts.Keep(childPath, error),
                    };
                    anyKept |= state == State.Kept;
                    anyScheduled |= state == State.Scheduled;
                }
            }

            if (anyKept)
            {
                return State.Kept;
            }

            if (anyScheduled)
            {
                return counts.Schedule(path);
            }
        }

        int marked = FileTreeNative.TryMarkForDeletion(handle);
        if (marked == 0)
        {
            counts.Deleted++;
            return State.Gone;
        }

        return marked is FileTreeNative.ErrorAccessDenied or FileTreeNative.ErrorSharingViolation
            ? counts.Schedule(path)
            : counts.Keep(path, marked);
    }

    private sealed class Counts
    {
        public int Deleted { get; set; }

        public int Scheduled { get; private set; }

        public int Kept { get; private set; }

        public string? Problem { get; private set; }

        public State Schedule(string path)
        {
            int error = FileTreeNative.TryDeleteAtReboot(path);
            if (error == 0)
            {
                Scheduled++;
                return State.Scheduled;
            }

            return Keep(path, error == FileTreeNative.ErrorAccessDenied ? "in use; deleting it at the next start needs elevation" : $"in use; Win32 error {error} scheduling it");
        }

        public State Keep(string path, int error) =>
            Keep(path, error == FileTreeNative.ErrorAccessDenied ? "access denied" : string.Create(CultureInfo.InvariantCulture, $"Win32 error {error}"));

        public State Keep(string path, string reason)
        {
            Kept++;
            Problem ??= $"{path}: {reason}";
            return State.Kept;
        }
    }
}
