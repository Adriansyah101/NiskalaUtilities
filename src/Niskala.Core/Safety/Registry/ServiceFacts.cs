using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Niskala.Core.Diagnostics;

namespace Niskala.Core.Safety.Registry;

public sealed record ServiceFacts(string Name, int? StartType, string? ImagePath, bool IsReadable = true);

public interface IServiceFacts
{
    ServiceFacts? TryGet(string serviceName);
}

public sealed class RegistryServiceFacts(ILogger? logger = null) : IServiceFacts
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    public ServiceFacts? TryGet(string serviceName)
    {
        ArgumentException.ThrowIfNullOrEmpty(serviceName);
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey($@"{ServicesKey}\{serviceName}");
            return key is null
                ? null
                : new ServiceFacts(
                    serviceName,
                    key.GetValue("Start") as int?,
                    key.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            CoreLog.ServiceReadFailed(_logger, serviceName, ex);
            return new ServiceFacts(serviceName, null, null, IsReadable: false);
        }
    }
}

public static class ServiceImagePath
{
    public static string? Resolve(string? imagePath, string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        string windows = windowsDirectory.TrimEnd('\\');
        string text = imagePath.Trim()
            .Replace("%SystemRoot%", windows, StringComparison.OrdinalIgnoreCase)
            .Replace("%windir%", windows, StringComparison.OrdinalIgnoreCase);
        text = Environment.ExpandEnvironmentVariables(text);

        if (text.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
        {
            text = windows + text[@"\SystemRoot".Length..];
        }
        else if (text.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            text = text[4..];
        }

        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            text = close < 0 ? text[1..] : text[1..close];
        }
        else
        {
            text = CutAfterExtension(text);
        }

        if (text.StartsWith(@"system32\", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith(@"SysWOW64\", StringComparison.OrdinalIgnoreCase))
        {
            text = Path.Combine(windows, text);
        }

        return PathSyntax.TryNormalize(text, out string normalized) == PathVerdict.Allowed ? normalized : null;
    }

    private static string CutAfterExtension(string text)
    {
        foreach (string extension in (ReadOnlySpan<string>)[".exe", ".sys", ".dll"])
        {
            int index = text.IndexOf(extension, StringComparison.OrdinalIgnoreCase);
            while (index >= 0)
            {
                int end = index + extension.Length;
                if (end == text.Length || text[end] == ' ')
                {
                    return text[..end];
                }

                index = text.IndexOf(extension, end, StringComparison.OrdinalIgnoreCase);
            }
        }

        int space = text.IndexOf(' ');
        return space < 0 ? text : text[..space];
    }
}
