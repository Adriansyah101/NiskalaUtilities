using Microsoft.Extensions.Logging;
using Niskala.Cleaner;
using Niskala.Cleaner.Execution;
using Niskala.Cleaner.Registry;
using Niskala.Cleaner.Rules;
using Niskala.Cleaner.Scanning;
using Niskala.Core.Commands;
using Niskala.Core.Safety;
using Niskala.Core.Safety.Registry;
using Niskala.Guardian;
using Niskala.Guardian.Cleanup;
using Niskala.Guardian.Journal;
using Niskala.Guardian.Restore;
using Niskala.Guardian.RestorePoints;
using Niskala.Guardian.Settings;
using Niskala.Guardian.Startup;
using Niskala.Optimizer.Drives;
using Niskala.Optimizer.Processes;
using Niskala.Optimizer.Resources;
using Niskala.Repair;
using Niskala.Scanner.Detection;
using Niskala.Scanner.Fingerprints;
using Niskala.Scanner.Icons;
using Niskala.Scanner.Inventory;
using Niskala.Scanner.Inventory.Msi;
using Niskala.Scanner.Leftovers;
using Niskala.Scanner.Sizing;
using Niskala.Scanner.Steam;
using Niskala.Tracker;
using Niskala.Tracker.Filtering;
using Niskala.Tracker.Launching;
using Niskala.Tracker.Logs;
using Niskala.Tracker.Recording;
using Niskala.Tracker.Rollback;
using Niskala.Tracker.Tracing;
using Niskala.Uninstall;
using Niskala.Uninstall.Batch;
using Niskala.Uninstall.Commands;
using Niskala.Uninstall.Execution;
using Niskala.Uninstall.Force;
using Niskala.Uninstall.Packages;
using Niskala.Uninstall.Preflight;
using Niskala.Uninstall.Security;
using Niskala.Uninstall.Steam;
using Niskala.Uninstall.Verification;
using Niskala.Updater;

namespace Niskala.Host;

public sealed class NiskalaHost : IDisposable
{
    private readonly Lazy<PathGuard> _guard;
    private readonly Lazy<RegistryGuard> _registryGuard;
    private readonly Lazy<MachineLayout> _layout;
    private readonly Lazy<PathGuard> _cleanerGuard;

    public NiskalaHost(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        LoggerFactory = loggerFactory;
        _guard = new Lazy<PathGuard>(() => PathGuard.ForCurrentMachine(loggerFactory));
        _registryGuard = new Lazy<RegistryGuard>(() => RegistryGuard.ForCurrentMachine(loggerFactory));
        _layout = new Lazy<MachineLayout>(() => MachineLayout.Detect(Logger<MachineLayout>()));
        _cleanerGuard = new Lazy<PathGuard>(() => CleanerGuard.Create(_layout.Value, SystemCleanEnvironment.Instance, Win32PathResolver.Instance, loggerFactory));
    }

    public ILoggerFactory LoggerFactory { get; }

    public InventoryCollector CreateInventoryCollector()
    {
        var mapper = new UninstallEntryMapper(new InstallerKindDetector(FileExecutableProbe.Instance), SystemCommandEnvironment.Instance);
        var enricher = new InstallLocationEnricher(
            _guard.Value,
            products => new MsiComponentIndex(products, Logger<MsiComponentIndex>()),
            MsiLocationCache.Load(MsiLocationCache.DefaultFile, Logger<MsiLocationCache>()),
            Logger<InstallLocationEnricher>());

        return new InventoryCollector(
            new RegistryUninstallSource(mapper, Logger<RegistryUninstallSource>()),
            new MsiProductSource(Logger<MsiProductSource>()),
            new AppxSource(Logger<AppxSource>()),
            enricher,
            Logger<InventoryCollector>());
    }

    public static FolderSizeService CreateFolderSizeService() => new(new DiskKindProbe());

    public IconCache CreateIconCache(IconExtractor extractor) =>
        new(extractor, IconSourceResolver.ForCurrentMachine(), IconCache.DefaultDirectory, Logger<IconCache>());

