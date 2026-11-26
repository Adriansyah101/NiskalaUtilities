namespace Niskala.Cleaner;

internal static class FileFacts
{
    public static bool IsLink(string folder)
    {
        try
        {
            return (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static long Size(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
