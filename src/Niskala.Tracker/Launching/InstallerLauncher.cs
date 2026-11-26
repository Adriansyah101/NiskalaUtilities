using System.Globalization;
using Microsoft.Win32.SafeHandles;
using Niskala.Core.Commands;
using Niskala.Interop.Processes;
using Niskala.Tracker.Tracing;

namespace Niskala.Tracker.Launching;

public interface ILaunchedInstaller : IDisposable
{
    int ProcessId { get; }

    string ImagePath { get; }

    void Resume();

    Task WaitForTreeAsync(CancellationToken cancellationToken);

    int? ExitCode { get; }
}

public interface IInstallerLauncher
{
    ILaunchedInstaller Start(string installer, string? arguments);
}

public sealed class JobInstallerLauncher(ICommandEnvironment environment) : IInstallerLauncher
{
    private const int ErrorFileNotFound = 2;
    private const int ErrorBadFormat = 193;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public ILaunchedInstaller Start(string installer, string? arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(installer);
        string path = Path.GetFullPath(installer);
        if (!File.Exists(path))
        {
            throw new TrackerUnavailableException($"The installer {path} does not exist.", ErrorFileNotFound);
        }

        string suffix = string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments;
        string msiexec = Path.Combine(environment.SystemDirectory, "msiexec.exe");
        (string application, string commandLine) = Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".EXE" => (path, $"\"{path}\"{suffix}"),
            ".MSI" => (msiexec, $"\"{msiexec}\" /i \"{path}\"{suffix}"),
            _ => throw new TrackerUnavailableException("Only .exe and .msi installers can be recorded.", ErrorBadFormat),
        };

        int error = JobObject.TryCreate(out JobObject? job);
        if (error != 0)
        {
            throw new TrackerUnavailableException(string.Create(CultureInfo.InvariantCulture, $"No job object for the installer (Win32 error {error})."), error);
        }

        error = job!.TryStartSuspended(application, commandLine, Path.GetDirectoryName(path)!, out int processId, out SafeProcessHandle? process, out SafeFileHandle? thread);
        if (error != 0)
        {
            job.Dispose();
            throw new TrackerUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The installer did not start (Win32 error {error})."), error);
        }

        return new Launched(job, process!, thread!, processId, application);
    }

    private sealed class Launched(JobObject job, SafeProcessHandle process, SafeFileHandle thread, int processId, string imagePath) : ILaunchedInstaller
    {
        public int ProcessId => processId;

        public string ImagePath => imagePath;

        public int? ExitCode => ProcessNative.TryGetExitCode(process);

        public void Resume()
        {
            int error = JobObject.TryResume(thread);
            if (error != 0)
            {
                throw new TrackerUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The installer could not be resumed (Win32 error {error})."), error);
            }
        }

        public async Task WaitForTreeAsync(CancellationToken cancellationToken)
        {
            while (job.TryGetProcessIds(out int[] ids) != 0 || ids.Length > 0)
            {
                await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            thread.Dispose();
            process.Dispose();
            job.Dispose();
        }
    }
}
