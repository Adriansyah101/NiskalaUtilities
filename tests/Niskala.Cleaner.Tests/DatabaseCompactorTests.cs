using Niskala.Cleaner.Databases;
using Niskala.Interop.Sqlite;

namespace Niskala.Cleaner.Tests;

public sealed class DatabaseCompactorTests
{
    private static readonly TimeSpan NoWait = TimeSpan.Zero;

    [Fact]
    public void A_database_with_free_pages_shrinks_and_keeps_its_rows()
    {
        using var scratch = new ScratchFolder();
        string path = Bloated(scratch, "History");

        CompactionResult result = DatabaseCompactor.Compact(path);

        Assert.Equal(CompactionOutcome.Compacted, result.Outcome);
        Assert.True(result.BytesAfter < result.BytesBefore / 4, $"{result.BytesBefore} -> {result.BytesAfter}");
        Assert.Equal(10, Count(path));
    }

    [Fact]
    public void A_damaged_database_is_left_untouched()
    {
        using var scratch = new ScratchFolder();
        string path = scratch.File("Cookies", 0);
        byte[] garbage = new byte[8192];
        Array.Fill(garbage, (byte)0xAB);
        File.WriteAllBytes(path, garbage);

        CompactionResult result = DatabaseCompactor.Compact(path);

        Assert.Equal(CompactionOutcome.Damaged, result.Outcome);
        Assert.Equal(garbage, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_database_another_connection_holds_is_reported_locked()
    {
        using var scratch = new ScratchFolder();
        string path = Bloated(scratch, "Favicons");
        Assert.Equal(SqliteNative.Ok, SqliteDatabase.TryOpen(path, NoWait, out SqliteDatabase? holder));
        using (holder)
        {
            holder!.Execute("BEGIN EXCLUSIVE");

            CompactionResult result = DatabaseCompactor.Compact(path);

            Assert.Equal(CompactionOutcome.Locked, result.Outcome);
        }
    }

    [Fact]
    public void A_missing_database_is_reported_missing() =>
        Assert.Equal(CompactionOutcome.Missing, DatabaseCompactor.Compact(Path.Combine(TestRoots.Folder, "absent.sqlite")).Outcome);

    private static string Bloated(ScratchFolder scratch, string name)
    {
        string path = scratch.File(name, 0);
        Assert.Equal(SqliteNative.Ok, SqliteDatabase.TryOpen(path, NoWait, out SqliteDatabase? database));
        using (database)
        {
            database!.Execute("CREATE TABLE visits(id INTEGER PRIMARY KEY, payload BLOB)");
            database.Execute("WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 2000) INSERT INTO visits(payload) SELECT randomblob(1024) FROM n");
            database.Execute("DELETE FROM visits WHERE id > 10");
        }

        return path;
    }

    private static long Count(string path)
    {
        Assert.Equal(SqliteNative.Ok, SqliteDatabase.TryOpen(path, NoWait, out SqliteDatabase? database));
        using (database)
        {
            return database!.Scalar("SELECT COUNT(*) FROM visits");
        }
    }
}
