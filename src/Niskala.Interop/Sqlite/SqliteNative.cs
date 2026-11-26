using System.Runtime.InteropServices;

namespace Niskala.Interop.Sqlite;

public static partial class SqliteNative
{
    public const int Ok = 0;
    public const int Busy = 5;
    public const int Locked = 6;
    public const int CannotOpen = 14;
    public const int NotADatabase = 26;
    public const int Row = 100;
    public const int Done = 101;

    internal const int OpenReadWrite = 0x0000_0002;
    internal const int OpenNoMutex = 0x0000_8000;
    internal const int OpenPrivateCache = 0x0004_0000;

    private const string Library = "winsqlite3.dll";

    [LibraryImport(Library, EntryPoint = "sqlite3_open_v2", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int Open(string fileName, out nint database, int flags, string? vfs);

    [LibraryImport(Library, EntryPoint = "sqlite3_close_v2")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int Close(nint database);

    [LibraryImport(Library, EntryPoint = "sqlite3_busy_timeout")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int BusyTimeout(nint database, int milliseconds);

    [LibraryImport(Library, EntryPoint = "sqlite3_prepare_v2", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int Prepare(nint database, string sql, int bytes, out nint statement, out nint tail);

    [LibraryImport(Library, EntryPoint = "sqlite3_step")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int Step(nint statement);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_text")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint ColumnText(nint statement, int column);

    [LibraryImport(Library, EntryPoint = "sqlite3_column_int64")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial long ColumnInt64(nint statement, int column);

    [LibraryImport(Library, EntryPoint = "sqlite3_finalize")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int Finalize(nint statement);

    [LibraryImport(Library, EntryPoint = "sqlite3_errmsg")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial nint ErrorMessage(nint database);

    [LibraryImport(Library, EntryPoint = "sqlite3_extended_errcode")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    internal static partial int ExtendedErrorCode(nint database);
}

public sealed class SqliteException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;

    public int PrimaryCode => Code & 0xFF;
}

public sealed class SqliteDatabase : IDisposable
{
    private nint _handle;

    private SqliteDatabase(nint handle) => _handle = handle;

    public static int TryOpen(string path, TimeSpan busyTimeout, out SqliteDatabase? database)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        database = null;
        int result = SqliteNative.Open(path, out nint handle, SqliteNative.OpenReadWrite | SqliteNative.OpenNoMutex | SqliteNative.OpenPrivateCache, null);
        if (result != SqliteNative.Ok)
        {
            if (handle != 0)
            {
                SqliteNative.Close(handle);
            }

            return result;
        }

        SqliteNative.BusyTimeout(handle, (int)busyTimeout.TotalMilliseconds);
        database = new SqliteDatabase(handle);
        return SqliteNative.Ok;
    }

    public void Execute(string sql) => _ = Query(sql).Count();

    public IReadOnlyList<string> Rows(string sql) => [.. Query(sql)];

    public long Scalar(string sql)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        int result = SqliteNative.Prepare(_handle, sql, -1, out nint statement, out _);
        Check(result);
        try
        {
            result = SqliteNative.Step(statement);
            if (result == SqliteNative.Row)
            {
                return SqliteNative.ColumnInt64(statement, 0);
            }

            Check(result == SqliteNative.Done ? SqliteNative.Ok : result);
            return 0;
        }
        finally
        {
            SqliteNative.Finalize(statement);
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            SqliteNative.Close(_handle);
            _handle = 0;
        }
    }

    private IEnumerable<string> Query(string sql)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);
        int result = SqliteNative.Prepare(_handle, sql, -1, out nint statement, out _);
        Check(result);
        try
        {
            while (true)
            {
                result = SqliteNative.Step(statement);
                if (result == SqliteNative.Done)
                {
                    yield break;
                }

                if (result != SqliteNative.Row)
                {
                    Check(result);
                }

                yield return Marshal.PtrToStringUTF8(SqliteNative.ColumnText(statement, 0)) ?? string.Empty;
            }
        }
        finally
        {
            SqliteNative.Finalize(statement);
        }
    }

    private void Check(int result)
    {
        if (result != SqliteNative.Ok)
        {
            int code = SqliteNative.ExtendedErrorCode(_handle);
            throw new SqliteException(code == 0 ? result : code, Marshal.PtrToStringUTF8(SqliteNative.ErrorMessage(_handle)) ?? "SQLite error");
        }
    }
}
