using System.Globalization;
using Niskala.Core.Safety;
using Niskala.Interop.Processes;
using Niskala.Scanner.Steam;
using Niskala.Uninstall.Preflight;

namespace Niskala.Uninstall.Steam;

public interface ISteamClient
{
    string? ClientFolder();

    bool IsRunning(string clientFolder);

    int Shutdown(string clientFolder);

    Task<bool> WaitForExitAsync(string clientFolder, TimeSpan wait, CancellationToken cancellationToken);

    int OpenUninstall(string clientFolder, int appId);
}

public sealed class SystemSteamClient : ISteamClient
{
    private const string Executable = "steam.exe";
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(500);

    public static SystemSteamClient Instance { get; } = new();

    public string? ClientFolder() => SteamLibrary.ClientFolder();

    public bool IsRunning(string clientFolder)
    {
        string client = Path.Combine(clientFolder, Executable);
        return SystemPreflightEnvironment.Instance.RunningProcesses().Any(p =>
            p.Name.Equals(Executable, StringComparison.OrdinalIgnoreCase)
            && (p.ImagePath is null || PathSyntax.TryNormalize(p.ImagePath, out string image) != PathVerdict.Allowed || image.Equals(client, StringComparison.OrdinalIgnoreCase)));
    }

    public int Shutdown(string clientFolder) => Start(clientFolder, "-shutdown");

    public async Task<bool> WaitForExitAsync(string clientFolder, TimeSpan wait, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + wait;
        while (IsRunning(clientFolder))
        {
            if (DateTime.UtcNow >= deadline)
            {
                return false;
            }

            await Task.Delay(Poll, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    public int OpenUninstall(string clientFolder, int appId) =>
        Start(clientFolder, string.Create(CultureInfo.InvariantCulture, $"steam://uninstall/{appId}"));

    private static int Start(string clientFolder, string arguments)
    {
        string client = Path.Combine(clientFolder, Executable);
        return UnelevatedShell.TryStart(client, $"\"{client}\" {arguments}");
    }
}