    public CommandLineResolver CreateCommandLineResolver() =>
        new(SystemCommandEnvironment.Instance, new AclWritableLocationProbe(Logger<AclWritableLocationProbe>()));

    public UninstallPreflight CreatePreflight() => new(SystemPreflightEnvironment.Instance, _guard.Value, Logger<UninstallPreflight>());

    public FingerprintCollector CreateFingerprintCollector() => new(
        _guard.Value,
        CreateTraceSource(),
        SystemCommandEnvironment.Instance,
        Logger<FingerprintCollector>());

    public FingerprintStore CreateFingerprintStore() => new(FingerprintStore.DefaultDirectory, Logger<FingerprintStore>());

    public PathGuard PathGuard => _guard.Value;

    public IReadOnlyList<string> KnownRoots => [.. FileLeftoverProbe.DefaultRoots(_layout.Value).Select(r => r.Path)];

    public UninstallOrchestrator CreateUninstallOrchestrator(bool restorePoint) => new(
        CreateCommandLineResolver(),
        CreatePreflight(),
        CreateFingerprintCollector(),
        CreateFingerprintStore(),
        new GuardianService(CreateTransactionStore(), CreateRestorePointService(), Logger<GuardianService>(), restorePoint),
        new JobUninstallRunner(new CompletionOptions(), LoggerFactory),
        new PackageManagerRemover(Logger<PackageManagerRemover>()),
        SystemInstallStateProbe.Instance,
        Logger<UninstallOrchestrator>());

    public LeftoverScanner CreateLeftoverScanner()
    {
        SystemTraceSource trace = CreateTraceSource();
        return new LeftoverScanner(
            _guard.Value,
            _registryGuard.Value,
            SystemFileTrustProbe.Instance,
            new FileLeftoverProbe(FileLeftoverProbe.DefaultRoots(_layout.Value), trace, Path.Combine(_layout.Value.WindowsDirectory, "Prefetch")),
            new RegistryLeftoverProbe(trace, Logger<RegistryLeftoverProbe>()),
            new FirewallLeftoverProbe(SystemFirewallRuleSource.Instance),
            new SteamLeftoverProbe(() => SteamLibrary.ClientFolder() is { } client ? new SteamLocations(client) : null),
            new ScheduledTaskLeftoverProbe(trace),
            new PackageLeftoverProbe(PackageLeftoverProbe.DefaultRoot),
            SystemCommandEnvironment.Instance,
            Logger<LeftoverScanner>());
    }

    public TransactionStore CreateTransactionStore() => new(TransactionStore.DefaultDirectory, Logger<TransactionStore>());

    public LeftoverCleaner CreateLeftoverCleaner() => new(
        _guard.Value,
        _registryGuard.Value,
        CreateTransactionStore(),
        CreateRestorePointService(),
        SystemFirewallEditor.Instance,
        SystemScheduledTaskEditor.Instance,
        SystemServiceControl.Instance,
        SystemCommandEnvironment.Instance,
        new EmptyParentFinder(KnownRoots, _guard.Value),
        Logger<LeftoverCleaner>());

    public BatchUninstaller CreateBatchUninstaller() => new(CreateUninstallOrchestrator, CreateSteamUninstaller(), CreateLeftoverScanner(), CreateLeftoverCleaner());

    public TransactionRestorer CreateTransactionRestorer() => new(
        CreateTransactionStore(),
        SystemFirewallEditor.Instance,
        SystemScheduledTaskEditor.Instance,
        Logger<TransactionRestorer>(),
        settings: CreateSettingsEditor());

    public ForceRemover CreateForceRemover() => new(
        CreateFingerprintCollector(),
        CreateLeftoverScanner(),
        CreateLeftoverCleaner(),
        SystemProcessControl.Instance,
        SystemServiceControl.Instance,
        Logger<ForceRemover>());

    public SteamUninstaller CreateSteamUninstaller() => new(
        CreateFingerprintCollector(),
        CreateFingerprintStore(),
        CreateLeftoverCleaner(),
        SystemSteamClient.Instance,
        Logger<SteamUninstaller>());

    public InstallLogStore CreateInstallLogStore() => new(InstallLogStore.DefaultDirectory, Logger<InstallLogStore>());

