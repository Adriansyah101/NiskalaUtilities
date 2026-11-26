using Niskala.Guardian.Files;

namespace Niskala.Guardian.Tests;

public sealed class FileEraserTests
{
    [Fact]
    public void Permanent_erase_removes_read_only_files_and_never_enters_a_junction()
    {
        using var scratch = new ScratchFolder();
        string keep = scratch.File(@"outside\keep.txt");
        string app = scratch.Dir("app");
        File.SetAttributes(scratch.File(@"app\readonly.txt"), FileAttributes.ReadOnly);
        scratch.File(@"app\sub\data.bin");
        scratch.Junction(@"app\link", Path.GetDirectoryName(keep)!);

        Assert.Equal(FileCheck.Allowed, new FileEraser(Guards.Real()).Check(app, expectDirectory: true, out CheckedEntry? entry, out _));
        EraseOutcome outcome;
        using (entry)
        {
            outcome = FileEraser.Erase(entry!);
        }

        Assert.Equal(new EraseOutcome(5, 0, 0, null), outcome);
        Assert.False(Directory.Exists(app));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void A_folder_replaced_by_a_junction_after_the_scan_goes_as_a_link()
    {
        using var scratch = new ScratchFolder();
        string keep = scratch.File(@"outside\keep.txt");
        string app = scratch.Dir("app");
        string data = scratch.File(@"app\data.txt");
        string moved = Path.Combine(scratch.Root, "app-old");
        Directory.Move(app, moved);
        scratch.Junction("app", Path.GetDirectoryName(keep)!);

        Assert.Equal(FileCheck.Allowed, new FileEraser(Guards.Real()).Check(app, expectDirectory: true, out CheckedEntry? entry, out _));
        using (entry)
        {
            Assert.True(entry!.Entry.IsReparsePoint);
            Assert.Equal(1, FileEraser.Erase(entry).Deleted);
        }

        Assert.False(Directory.Exists(app));
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(Path.Combine(moved, Path.GetFileName(data))));
    }

    [Fact]
    public void A_swap_after_the_check_cannot_redirect_the_removal()
    {
        using var scratch = new ScratchFolder();
        string keep = scratch.File(@"outside\keep.txt");
        string app = scratch.Dir("app");
        scratch.File(@"app\data.txt");
        string moved = Path.Combine(scratch.Root, "app-moved");

        Assert.Equal(FileCheck.Allowed, new FileEraser(Guards.Real()).Check(app, expectDirectory: true, out CheckedEntry? entry, out _));
        using (entry)
        {
            Directory.Move(app, moved);
            scratch.Junction("app", Path.GetDirectoryName(keep)!);
            Assert.Equal(0, FileEraser.Erase(entry!).Kept);
        }

        Assert.False(Directory.Exists(moved));
        Assert.True(Directory.Exists(app));
        Assert.True(File.Exists(keep));
    }

    [Fact]
    public void A_path_through_a_parent_junction_into_a_protected_folder_is_refused()
    {
        using var scratch = new ScratchFolder();
        string victim = scratch.File(@"protected\victim\v.txt");
        string link = scratch.Junction("app", Path.Combine(scratch.Root, "protected"));
        var eraser = new FileEraser(Guards.Real(Path.Combine(scratch.Root, "protected")));

        Assert.Equal(FileCheck.Protected, eraser.Check(Path.Combine(link, "victim"), expectDirectory: true, out CheckedEntry? entry, out _));
        Assert.Null(entry);
        Assert.True(File.Exists(victim));
    }

    [Fact]
    public void A_kind_change_since_the_scan_is_refused()
    {
        using var scratch = new ScratchFolder();
        string path = scratch.File("settings");

        Assert.Equal(FileCheck.Changed, new FileEraser(Guards.Real()).Check(path, expectDirectory: true, out _, out string detail));
        Assert.Contains("file replaced the folder", detail, StringComparison.Ordinal);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void An_open_file_is_locked_and_keeps_its_folder()
    {
        using var scratch = new ScratchFolder();
        string folder = scratch.Dir("app");
        string file = scratch.File(@"app\busy.dat");
        scratch.File(@"app\free.dat");
        var eraser = new FileEraser(Guards.Real());
        EraseOutcome outcome;
        int forgotten;

        try
        {
            using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Equal(FileCheck.Locked, eraser.Check(file, expectDirectory: false, out _, out _));
                Assert.Equal(FileCheck.Allowed, eraser.Check(folder, expectDirectory: true, out CheckedEntry? entry, out _));
                using (entry)
                {
                    outcome = FileEraser.Erase(entry!);
                }
            }
        }
        finally
        {
            forgotten = RebootQueue.Forget(scratch.Root);
        }

        Assert.Equal(1, outcome.Deleted);
        if (Environment.IsPrivilegedProcess)
        {
            Assert.Equal((2, 0, 2), (outcome.Scheduled, outcome.Kept, forgotten));
        }
        else
        {
            Assert.True(outcome.Kept >= 1);
            Assert.Contains("needs elevation", outcome.Problem, StringComparison.Ordinal);
        }

        Assert.True(File.Exists(file));
    }

    [Fact]
    public void Folders_of_only_Microsoft_signed_programs_are_refused()
    {
        using var scratch = new ScratchFolder();
        string folder = scratch.Dir("runtime");
        string signed = Path.Combine(folder, "dotnet.exe");
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"dotnet\dotnet.exe"), signed);
        var eraser = new FileEraser(Guards.Real());

        Assert.Equal(FileCheck.MicrosoftSigned, eraser.Check(signed, expectDirectory: false, out _, out _));
        Assert.Equal(FileCheck.MicrosoftSigned, eraser.Check(folder, expectDirectory: true, out _, out _));

        File.Copy(typeof(FileEraserTests).Assembly.Location, Path.Combine(folder, "own.dll"));
        Assert.Equal(FileCheck.Allowed, eraser.Check(folder, expectDirectory: true, out CheckedEntry? entry, out _));
        entry!.Dispose();
    }
}
