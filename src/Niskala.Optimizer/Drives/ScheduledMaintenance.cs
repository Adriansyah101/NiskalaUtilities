using System.Globalization;
using System.Xml.Linq;
using Niskala.Interop.Tasks;

namespace Niskala.Optimizer.Drives;

public enum MaintenanceSchedule
{
    Off,
    Weekly,
    Monthly,
    WhenIdle,
}

public sealed record ScheduleState(MaintenanceSchedule Schedule, DateTime? NextRun, int Error)
{
    public static ScheduleState Off { get; } = new(MaintenanceSchedule.Off, null, 0);
}

public interface IMaintenanceTasks
{
    int Read(string path, out string? xml);

    int Register(string path, string xml);

    int Delete(string path);

    int NextRun(string path, out DateTime? next);
}

public sealed class SystemMaintenanceTasks : IMaintenanceTasks
{
    private SystemMaintenanceTasks()
    {
    }

    public static SystemMaintenanceTasks Instance { get; } = new();

    public int Read(string path, out string? xml) => TaskSchedulerNative.TryRead(path, out xml);

    public int Register(string path, string xml) => TaskSchedulerNative.TryRegister(path, xml, replace: true);

    public int Delete(string path) => TaskSchedulerNative.TryDelete(path);

    public int NextRun(string path, out DateTime? next) => TaskSchedulerNative.TryGetNextRun(path, out next);
}

public static class ScheduledMaintenance
{
    public const string TaskPath = @"\Niskala Utilities\Scheduled drive optimization";
    public const string Argument = "--scheduled-optimize";
    public const string SystemAccount = "S-1-5-18";
    public const int StartHour = 3;
    public const DayOfWeek WeeklyDay = DayOfWeek.Sunday;
    public const int MonthlyDay = 1;
    public const string IdlePeriod = "P1D";
    public const string IdleDeadline = "P7D";
    public const string TimeLimit = "PT4H";
    public const int NotFound = TaskSchedulerNative.NotFound;

    private const string Version = "1.4";
    private const string Author = "Niskala Project";
    private const string Description = "Niskala Utilities drive maintenance: TRIM for solid-state drives and Quick Defrag for hard disks.";
    private const string Lowest = "7";
    private const string Boundary = "yyyy-MM-dd'T'HH:mm:ss";
    private const string WeekInterval = "1";
    private const string WeeklyDayName = "Sunday";

    private static readonly XNamespace Task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly string[] MonthNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    public static bool IsScheduledRun(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanSchedule(string? program, IEnumerable<string?> trustedRoots)
    {
        ArgumentNullException.ThrowIfNull(trustedRoots);
        if (string.IsNullOrWhiteSpace(program) || !Path.IsPathFullyQualified(program))
        {
            return false;
        }

        string full = Path.GetFullPath(program);
        return trustedRoots
            .Where(r => !string.IsNullOrWhiteSpace(r) && Path.IsPathFullyQualified(r))
            .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r!)) + Path.DirectorySeparatorChar)
            .Any(r => full.StartsWith(r, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<string?> InstalledRoots() =>
        [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)];

    public static DateTime FirstStart(MaintenanceSchedule schedule, DateTime now)
    {
        DateTime day = now.Date.AddDays(1);
        return schedule switch
        {
            MaintenanceSchedule.Weekly => day.AddDays(((int)WeeklyDay - (int)day.DayOfWeek + 7) % 7).AddHours(StartHour),
            MaintenanceSchedule.Monthly => (day.Day == MonthlyDay ? day : new DateTime(day.Year, day.Month, MonthlyDay, 0, 0, 0, day.Kind).AddMonths(1)).AddHours(StartHour),
            _ => day.AddHours(StartHour),
        };
    }

    public static string BuildXml(MaintenanceSchedule schedule, string program, DateTime now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);
        if (schedule == MaintenanceSchedule.Off)
        {
            throw new ArgumentOutOfRangeException(nameof(schedule), schedule, "An Off schedule has no task to register.");
        }

        bool idle = schedule == MaintenanceSchedule.WhenIdle;
        var settings = new XElement(
            Task + "Settings",
            new XElement(Task + "MultipleInstancesPolicy", "IgnoreNew"),
            new XElement(Task + "DisallowStartIfOnBatteries", "true"),
            new XElement(Task + "StopIfGoingOnBatteries", "true"),
            new XElement(Task + "AllowHardTerminate", "true"),
            new XElement(Task + "StartWhenAvailable", "true"),
            new XElement(Task + "RunOnlyIfNetworkAvailable", "false"),
            new XElement(
                Task + "IdleSettings",
                new XElement(Task + "StopOnIdleEnd", idle ? "true" : "false"),
                new XElement(Task + "RestartOnIdle", "false")),
            new XElement(Task + "AllowStartOnDemand", "true"),
            new XElement(Task + "Enabled", "true"),
            new XElement(Task + "Hidden", "false"),
            new XElement(Task + "RunOnlyIfIdle", idle ? "true" : "false"),
            new XElement(Task + "WakeToRun", "false"),
            new XElement(Task + "ExecutionTimeLimit", TimeLimit),
            new XElement(Task + "Priority", Lowest));
        if (idle)
        {
            settings.Add(
                new XElement(Task + "UseUnifiedSchedulingEngine", "true"),
                new XElement(
                    Task + "MaintenanceSettings",
                    new XElement(Task + "Period", IdlePeriod),
                    new XElement(Task + "Deadline", IdleDeadline)));
        }

        var root = new XElement(
            Task + "Task",
            new XAttribute("version", Version),
            new XElement(
                Task + "RegistrationInfo",
                new XElement(Task + "Author", Author),
                new XElement(Task + "Description", Description),
                new XElement(Task + "URI", TaskPath)),
            new XElement(Task + "Triggers", idle ? null : Trigger(schedule, now)),
            new XElement(
                Task + "Principals",
                new XElement(
                    Task + "Principal",
                    new XAttribute("id", "Author"),
                    new XElement(Task + "UserId", SystemAccount),
                    new XElement(Task + "LogonType", "ServiceAccount"),
                    new XElement(Task + "RunLevel", "HighestAvailable"))),
            settings,
            new XElement(
                Task + "Actions",
                new XAttribute("Context", "Author"),
                new XElement(
                    Task + "Exec",
                    new XElement(Task + "Command", program),
                    new XElement(Task + "Arguments", Argument),
                    new XElement(Task + "WorkingDirectory", Path.GetDirectoryName(program) ?? string.Empty))));
        return new XDocument(new XDeclaration("1.0", "UTF-16", null), root).Declaration + Environment.NewLine + root;
    }

    public static MaintenanceSchedule Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml))
        {
            return MaintenanceSchedule.Off;
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return MaintenanceSchedule.Off;
        }

        XElement? root = document.Root;
        bool ours = root?.Descendants(Task + "Arguments").Any(a => a.Value.Contains(Argument, StringComparison.OrdinalIgnoreCase)) == true;
        if (root is null || !ours)
        {
            return MaintenanceSchedule.Off;
        }

        if (root.Descendants(Task + "MaintenanceSettings").Any())
        {
            return MaintenanceSchedule.WhenIdle;
        }

        return root.Descendants(Task + "ScheduleByMonth").Any() ? MaintenanceSchedule.Monthly
            : root.Descendants(Task + "ScheduleByWeek").Any() ? MaintenanceSchedule.Weekly
            : MaintenanceSchedule.Off;
    }

    private static XElement Trigger(MaintenanceSchedule schedule, DateTime now)
    {
        string start = FirstStart(schedule, now).ToString(Boundary, CultureInfo.InvariantCulture);
        return schedule == MaintenanceSchedule.Monthly
            ? new XElement(
                Task + "CalendarTrigger",
                new XElement(Task + "StartBoundary", start),
                new XElement(Task + "Enabled", "true"),
                new XElement(
                    Task + "ScheduleByMonth",
                    new XElement(Task + "DaysOfMonth", new XElement(Task + "Day", MonthlyDay.ToString(CultureInfo.InvariantCulture))),
                    new XElement(Task + "Months", MonthNames.Select(m => new XElement(Task + m)))))
            : new XElement(
                Task + "CalendarTrigger",
                new XElement(Task + "StartBoundary", start),
                new XElement(Task + "Enabled", "true"),
                new XElement(
                    Task + "ScheduleByWeek",
                    new XElement(Task + "DaysOfWeek", new XElement(Task + WeeklyDayName)),
                    new XElement(Task + "WeeksInterval", WeekInterval)));
    }
}

