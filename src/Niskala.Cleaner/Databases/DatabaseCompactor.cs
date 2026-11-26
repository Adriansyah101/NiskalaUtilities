using Niskala.Interop.Sqlite;

namespace Niskala.Cleaner.Databases;

public enum CompactionOutcome
{
    Compacted,
    Missing,
    Locked,
    Damaged,
    Failed,
}

public sealed record CompactionResult(string Path, CompactionOutcome Outcome, long BytesBefore, long BytesAfter, string? Detail = null)
{
    public long Saved => Math.Max(0, BytesBefore - BytesAfter);
}

public static class DatabaseCompactor
{
    public const string IntegrityOk = "ok";

    private static readonly TimeSpan BusyTimeout = TimeSpan.Zero;

    public static CompactionResult Compact(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (!File.Exists(path))
        {
            return new CompactionResult(path, CompactionOutcome.Missing, 0, 0);
        }

        long before = FileFacts.Size(path);
        int opened = SqliteDatabase.TryOpen(path, BusyTimeout, out SqliteDatabase? database);
        if (opened != SqliteNative.Ok)
        {
            return new CompactionResult(path, opened is SqliteNative.Busy or SqliteNative.Locked or SqliteNative.CannotOpen ? CompactionOutcome.Locked : CompactionOutcome.Failed, before, before, $"SQLite error {opened}");
        }

        using (database)
        {
            try
            {
                IReadOnlyList<string> integrity = database!.Rows("PRAGMA integrity_check");
                if (integrity is not [IntegrityOk])
                {
                    return new CompactionResult(path, CompactionOutcome.Damaged, before, before, integrity.Count > 0 ? integrity[0] : "no integrity result");
                }

                database.Execute("VACUUM");
            }
            catch (SqliteException ex)
            {
                CompactionOutcome outcome = ex.PrimaryCode switch
                {
                    SqliteNative.Busy or SqliteNative.Locked => CompactionOutcome.Locked,
                    SqliteNative.NotADatabase => CompactionOutcome.Damaged,
                    _ => CompactionOutcome.Failed,
                };
                return new CompactionResult(path, outcome, before, before, ex.Message);
            }
        }

        return new CompactionResult(path, CompactionOutcome.Compacted, before, FileFacts.Size(path));
    }
}
