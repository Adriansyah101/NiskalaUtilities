using Niskala.Interop.Shell;

namespace Niskala.UI.Services;

internal static class Pickers
{
    public static nint Owner { get; set; }

    public static string? Installer() =>
        FileDialogNative.PickFile(Owner, Strings.Current.PickInstallerTitle, [(Strings.Current.InstallerFiles, "*.exe;*.msi")]);

    public static string? Folder() => FileDialogNative.PickFolder(Owner, Strings.Current.PickFolderTitle);

    public static string? ScanFolder() => FileDialogNative.PickFolder(Owner, Strings.Current.PickScanFolderTitle);

    public static string? AnyFile() => FileDialogNative.PickFile(Owner, Strings.Current.PickWipeTitle, [(Strings.Current.AllFiles, "*.*")]);

    public static string? ProgramOrShortcut() =>
        FileDialogNative.PickFileOrShortcut(Owner, Strings.Current.PickTargetTitle, [(Strings.Current.ProgramsAndShortcuts, "*.exe;*.lnk;*.url")]);
}