public sealed class MaintenanceScheduler(IMaintenanceTasks tasks, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ScheduleState Read()
    {
        int error = tasks.Read(ScheduledMaintenance.TaskPath, out string? xml);
        if (error == ScheduledMaintenance.NotFound)
        {
            return ScheduleState.Off;
        }

        if (error != 0)
        {
            return ScheduleState.Off with { Error = error };
        }

        MaintenanceSchedule schedule = ScheduledMaintenance.Parse(xml);
        DateTime? next = schedule != MaintenanceSchedule.Off && tasks.NextRun(ScheduledMaintenance.TaskPath, out DateTime? when) == 0 ? when : null;
        return new ScheduleState(schedule, next, 0);
    }

    public ScheduleState Apply(MaintenanceSchedule schedule, string program)
    {
        if (schedule == MaintenanceSchedule.Off)
        {
            int removed = tasks.Delete(ScheduledMaintenance.TaskPath);
            return removed is 0 or ScheduledMaintenance.NotFound ? ScheduleState.Off : Read() with { Error = removed };
        }

        string xml = ScheduledMaintenance.BuildXml(schedule, program, _clock.GetLocalNow().DateTime);
        int error = tasks.Register(ScheduledMaintenance.TaskPath, xml);
        return error == 0 ? Read() : Read() with { Error = error };
    }
}

public static class ScheduledOptimization
{
    public const string MutexName = @"Global\NiskalaUtilities.ScheduledOptimization";

    public static IReadOnlyList<QueueStep> Plan(IEnumerable<DriveVolume> volumes)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        return [.. DriveQueue.Plan(volumes, QueueKind.Optimize, DefragMode.Quick).Where(IsAllowed)];
    }

    public static bool IsAllowed(QueueStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Kind switch
        {
            QueueStepKind.Trim => step.Volume.Media == MediaKind.Ssd && DriveQueue.IsAllowed(step),
            QueueStepKind.Defragment => step.Volume.Media == MediaKind.Hdd && step.Mode == DefragMode.Quick && DriveQueue.IsAllowed(step),
            QueueStepKind.Skip => true,
            _ => false,
        };
    }

    public static async Task<IReadOnlyList<QueueResult>> RunAsync(DriveOptimizer optimizer, IProgress<QueueProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(optimizer);
        IReadOnlyList<QueueStep> steps = Plan(optimizer.Volumes());
        return await new DriveQueueRunner(optimizer).RunAsync(steps, progress, cancellationToken).ConfigureAwait(false);
    }

    public static int ExitCode(IReadOnlyList<QueueResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return results.Any(r => r.Outcome is QueueOutcome.Failed or QueueOutcome.Refused) ? 1 : 0;
    }
}
