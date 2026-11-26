using Niskala.Core.Commands;

namespace Niskala.Tracker.Filtering;

public static class DefaultNoise
{
    private const string Temporary = "Temporary files";
    private const string InternetCache = "Internet cache";
    private const string ShellState = "Shell state";
    private const string ErrorReports = "Error reports";
    private const string ShaderCache = "Shader cache";
    private const string Cryptography = "Cryptography";
    private const string BrowserCache = "Browser cache";
    private const string WindowsInstaller = "Windows Installer bookkeeping";
    private const string Compatibility = "Compatibility Assistant";

    private static readonly string[] BrowserProfiles =
    [
        @"%LOCALAPPDATA%\Google\Chrome\User Data\*",
        @"%LOCALAPPDATA%\Microsoft\Edge\User Data\*",
        @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data\*",
    ];

    private static readonly string[] BrowserCaches = ["Cache", "Code Cache", "GPUCache", @"Service Worker\CacheStorage"];

    public static IReadOnlyList<NoiseRule> Keys { get; } =
    [
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\ComDlg32", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\MountPoints2", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FeatureUsage", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\TypedPaths", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\Shell\BagMRU", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\Shell\Bags", ShellState),
        new(@"HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\Shell", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\CloudStore", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Notifications", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\PushNotifications", ShellState),
        new(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings", InternetCache),
        new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Internet Settings", InternetCache),
        new(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Internet Settings", InternetCache),
        new(@"HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags", Compatibility),
        new(@"HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags", Compatibility),
        new(@"HKLM\SOFTWARE\Microsoft\Tracing", "Network tracing"),
        new(@"HKLM\SOFTWARE\WOW6432Node\Microsoft\Tracing", "Network tracing"),
        new(@"HKLM\SOFTWARE\Microsoft\Cryptography", Cryptography),
        new(@"HKLM\SOFTWARE\Microsoft\SystemCertificates", Cryptography),
        new(@"HKCU\Software\Microsoft\SystemCertificates", Cryptography),
        new(@"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer", WindowsInstaller),
        new(@"HKLM\SOFTWARE\Classes\Installer", WindowsInstaller),
        new(@"HKCU\Software\Microsoft\Installer", WindowsInstaller),
        new(@"HKCU\Software\Classes\Installer", WindowsInstaller),
        new(@"HKCU\Software\Microsoft\RestartManager", "Restart Manager"),
        new(@"HKLM\SYSTEM\CurrentControlSet\Services\bam", "Background activity"),
        new(@"HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting", ErrorReports),
        new(@"HKCU\Software\Microsoft\Windows\Windows Error Reporting", ErrorReports),
    ];

    public static IReadOnlyList<NoiseRule> Files(ICommandEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        (string Pattern, string Reason)[] rules =
        [
            ("%TEMP%", Temporary),
            (@"%SystemRoot%\Temp", Temporary),
            (@"%SystemRoot%\Prefetch", "Prefetch"),
            (@"%LOCALAPPDATA%\Microsoft\Windows\INetCache", InternetCache),
            (@"%LOCALAPPDATA%\Microsoft\Windows\INetCookies", InternetCache),
            (@"%LOCALAPPDATA%\Microsoft\Windows\WebCache", InternetCache),
            (@"%LOCALAPPDATA%\Microsoft\Windows\History", InternetCache),
            (@"%LOCALAPPDATA%\Microsoft\Windows\Explorer", ShellState),
            (@"%LOCALAPPDATA%\Microsoft\Windows\Caches", ShellState),
            (@"%APPDATA%\Microsoft\Windows\Recent", ShellState),
            (@"%LOCALAPPDATA%\CrashDumps", ErrorReports),
            (@"%LOCALAPPDATA%\Microsoft\Windows\WER", ErrorReports),
            (@"%ProgramData%\Microsoft\Windows\WER", ErrorReports),
            (@"%LOCALAPPDATA%\D3DSCache", ShaderCache),
            (@"%LOCALAPPDATA%\NVIDIA\DXCache", ShaderCache),
            (@"%LOCALAPPDATA%\NVIDIA\GLCache", ShaderCache),
            (@"%LOCALAPPDATA%\AMD\DxCache", ShaderCache),
            (@"%APPDATA%\Microsoft\Crypto", Cryptography),
            (@"%APPDATA%\Microsoft\Protect", Cryptography),
            (@"%APPDATA%\Microsoft\SystemCertificates", Cryptography),
            (@"%ProgramData%\Microsoft\Crypto", Cryptography),
            (@"%LOCALAPPDATA%\Microsoft\CLR_v4.0", ".NET usage logs"),
            (@"%LOCALAPPDATA%\Microsoft\CLR_v4.0_32", ".NET usage logs"),
            (@"%SystemDrive%\Config.Msi", WindowsInstaller),
            (@"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2", BrowserCache),
        ];

        return
        [
            .. rules.Select(r => new NoiseRule(environment.ExpandVariables(r.Pattern).TrimEnd('\\'), r.Reason)),
            .. BrowserProfiles.SelectMany(p => BrowserCaches.Select(c => new NoiseRule($@"{environment.ExpandVariables(p)}\{c}", BrowserCache))),
        ];
    }
}
