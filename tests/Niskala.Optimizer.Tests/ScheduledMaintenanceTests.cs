using System.Xml.Linq;
using Niskala.Interop.Volumes;
using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class ScheduledMaintenanceTests
{
    private const string Program = @"C:\Program Files\Niskala Utilities\lib\Niskala.exe";
    private static readonly XNamespace Task = "http://schemas.microsoft.com/windows/2004/02/mit/task";
    private static readonly DateTime Friday = new(2026, 11, 27, 14, 30, 0, DateTimeKind.Local);

    private static XElement Root(MaintenanceSchedule schedule) => XDocument.Parse(ScheduledMaintenance.BuildXml(schedule, Program, Friday)).Root!;

    private static string Value(XElement root, string name) => root.Descendants(Task + name).Single().Value;

    [Theory]
    [InlineData(MaintenanceSchedule.Weekly, "2026-11-29T03:00:00")]
    [InlineData(MaintenanceSchedule.Monthly, "2026-12-01T03:00:00")]
    public void Calendar_schedules_start_at_three_in_the_morning_on_the_next_matching_day(MaintenanceSchedule schedule, string start)
    {
        XElement root = Root(schedule);

        Assert.Equal(start, Value(root, "StartBoundary"));
        Assert.Equal("true", root.Descendants(Task + "CalendarTrigger").Single().Element(Task + "Enabled")!.Value);
        Assert.Equal(schedule == MaintenanceSchedule.Weekly, root.Descendants(Task + "Sunday").Any());
        Assert.Equal(schedule == MaintenanceSchedule.Monthly ? 12 : 0, root.Descendants(Task + "Months").Elements().Count());
        Assert.Empty(root.Descendants(Task + "MaintenanceSettings"));
        Assert.Equal("false", Value(root, "RunOnlyIfIdle"));
    }

    [Fact]
    public void When_idle_uses_windows_idle_maintenance_with_no_fixed_trigger()
    {
        XElement root = Root(MaintenanceSchedule.WhenIdle);

        Assert.Empty(root.Element(Task + "Triggers")!.Elements());
        Assert.Equal(ScheduledMaintenance.IdlePeriod, Value(root, "Period"));
        Assert.Equal(ScheduledMaintenance.IdleDeadline, Value(root, "Deadline"));
        Assert.Equal("true", Value(root, "UseUnifiedSchedulingEngine"));
        Assert.Equal("true", Value(root, "RunOnlyIfIdle"));
        Assert.Equal("true", Value(root, "StopOnIdleEnd"));
    }

    [Theory]
    [InlineData(MaintenanceSchedule.Weekly)]
    [InlineData(MaintenanceSchedule.Monthly)]
    [InlineData(MaintenanceSchedule.WhenIdle)]
    public void Every_schedule_runs_the_installed_program_quietly_as_system_without_a_background_service(MaintenanceSchedule schedule)
    {
        XElement root = Root(schedule);

        Assert.Equal(Program, Value(root, "Command"));
        Assert.Equal(ScheduledMaintenance.Argument, Value(root, "Arguments"));
        Assert.Equal(@"C:\Program Files\Niskala Utilities\lib", Value(root, "WorkingDirectory"));
        Assert.Equal(ScheduledMaintenance.SystemAccount, Value(root, "UserId"));
        Assert.Equal("HighestAvailable", Value(root, "RunLevel"));
        Assert.Equal("IgnoreNew", Value(root, "MultipleInstancesPolicy"));
        Assert.Equal("true", Value(root, "DisallowStartIfOnBatteries"));
        Assert.Equal(ScheduledMaintenance.TimeLimit, Value(root, "ExecutionTimeLimit"));
        Assert.Equal(ScheduledMaintenance.TaskPath, Value(root, "URI"));
        Assert.Equal("Niskala Project", Value(root, "Author"));
        Assert.Contains("TRIM", Value(root, "Description"), StringComparison.Ordinal);
        Assert.Empty(root.Descendants(Task + "ComHandler"));
    }

    [Fact]
    public void An_off_schedule_has_no_task_to_build()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ScheduledMaintenance.BuildXml(MaintenanceSchedule.Off, Program, Friday));
        Assert.Throws<ArgumentException>(() => ScheduledMaintenance.BuildXml(MaintenanceSchedule.Weekly, " ", Friday));
    }

    [Theory]
    [InlineData(MaintenanceSchedule.Weekly)]
    [InlineData(MaintenanceSchedule.Monthly)]
    [InlineData(MaintenanceSchedule.WhenIdle)]
    public void A_registered_task_reads_back_as_the_schedule_that_built_it(MaintenanceSchedule schedule) =>
        Assert.Equal(schedule, ScheduledMaintenance.Parse(ScheduledMaintenance.BuildXml(schedule, Program, Friday)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<not xml")]
    [InlineData("<Task xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"><Actions><Exec><Command>defrag.exe</Command><Arguments>-c</Arguments></Exec></Actions><Settings><MaintenanceSettings /></Settings></Task>")]
    public void Foreign_or_broken_task_xml_counts_as_no_schedule(string? xml) => Assert.Equal(MaintenanceSchedule.Off, ScheduledMaintenance.Parse(xml));

    [Theory]
    [InlineData(new[] { "--scheduled-optimize" }, true)]
    [InlineData(new[] { "--SCHEDULED-OPTIMIZE" }, true)]
    [InlineData(new[] { "--page", "drives" }, false)]
    [InlineData(new[] { "scheduled-optimize" }, false)]
    public void Only_the_scheduled_argument_starts_a_quiet_run(string[] arguments, bool expected) =>
        Assert.Equal(expected, ScheduledMaintenance.IsScheduledRun(arguments));

    [Theory]
    [InlineData(@"C:\Program Files\Niskala Utilities\lib\Niskala.exe", true)]
    [InlineData(@"C:\PROGRAM FILES\Niskala Utilities\Niskala.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Niskala\Niskala.exe", true)]
    [InlineData(@"C:\Program Files Evil\Niskala.exe", false)]
    [InlineData(@"D:\Builds\Niskala.exe", false)]
    [InlineData(@"lib\Niskala.exe", false)]
    [InlineData(null, false)]
    public void Only_a_copy_under_program_files_may_be_scheduled_as_system(string? program, bool expected) =>
        Assert.Equal(expected, ScheduledMaintenance.CanSchedule(program, [@"C:\Program Files", @"C:\Program Files (x86)\", null, "relative"]));

    [Fact]
    public void Turning_the_schedule_on_registers_the_task_and_reads_its_next_run()
    {
        var tasks = new FakeTasks();
        var scheduler = new MaintenanceScheduler(tasks, new FixedClock());

        ScheduleState state = scheduler.Apply(MaintenanceSchedule.Weekly, Program);

        Assert.Equal(MaintenanceSchedule.Weekly, state.Schedule);
        Assert.Equal(0, state.Error);
        Assert.Equal(FakeTasks.Next, state.NextRun);
        Assert.Equal(ScheduledMaintenance.TaskPath, tasks.Registered.Single().Path);
        Assert.Equal(MaintenanceSchedule.Weekly, ScheduledMaintenance.Parse(tasks.Registered.Single().Xml));
        Assert.Contains("2026-11-29T03:00:00", tasks.Registered.Single().Xml, StringComparison.Ordinal);
    }

    [Fact]
    public void Switching_schedules_replaces_the_one_task_and_off_removes_it()
    {
        var tasks = new FakeTasks();
        var scheduler = new MaintenanceScheduler(tasks, new FixedClock());
        scheduler.Apply(MaintenanceSchedule.Weekly, Program);
        scheduler.Apply(MaintenanceSchedule.WhenIdle, Program);

        Assert.Equal(MaintenanceSchedule.WhenIdle, scheduler.Read().Schedule);
        Assert.Equal(2, tasks.Registered.Count);
        Assert.All(tasks.Registered, r => Assert.Equal(ScheduledMaintenance.TaskPath, r.Path));

        ScheduleState off = scheduler.Apply(MaintenanceSchedule.Off, Program);
        Assert.Equal(ScheduleState.Off, off);
        Assert.Equal(1, tasks.Deleted);
        Assert.Equal(ScheduleState.Off, scheduler.Read());
        Assert.Equal(ScheduleState.Off, scheduler.Apply(MaintenanceSchedule.Off, Program));
    }

    [Fact]
    public void A_refused_registration_keeps_the_old_state_and_reports_the_error()
    {
        var tasks = new FakeTasks { RegisterError = unchecked((int)0x80070005) };
        var scheduler = new MaintenanceScheduler(tasks, new FixedClock());

        ScheduleState state = scheduler.Apply(MaintenanceSchedule.Monthly, Program);

        Assert.Equal(MaintenanceSchedule.Off, state.Schedule);
        Assert.Equal(unchecked((int)0x80070005), state.Error);
    }

    [Fact]
    public void An_unreadable_scheduler_is_reported_as_off_with_its_error()
    {
        var tasks = new FakeTasks { ReadError = 42 };
        Assert.Equal(ScheduleState.Off with { Error = 42 }, new MaintenanceScheduler(tasks).Read());
    }

    [Fact]
    public void The_scheduled_plan_gives_ssds_trim_and_hard_disks_quick_defrag_only()
    {
        FakeDisk ssd = new FakeDisk(1_000, MediaKind.Ssd, letter: 'C'), hdd = new FakeDisk(1_000, letter: 'D');
        FakeDisk noTrim = new(1_000, MediaKind.Ssd, trim: false, letter: 'E'), unknown = new(100, MediaKind.Unknown, letter: 'F');

        IReadOnlyList<QueueStep> steps = ScheduledOptimization.Plan([hdd.Volume, ssd.Volume, noTrim.Volume, unknown.Volume]);

        Assert.Equal(
            [('C', QueueStepKind.Trim), ('D', QueueStepKind.Defragment), ('E', QueueStepKind.Skip), ('F', QueueStepKind.Skip)],
            steps.Select(s => (s.Volume.Letter, s.Kind)));
        Assert.All(steps.Where(s => s.Kind == QueueStepKind.Defragment), s => Assert.Equal(DefragMode.Quick, s.Mode));
    }

    [Fact]
    public void The_scheduled_guard_refuses_every_forged_step()
    {
        DriveVolume ssd = new FakeDisk(10, MediaKind.Ssd).Volume, hdd = new FakeDisk(10).Volume;

        Assert.False(ScheduledOptimization.IsAllowed(new QueueStep(hdd, QueueStepKind.Defragment, DefragMode.FullZone)));
        Assert.False(ScheduledOptimization.IsAllowed(new QueueStep(hdd, QueueStepKind.Defragment, DefragMode.Consolidate)));
        Assert.False(ScheduledOptimization.IsAllowed(new QueueStep(ssd, QueueStepKind.Defragment, DefragMode.Quick)));
        Assert.False(ScheduledOptimization.IsAllowed(new QueueStep(hdd, QueueStepKind.Trim)));
        Assert.False(ScheduledOptimization.IsAllowed(new QueueStep(hdd, QueueStepKind.Analyze)));
        Assert.True(ScheduledOptimization.IsAllowed(new QueueStep(hdd, QueueStepKind.Defragment, DefragMode.Quick)));
        Assert.True(ScheduledOptimization.IsAllowed(new QueueStep(ssd, QueueStepKind.Trim)));
        Assert.True(ScheduledOptimization.IsAllowed(new QueueStep(ssd, QueueStepKind.Skip, Skip: QueueSkipReason.NoTrim)));
    }

    [Fact]
    public async Task A_scheduled_run_trims_the_ssd_and_quick_defragments_the_hard_disk()
    {
        FakeDisk ssd = new FakeDisk(1_000, MediaKind.Ssd, letter: 'C').File("a.dat", (10, 4));
        FakeDisk hdd = new FakeDisk(1_000, letter: 'D').File("split.dat", (10, 4), (500, 4)).File("other.dat", (100, 8));
        var optimizer = new DriveOptimizer(new Pair(ssd, hdd));

        IReadOnlyList<QueueResult> results = await ScheduledOptimization.RunAsync(optimizer, null, TestContext.Current.CancellationToken);

        Assert.Equal([QueueStepKind.Trim, QueueStepKind.Defragment], results.Select(r => r.Step.Kind));
        Assert.Equal(DefragMode.Quick, results[1].Defrag?.Mode);
        Assert.Empty(ssd.Moves);
        Assert.NotEmpty(hdd.Moves);
        Assert.Equal(0, ScheduledOptimization.ExitCode(results));
        Assert.Equal(1, ScheduledOptimization.ExitCode([.. results, new QueueResult(results[0].Step, QueueOutcome.Failed)]));
    }

    [Theory]
    [InlineData(2026, 11, 27, "2026-11-29")]
    [InlineData(2026, 11, 28, "2026-11-29")]
    [InlineData(2026, 11, 29, "2026-12-06")]
    [InlineData(2026, 12, 31, "2027-01-03")]
    public void The_first_weekly_run_is_the_next_sunday_after_today(int year, int month, int day, string expected) =>
        Assert.Equal(expected, ScheduledMaintenance.FirstStart(MaintenanceSchedule.Weekly, new DateTime(year, month, day, 10, 0, 0, DateTimeKind.Local)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData(2026, 11, 27, "2026-12-01")]
    [InlineData(2026, 11, 30, "2026-12-01")]
    [InlineData(2026, 12, 1, "2027-01-01")]
    [InlineData(2026, 12, 31, "2027-01-01")]
    public void The_first_monthly_run_is_the_next_first_of_a_month_after_today(int year, int month, int day, string expected) =>
        Assert.Equal(expected, ScheduledMaintenance.FirstStart(MaintenanceSchedule.Monthly, new DateTime(year, month, day, 10, 0, 0, DateTimeKind.Local)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(Friday).ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;
    }

    private sealed class FakeTasks : IMaintenanceTasks
    {
        public static readonly DateTime Next = new(2026, 11, 29, 3, 0, 0);

        private string? _xml;

        public int RegisterError { get; init; }

        public int ReadError { get; init; }

        public List<(string Path, string Xml)> Registered { get; } = [];

        public int Deleted { get; private set; }

        public int Read(string path, out string? xml)
        {
            xml = _xml;
            return ReadError != 0 ? ReadError : _xml is null ? ScheduledMaintenance.NotFound : 0;
        }

        public int Register(string path, string xml)
        {
            if (RegisterError != 0)
            {
                return RegisterError;
            }

            Registered.Add((path, xml));
            _xml = xml;
            return 0;
        }

        public int Delete(string path)
        {
            if (_xml is null)
            {
                return ScheduledMaintenance.NotFound;
            }

            Deleted++;
            _xml = null;
            return 0;
        }

        public int NextRun(string path, out DateTime? next)
        {
            next = Next;
            return _xml is null ? ScheduledMaintenance.NotFound : 0;
        }
    }

    private sealed class Pair(params FakeDisk[] disks) : IVolumeSystem
    {
        public IReadOnlyList<DriveVolume> FixedVolumes() => [.. disks.Select(d => d.Volume)];

        public int TryOpen(DriveVolume volume, out IVolumeSession? session) => Disk(volume).TryOpen(volume, out session);

        public void EnterBackground()
        {
        }

        public void LeaveBackground()
        {
        }

        public int RetrimWithSystemTool(DriveVolume volume, Action<double> percent, CancellationToken cancellationToken) =>
            Disk(volume).RetrimWithSystemTool(volume, percent, cancellationToken);

        private FakeDisk Disk(DriveVolume volume) => disks.Single(d => d.Volume.Letter == volume.Letter);
    }
}
