using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Windows.Wdk.Foundation;
using Windows.Wdk.Storage.FileSystem;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.Security.Authorization;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.IO;
using WdkPInvoke = Windows.Wdk.PInvoke;

namespace Niskala.Interop.FileSystem;

public readonly record struct OpenedEntry(FileAttributes Attributes, uint ReparseTag)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    public bool IsReparsePoint => (Attributes & FileAttributes.ReparsePoint) != 0;
}

public static class FileTreeNative
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorSharingViolation = 32;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorDirNotEmpty = 145;
    public const int ErrorNotSupported = 50;
    public const int ErrorNoMoreFiles = 18;

    private const FILE_ACCESS_RIGHTS EntryAccess =
        FILE_ACCESS_RIGHTS.DELETE | FILE_ACCESS_RIGHTS.FILE_READ_DATA | FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES
        | FILE_ACCESS_RIGHTS.FILE_WRITE_ATTRIBUTES | FILE_ACCESS_RIGHTS.READ_CONTROL | FILE_ACCESS_RIGHTS.SYNCHRONIZE;

    private const int ListBufferBytes = 64 * 1024;
    private const int FinalPathCapacity = 512;

    public static unsafe int TryOpen(string path, out SafeFileHandle handle, out OpenedEntry entry)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        entry = default;
        int error = NativeHandle.TryOpen(
            path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path,
            (uint)EntryAccess,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT,
            out HANDLE raw);
        if (error != 0)
        {
            handle = new SafeFileHandle();
            return error;
        }

        handle = new SafeFileHandle((nint)raw.Value, ownsHandle: true);
        return TryDescribe(handle, out entry);
    }

    public static unsafe int TryOpenChild(SafeFileHandle directory, string name, out SafeFileHandle handle, out OpenedEntry entry)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentException.ThrowIfNullOrEmpty(name);
        entry = default;
        bool added = false;
        try
        {
            directory.DangerousAddRef(ref added);
            fixed (char* text = name)
            {
                var objectName = new UNICODE_STRING
                {
                    Length = (ushort)(name.Length * sizeof(char)),
                    MaximumLength = (ushort)(name.Length * sizeof(char)),
                    Buffer = text,
                };
                var attributes = new OBJECT_ATTRIBUTES
                {
                    Length = (uint)sizeof(OBJECT_ATTRIBUTES),
                    RootDirectory = (HANDLE)directory.DangerousGetHandle(),
                    ObjectName = &objectName,
                    Attributes = OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE,
                };

                HANDLE raw;
                IO_STATUS_BLOCK status;
                NTSTATUS result = WdkPInvoke.NtCreateFile(
                    &raw,
                    EntryAccess,
                    &attributes,
                    &status,
                    null,
                    0,
                    NativeHandle.ShareAll,
                    NTCREATEFILE_CREATE_DISPOSITION.FILE_OPEN,
                    NTCREATEFILE_CREATE_OPTIONS.FILE_OPEN_REPARSE_POINT | NTCREATEFILE_CREATE_OPTIONS.FILE_OPEN_FOR_BACKUP_INTENT
                        | NTCREATEFILE_CREATE_OPTIONS.FILE_SYNCHRONOUS_IO_NONALERT,
                    null,
                    0);
                if (result.SeverityCode == NTSTATUS.Severity.Error)
                {
                    handle = new SafeFileHandle();
                    return (int)PInvoke.RtlNtStatusToDosError(result);
                }

                handle = new SafeFileHandle((nint)raw.Value, ownsHandle: true);
            }
        }
        finally
        {
            if (added)
            {
                directory.DangerousRelease();
            }
        }

        return TryDescribe(handle, out entry);
    }

    public static unsafe int TryList(SafeFileHandle directory, out List<string> names)
    {
        ArgumentNullException.ThrowIfNull(directory);
        names = [];
        byte[] buffer = new byte[ListBufferBytes];
        FILE_INFO_BY_HANDLE_CLASS kind = FILE_INFO_BY_HANDLE_CLASS.FileFullDirectoryRestartInfo;
        fixed (byte* start = buffer)
        {
            while (true)
            {
                if (!PInvoke.GetFileInformationByHandleEx((HANDLE)directory.DangerousGetHandle(), kind, start, (uint)buffer.Length))
                {
                    int error = Marshal.GetLastPInvokeError();
                    return error == ErrorNoMoreFiles ? 0 : error;
                }

                kind = FILE_INFO_BY_HANDLE_CLASS.FileFullDirectoryInfo;
                for (byte* at = start; ; )
                {
                    var info = (FILE_FULL_DIR_INFO*)at;
                    string name = new((char*)&info->FileName, 0, (int)(info->FileNameLength / sizeof(char)));
                    if (name is not ("." or ".."))
                    {
                        names.Add(name);
                    }

                    if (info->NextEntryOffset == 0)
                    {
                        break;
                    }

                    at += info->NextEntryOffset;
                }
            }
        }
    }

    public static unsafe int TryMarkForDeletion(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        var extended = new FILE_DISPOSITION_INFO_EX
        {
            Flags = FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_DELETE
                | FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_POSIX_SEMANTICS
                | FILE_DISPOSITION_INFO_EX_FLAGS.FILE_DISPOSITION_FLAG_IGNORE_READONLY_ATTRIBUTE,
        };
        if (PInvoke.SetFileInformationByHandle(handle, FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfoEx, new ReadOnlySpan<byte>(&extended, sizeof(FILE_DISPOSITION_INFO_EX))))
        {
            return 0;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error is not (ErrorInvalidParameter or ErrorNotSupported))
        {
            return error;
        }

        ClearReadOnly(handle);
        var plain = new FILE_DISPOSITION_INFO { DeleteFile = true };
        return PInvoke.SetFileInformationByHandle(handle, FILE_INFO_BY_HANDLE_CLASS.FileDispositionInfo, new ReadOnlySpan<byte>(&plain, sizeof(FILE_DISPOSITION_INFO)))
            ? 0
            : Marshal.GetLastPInvokeError();
    }

    public static int TryGetFinalPath(SafeFileHandle handle, out string path)
    {
        ArgumentNullException.ThrowIfNull(handle);
        const GETFINALPATHNAMEBYHANDLE_FLAGS Flags = GETFINALPATHNAMEBYHANDLE_FLAGS.FILE_NAME_NORMALIZED | GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_DOS;
        path = string.Empty;
        char[] buffer = new char[FinalPathCapacity];
        uint length = PInvoke.GetFinalPathNameByHandle(handle, buffer, Flags);
        if (length >= buffer.Length)
        {
            buffer = new char[length];
            length = PInvoke.GetFinalPathNameByHandle(handle, buffer, Flags);
        }

        if (length == 0 || length >= buffer.Length)
        {
            return length == 0 ? Marshal.GetLastPInvokeError() : FileSystemNative.ErrorInsufficientBuffer;
        }

        ReadOnlySpan<char> text = buffer.AsSpan(0, (int)length);
        path = text.StartsWith(@"\\?\") && !text.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? new string(text[4..]) : new string(text);
        return 0;
    }

    public static unsafe SecurityIdentifier? TryGetOwner(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        PSID owner;
        PSECURITY_DESCRIPTOR descriptor;
        WIN32_ERROR error = PInvoke.GetSecurityInfo(
            (HANDLE)handle.DangerousGetHandle(),
            SE_OBJECT_TYPE.SE_FILE_OBJECT,
            OBJECT_SECURITY_INFORMATION.OWNER_SECURITY_INFORMATION,
            &owner,
            null,
            null,
            null,
            &descriptor);
        if (error != WIN32_ERROR.ERROR_SUCCESS)
        {
            return null;
        }

        try
        {
            return owner.Value is null ? null : new SecurityIdentifier((nint)owner.Value);
        }
        finally
        {
            PInvoke.LocalFree((HLOCAL)descriptor.Value);
        }
    }

    public static int TryDeleteAtReboot(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return PInvoke.MoveFileEx(path, null, MOVE_FILE_FLAGS.MOVEFILE_DELAY_UNTIL_REBOOT) ? 0 : Marshal.GetLastPInvokeError();
    }

    private static unsafe int TryDescribe(SafeFileHandle handle, out OpenedEntry entry)
    {
        entry = default;
        FILE_ATTRIBUTE_TAG_INFO info;
        if (!PInvoke.GetFileInformationByHandleEx((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileAttributeTagInfo, &info, (uint)sizeof(FILE_ATTRIBUTE_TAG_INFO)))
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return error;
        }

        var attributes = (FileAttributes)info.FileAttributes;
        entry = new OpenedEntry(attributes, (attributes & FileAttributes.ReparsePoint) != 0 ? info.ReparseTag : 0);
        return 0;
    }

    private static unsafe void ClearReadOnly(SafeFileHandle handle)
    {
        FILE_BASIC_INFO basic;
        if (PInvoke.GetFileInformationByHandleEx((HANDLE)handle.DangerousGetHandle(), FILE_INFO_BY_HANDLE_CLASS.FileBasicInfo, &basic, (uint)sizeof(FILE_BASIC_INFO))
            && (basic.FileAttributes & (uint)FileAttributes.ReadOnly) != 0)
        {
            basic.FileAttributes &= ~(uint)FileAttributes.ReadOnly;
            PInvoke.SetFileInformationByHandle(handle, FILE_INFO_BY_HANDLE_CLASS.FileBasicInfo, new ReadOnlySpan<byte>(&basic, sizeof(FILE_BASIC_INFO)));
        }
    }
}
