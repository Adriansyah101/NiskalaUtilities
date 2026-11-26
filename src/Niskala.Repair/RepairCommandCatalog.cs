using System.Diagnostics.CodeAnalysis;

namespace Niskala.Repair;

public enum RepairCommandId
{
    DismCheckHealth,
    DismRestoreHealth,
    SfcScanNow,
    DismComponentCleanup,
    NetshWinsockReset,
    NetshIpReset,
    FlushDns,
    ChkdskOnlineScan,
}

[Flags]
public enum RepairTraits
{
    None = 0,
    NeedsElevation = 1,
    ChangesSystem = 2,
    NeedsRestart = 4,
    LongRunning = 8,
}

public sealed record RepairCommand(RepairCommandId Id, string Name, RepairTraits Traits)
{
    public bool NeedsElevation => (Traits & RepairTraits.NeedsElevation) != 0;

    public bool ChangesSystem => (Traits & RepairTraits.ChangesSystem) != 0;

    public bool NeedsRestart => (Traits & RepairTraits.NeedsRestart) != 0;

    public bool IsLongRunning => (Traits & RepairTraits.LongRunning) != 0;
}

public static class RepairCommandCatalog
{
    internal const string Dism = "Dism.exe";
    internal const string Sfc = "sfc.exe";
    internal const string Netsh = "netsh.exe";
    internal const string IpConfig = "ipconfig.exe";
    internal const string Chkdsk = "chkdsk.exe";

    private const string Online = "/Online";
    private const string CleanupImage = "/Cleanup-Image";

    public static IReadOnlyList<RepairCommand> All { get; } =
    [
        new(RepairCommandId.DismCheckHealth, "dism-check-health", RepairTraits.NeedsElevation),
        new(RepairCommandId.DismRestoreHealth, "dism-restore-health", RepairTraits.NeedsElevation | RepairTraits.ChangesSystem | RepairTraits.LongRunning),
        new(RepairCommandId.SfcScanNow, "sfc-scan", RepairTraits.NeedsElevation | RepairTraits.ChangesSystem | RepairTraits.LongRunning),
        new(RepairCommandId.DismComponentCleanup, "dism-component-cleanup", RepairTraits.NeedsElevation | RepairTraits.ChangesSystem | RepairTraits.LongRunning),
        new(RepairCommandId.NetshWinsockReset, "winsock-reset", RepairTraits.NeedsElevation | RepairTraits.ChangesSystem | RepairTraits.NeedsRestart),
        new(RepairCommandId.NetshIpReset, "ip-reset", RepairTraits.NeedsElevation | RepairTraits.ChangesSystem | RepairTraits.NeedsRestart),
        new(RepairCommandId.FlushDns, "flush-dns", RepairTraits.NeedsElevation),
        new(RepairCommandId.ChkdskOnlineScan, "chkdsk-scan", RepairTraits.NeedsElevation | RepairTraits.LongRunning),
    ];

    public static RepairCommand Get(RepairCommandId id) =>
        (uint)id < (uint)All.Count ? All[(int)id] : throw new ArgumentOutOfRangeException(nameof(id), id, "Not a repair command.");

    public static bool TryFind(string name, [NotNullWhen(true)] out RepairCommand? command)
    {
        ArgumentNullException.ThrowIfNull(name);
        command = All.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return command is not null;
    }

    public static RepairInvocation Resolve(RepairCommandId id, IRepairEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string system = environment.SystemDirectory;
        return id switch
        {
            RepairCommandId.DismCheckHealth => new(system, Dism, [Online, CleanupImage, "/CheckHealth"]),
            RepairCommandId.DismRestoreHealth => new(system, Dism, [Online, CleanupImage, "/RestoreHealth"]),
            RepairCommandId.SfcScanNow => new(system, Sfc, ["/scannow"]),
            RepairCommandId.DismComponentCleanup => new(system, Dism, [Online, CleanupImage, "/StartComponentCleanup"]),
            RepairCommandId.NetshWinsockReset => new(system, Netsh, ["winsock", "reset"]),
            RepairCommandId.NetshIpReset => new(system, Netsh, ["int", "ip", "reset"]),
            RepairCommandId.FlushDns => new(system, IpConfig, ["/flushdns"]),
            RepairCommandId.ChkdskOnlineScan => new(system, Chkdsk, [SystemDrive(system), "/scan"]),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "Not a repair command."),
        };
    }

    internal static string SystemDrive(string systemDirectory)
    {
        string drive = (Path.GetPathRoot(systemDirectory) ?? string.Empty).TrimEnd('\\');
        return RepairInvocation.IsDriveToken(drive)
            ? drive.ToUpperInvariant()
            : throw new InvalidOperationException($"The system directory {systemDirectory} is not on a lettered volume.");
    }
}
