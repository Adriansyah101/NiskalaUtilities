using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace Niskala.Uninstall.Packages;

public sealed record PackageRemovalResult(int HResult, string? ErrorText, TimeSpan Elapsed)
{
    public bool Succeeded => HResult == 0;
}

public interface IPackageRemover
{
    Task<PackageRemovalResult> RemoveAsync(string packageFullName, CancellationToken cancellationToken = default);
}

public sealed class PackageManagerRemover(ILogger<PackageManagerRemover> logger) : IPackageRemover
{
    public async Task<PackageRemovalResult> RemoveAsync(string packageFullName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageFullName);
        long start = Stopwatch.GetTimestamp();
        IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> operation;
        try
        {
            operation = new PackageManager().RemovePackageAsync(packageFullName);
        }
        catch (COMException ex)
        {
            UninstallLog.PackageRemovalFailed(logger, packageFullName, ex.HResult, ex.Message);
            return new PackageRemovalResult(ex.HResult, ex.Message, Stopwatch.GetElapsedTime(start));
        }

        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        operation.Completed = (_, _) => completed.TrySetResult();
        await completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        PackageRemovalResult result = operation.Status == AsyncStatus.Completed
            ? new PackageRemovalResult(0, null, Stopwatch.GetElapsedTime(start))
            : new PackageRemovalResult(operation.ErrorCode?.HResult ?? -1, ErrorText(operation), Stopwatch.GetElapsedTime(start));

        if (result.Succeeded)
        {
            UninstallLog.PackageRemoved(logger, packageFullName, (long)result.Elapsed.TotalMilliseconds);
        }
        else
        {
            UninstallLog.PackageRemovalFailed(logger, packageFullName, result.HResult, result.ErrorText);
        }

        return result;
    }

    private static string? ErrorText(IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> operation)
    {
        try
        {
            return operation.GetResults()?.ErrorText;
        }
        catch (COMException ex)
        {
            return ex.Message;
        }
    }
}
