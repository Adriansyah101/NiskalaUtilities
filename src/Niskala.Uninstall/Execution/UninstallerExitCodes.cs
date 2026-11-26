using Niskala.Core.Models;
using Niskala.Uninstall.Commands;

namespace Niskala.Uninstall.Execution;

public enum ExitMeaning
{
    Unknown,
    Success,
    RebootRequired,
    RebootInitiated,
    Cancelled,
    InstallerBusy,
    ProductNotInstalled,
    Failed,
}

public static class UninstallerExitCodes
{
    private const int MsiUserExit = 1602;
    private const int MsiUnknownProduct = 1605;
    private const int MsiProductUninstalled = 1614;
    private const int MsiInstallInProgress = 1618;
    private const int RebootInitiated = 1641;
    private const int RebootRequired = 3010;

    public static ExitMeaning Interpret(int? exitCode, InstallerKind kind, CommandOrigin origin) => exitCode switch
    {
        null => ExitMeaning.Unknown,
        0 => ExitMeaning.Success,
        RebootRequired => ExitMeaning.RebootRequired,
        RebootInitiated => ExitMeaning.RebootInitiated,
        _ when origin == CommandOrigin.WindowsInstaller || kind is InstallerKind.Msi or InstallerKind.WixBurn => exitCode switch
        {
            MsiUserExit => ExitMeaning.Cancelled,
            MsiInstallInProgress => ExitMeaning.InstallerBusy,
            MsiUnknownProduct or MsiProductUninstalled => ExitMeaning.ProductNotInstalled,
            _ => ExitMeaning.Failed,
        },
        1 when kind == InstallerKind.Nsis => ExitMeaning.Cancelled,
        _ => ExitMeaning.Failed,
    };
}