    public InstallTracker CreateInstallTracker() => new(
        EtwTraceSource.Instance,
        new JobInstallerLauncher(SystemCommandEnvironment.Instance),
        LiveSystemState.Instance,
        new SystemServices(SystemCommandEnvironment.Instance),
        NoiseFilter.ForCurrentMachine(_guard.Value, _registryGuard.Value, SystemCommandEnvironment.Instance),
        CreateInstallLogStore(),
        KernelPathTranslator.ForCurrentUser(),
        Logger<InstallTracker>());

    public LogRollback CreateLogRollback(LeftoverCleaner cleaner) =>
        new(cleaner, CreateInstallLogStore(), LiveSystemState.Instance, Logger<LogRollback>());

    public StartupCatalog CreateStartupCatalog() =>
        new(StartupCatalog.DefaultLocations, CreateLeftoverCleaner(), SystemCommandEnvironment.Instance);

    public RepairProcessRunner CreateRepairRunner() => new(SystemRepairEnvironment.Instance, Logger<RepairProcessRunner>());

    public CleanRuleCatalog LoadRuleCatalog()
    {
        CleanRuleCatalog catalog = CleanRuleCatalog.Load(CleanRuleCatalog.DefaultDirectory);
        ILogger logger = LoggerFactory.CreateLogger("Niskala.Cleaner");
        foreach (RuleProblem problem in catalog.Problems)
        {
            HostLog.RuleProblem(logger, problem.Source, problem.Message);
        }

        return catalog;
    }

    public static CleanScanner CreateCleanScanner() => new(SystemCleanEnvironment.Instance);

    public SystemCleaner CreateSystemCleaner() => new(
        new CleanupJournal(_cleanerGuard.Value, CreateTransactionStore(), CreateRestorePointService(), Logger<CleanupJournal>()),
        SystemCleanEnvironment.Instance,
        Logger<SystemCleaner>());

    public QuickScanner CreateQuickScanner(CleanRuleCatalog catalog) => new(catalog, CreateCleanScanner(), new StartupReview(CreateResourceControl()));

    public RegistryIntegrityScanner CreateIntegrityScanner() => new(
        IntegrityLocations.Default,
        _registryGuard.Value,
        SystemFirewallRuleSource.Instance,
        SystemCommandEnvironment.Instance,
        Logger<RegistryIntegrityScanner>());

    public RegistryIntegrityFixer CreateIntegrityFixer(RegistryIntegrityScanner scanner) => new(scanner, CreateLeftoverCleaner());

    public SettingsEditor CreateSettingsEditor() => new(
        _registryGuard.Value,
        CreateTransactionStore(),
        SystemServiceStartControl.Instance,
        SystemTaskStateControl.Instance,
        Logger<SettingsEditor>());

    public ResourceControl CreateResourceControl() => new(
        new ResourceLocations(),
        CreateStartupCatalog(),
        _registryGuard.Value,
        CreateSettingsEditor(),
        CreateTransactionStore(),
        SystemTaskSource.Instance,
        new SystemProgramFacts(new DiskKindProbe(), _guard.Value),
        SystemCommandEnvironment.Instance);

    public ProcessRestrainer CreateProcessRestrainer() => new(
        SystemProcesses.Instance,
        new ProcessGuard(SystemProcesses.Instance),
        new RestraintStore(RestraintStore.DefaultFile),
        Logger<ProcessRestrainer>());

    public static DriveOptimizer CreateDriveOptimizer() => new(SystemVolumes.Instance);

    public static SizeQueue CreateSizeQueue() => SizeQueue.Create(new DiskKindProbe());

    public UpdateService CreateUpdateService() => new(Logger<UpdateService>());

    public void Dispose() => LoggerFactory.Dispose();

    private WmiRestorePointService CreateRestorePointService() => new(Logger<WmiRestorePointService>());

    private SystemTraceSource CreateTraceSource() => new(SystemCommandEnvironment.Instance, Logger<SystemTraceSource>());

    private ILogger<T> Logger<T>() => LoggerFactory.CreateLogger<T>();
}
