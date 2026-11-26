using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Niskala.Core.Commands;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Interop.Etw;
using Niskala.Tracker.Filtering;
using Niskala.Tracker.Launching;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Recording;
using Niskala.Tracker.Rollback;
using Niskala.Tracker.Tracing;
using Hives = Microsoft.Win32.Registry;

namespace Niskala.Tracker.Tests;

public sealed class EtwTraceTests
{
    [Fact]
    public void The_helper_loads_and_refuses_a_trace_without_elevation()
    {
        int error = EtwSession.TryStart(_ => false, out EtwSession? session);
        if (Environment.IsPrivilegedProcess)
        {
            Assert.Equal(0, error);
            using (session)
            {
                Assert.True(session!.Stop().EventsSeen > 0);
            }
        }
        else
        {
            Assert.Equal(EtwSession.ErrorAccessDenied, error);
            Assert.Null(session);
        }
    }

    [Fact]
    public async Task A_real_install_is_recorded_and_rolled_back()
    {
        if (!Environment.IsPrivilegedProcess)
        {
            Assert.Skip("Kernel tracing needs an elevated test run.");
        }

        using var scratch = new ScratchFolder();
        using var registry = new RegistryScratch();
        string app = Path.Combine(scratch.Root, "Acme");
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string script = $"/c mkdir \"{app}\" && echo acme> \"{app}\\acme.txt\" && reg add \"HKCU\\{registry.Key}\\Acme\" /v Theme /d dark /f >nul";

        var store = new InstallLogStore(Path.Combine(scratch.Root, "logs"));
        var source = new CapturingSource(EtwTraceSource.Instance, registry.Key[(registry.Key.LastIndexOf('\\') + 1)..]);
        var tracker = new InstallTracker(
            source,
            new JobInstallerLauncher(SystemCommandEnvironment.Instance),
            LiveSystemState.Instance,
            new SystemServices(SystemCommandEnvironment.Instance),
            new NoiseFilter([], DefaultNoise.Keys, Guards.Real(), RegistryGuard.ForCurrentMachine()),
            store,
            KernelPathTranslator.ForCurrentUser(),
            NullLogger<InstallTracker>.Instance);

        InstallLog log = await tracker.RunAsync(new TrackRequest(cmd, script, "Acme"), null, CancellationToken.None, TestContext.Current.CancellationToken);

        string recorded = Describe(log, source.Seen);
        Assert.True(log.Processes.Any(p => string.Equals(Path.GetFileName(p.ImagePath), "reg.exe", StringComparison.OrdinalIgnoreCase)), recorded);
        Assert.True(log.Files.Any(f => f is { Change: ChangeKind.Created, IsDirectory: true } && string.Equals(f.Path, app, StringComparison.OrdinalIgnoreCase)), recorded);
        Assert.True(log.Files.Any(f => string.Equals(f.Path, Path.Combine(app, "acme.txt"), StringComparison.OrdinalIgnoreCase)), recorded);
        Assert.True(log.Keys.Any(k => k.Change == ChangeKind.Created && string.Equals(k.Path, registry.Path("Acme"), StringComparison.OrdinalIgnoreCase)), recorded);
        Assert.True(log.Statistics!.EventsLost == 0, recorded);

        var rollback = new LogRollback(Guards.Cleaner(new TransactionStore(Path.Combine(scratch.Root, "journal")), Guards.Real()), store, LiveSystemState.Instance, NullLogger<LogRollback>.Instance);
        CleanResult result = await rollback.ExecuteAsync(rollback.Plan(log), new CleanOptions { Permanent = true, CreateRestorePoint = false }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Transaction.Items, i => i.Status == RemovalStatus.Failed);
        Assert.False(Directory.Exists(app));
        Assert.Null(Hives.CurrentUser.OpenSubKey($@"{registry.Key}\Acme"));
    }

    private static string Describe(InstallLog log, IEnumerable<EtwEvent> seen)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"processes: {string.Join(", ", log.Processes.Select(p => Path.GetFileName(p.ImagePath)))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"keys: {string.Join(" | ", log.Keys.Select(k => $"{k.Change} {k.Path}"))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"values: {string.Join(" | ", log.Values.Select(v => $"{v.Change} {v.Key} {v.Name}"))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"filtered: {string.Join(", ", log.Filtered.Select(f => $"{f.Key}={f.Value}"))}");
        text.AppendLine(CultureInfo.InvariantCulture, $"notes: {string.Join(" | ", log.Notes)}");
        text.AppendLine(CultureInfo.InvariantCulture, $"statistics: {log.Statistics}");
        foreach (EtwEvent e in seen)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"event: {e.Kind} pid {e.ProcessId} flags {e.Flags} {e.Path} name {e.Name}");
        }

        return text.ToString();
    }

    private sealed class CapturingSource(ITraceSource inner, string marker) : ITraceSource
    {
        private readonly ConcurrentQueue<EtwEvent> _seen = new();

        public EtwEvent[] Seen => [.. _seen];

        public ITraceSession Start(Func<EtwEvent, bool> onEvent) => inner.Start(e =>
        {
            if (e.Path.Contains(marker, StringComparison.OrdinalIgnoreCase) || e.Path.Contains("Acme", StringComparison.OrdinalIgnoreCase))
            {
                _seen.Enqueue(e);
            }

            return onEvent(e);
        });
    }
}
