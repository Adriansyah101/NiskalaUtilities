using System.IO.Compression;

if (args.Length == 0 || args.Length % 2 != 0)
{
    Console.Error.WriteLine("Usage: dotnet run New-ReleaseArchive.cs -- <folder> <archive.zip> [<folder> <archive.zip> ...]");
    return 2;
}

for (int i = 0; i < args.Length; i += 2)
{
    string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(args[i]));
    string archive = Path.GetFullPath(args[i + 1]);
    string parent = Path.GetDirectoryName(folder) ?? folder;
    File.Delete(archive);
    using ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create);
    foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
    {
        zip.CreateEntryFromFile(file, Path.GetRelativePath(parent, file).Replace('\\', '/'), CompressionLevel.SmallestSize);
    }
}

return 0;
