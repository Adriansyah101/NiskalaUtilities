using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Niskala.Interop.FileSystem;

public readonly record struct FileIdentity(string FinalPath, FileAttributes Attributes, uint ReparseTag)
{
    private const uint NameSurrogateBit = 0x2000_0000;

    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;

    public bool IsNameSurrogate => IsReparsePoint && (ReparseTag & NameSurrogateBit) != 0;
}

public enum DriveKind
{
    Unknown = 0,
    NoRootDirectory = 1,
    Removable = 2,
    Fixed = 3,
    Remote = 4,
    CdRom = 5,
    RamDisk = 6,
}

public static class FileSystemNative
{
    public const int ErrorSuccess = 0;
    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorInsufficientBuffer = 122;

    private const string ExtendedPrefix = @"\\?\";

    public static unsafe int TryQueryIdentity(string path, bool openReparsePoint, out FileIdentity identity)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        identity = default;

        FILE_FLAGS_AND_ATTRIBUTES flags = FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS;
        if (openReparsePoint)
        {
            flags |= FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT;
        }

        int error = NativeHandle.TryOpen(ToExtendedPath(path), NativeHandle.FileReadAttributes, flags, out HANDLE handle);
        if (error != ErrorSuccess)
        {
            return error;
        }

        try
        {
            FILE_ATTRIBUTE_TAG_INFO tagInfo;
            if (!PInvoke.GetFileInformationByHandleEx(handle, FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, &tagInfo, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
            {
                return Marshal.GetLastPInvokeError();
            }

            error = TryGetFinalPath(handle, out string finalPath);
            if (error != ErrorSuccess)
            {
                return error;
            }

            var attributes = (FileAttributes)tagInfo.FileAttributes;
            uint tag = (attributes & FileAttributes.ReparsePoint) != 0 ? tagInfo.ReparseTag : 0;
            identity = new FileIdentity(finalPath, attributes, tag);
            return ErrorSuccess;
        }
        finally
        {
            PInvoke.CloseHandle(handle);
        }
    }

    public static DriveKind GetDriveKind(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        uint kind = PInvoke.GetDriveType(root);
        return kind <= (uint)DriveKind.RamDisk ? (DriveKind)kind : DriveKind.Unknown;
    }

    private static unsafe int TryGetFinalPath(HANDLE handle, out string finalPath)
    {
        finalPath = string.Empty;
        const GETFINALPATHNAMEBYHANDLE_FLAGS Flags =
            GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED | GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_DOS;

        int capacity = 512;
        while (true)
        {
            char[] buffer = new char[capacity];
            uint length;
            fixed (char* p = buffer)
            {
                length = PInvoke.GetFinalPathNameByHandle(handle, p, (uint)buffer.Length, Flags);
            }

            if (length == 0)
            {
                return Marshal.GetLastPInvokeError();
            }

            if (length < buffer.Length)
            {
                finalPath = new string(buffer, 0, (int)length);
                return ErrorSuccess;
            }

            if (length > short.MaxValue + 1)
            {
                return ErrorInsufficientBuffer;
            }

            capacity = (int)length;
        }
    }

    private static string ToExtendedPath(string path) =>
        path.StartsWith(ExtendedPrefix, StringComparison.Ordinal) ? path : ExtendedPrefix + path;
}
