using System.Collections.Concurrent;
using System.Diagnostics;
using Niskala.Interop.Volumes;
using Niskala.Optimizer.Drives;

namespace Niskala.Optimizer.Tests;

public sealed class DriveLoadingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void The_shell_lists_every_drive_before_any_media_probe_finishes()
    {
        using var drives = new SlowDrives(('C', MediaKind.Ssd, StorageBus.Nvme), ('E', MediaKind.Hdd, StorageBus.Usb));
        drives.Hold('E');
        var loader = new DriveListLoader(new DriveOptimizer(drives));

        var clock = Stopwatch.StartNew();
        IReadOnlyList<DriveVolume> shell = loader.Shell();
        clock.Stop();

        Assert.Equal(['C', 'E'], shell.Select(v => v.Letter));
        Assert.All(shell, v => Assert.False(v.Probed));
        Assert.All(shell, v => Assert.Equal(MediaKind.Unknown, v.Media));
        Assert.True(clock.Elapsed < TimeSpan.FromMilliseconds(50), $"The shell took {clock.Elapsed.TotalMilliseconds} ms.");
        Assert.Equal(0, drives.ProbeCount);
    }

    [Fact]
    public async Task Every_drive_is_probed_at_the_same_time()
    {
        using var drives = new SlowDrives(('C', MediaKind.Ssd, StorageBus.Nvme), ('D', MediaKind.Hdd, StorageBus.Sata), ('E', MediaKind.Hdd, StorageBus.Usb));
        using var together = new Barrier(3);
        drives.OnProbe = _ => Assert.True(together.SignalAndWait(Patience), "The probes ran one after another.");
        var loader = new DriveListLoader(new DriveOptimizer(drives, health: new FakeHealth()));
        var updates = new ConcurrentQueue<DriveProbeUpdate>();

        await loader.ProbeAllAsync(loader.Shell(), updates.Enqueue, Token).WaitAsync(Patience, Token);

        Assert.Equal(3, updates.Count(u => u.Stage == ProbeStage.Media));
        Assert.Equal(3, updates.Count(u => u.Stage == ProbeStage.Health));
        Assert.Equal(MediaKind.Hdd, updates.Single(u => u.Stage == ProbeStage.Media && u.Volume.Letter == 'E').Volume.Media);
        Assert.All(updates.Where(u => u.Stage == ProbeStage.Health), u => Assert.True(u.Volume.Probed));
    }

    [Fact]
    public async Task A_drive_that_is_spinning_up_shows_as_waiting_and_lands_when_it_answers()
    {
        using var drives = new SlowDrives(('C', MediaKind.Ssd, StorageBus.Nvme), ('E', MediaKind.Hdd, StorageBus.Usb));
        drives.Hold('E');
        var loader = new DriveListLoader(new DriveOptimizer(drives, health: new FakeHealth()), TimeSpan.FromMilliseconds(30));
        var updates = new ConcurrentQueue<DriveProbeUpdate>();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        Task load = loader.ProbeAllAsync(loader.Shell(), u =>
        {
            updates.Enqueue(u);
            if (u.Volume.Letter == 'E' && u.Stage == ProbeStage.Slow)
            {
                waiting.TrySetResult();
            }

            if (u.Volume.Letter == 'C' && u.Stage == ProbeStage.Health)
            {
                ready.TrySetResult();
            }
        }, Token);
        await Task.WhenAll(waiting.Task, ready.Task).WaitAsync(Patience, Token);

        Assert.DoesNotContain(updates, u => u.Volume.Letter == 'E' && u.Stage == ProbeStage.Media);
        Assert.False(load.IsCompleted);
        drives.Release('E');
        await load.WaitAsync(Patience, Token);

        DriveProbeUpdate[] e = [.. updates.Where(u => u.Volume.Letter == 'E')];
        Assert.Equal([ProbeStage.Slow, ProbeStage.Media, ProbeStage.Health], e.Select(u => u.Stage));
        Assert.Equal(MediaKind.Hdd, e[1].Volume.Media);
    }

    [Fact]
    public async Task A_drive_that_fails_to_answer_is_marked_and_the_others_carry_on()
    {
        using var drives = new SlowDrives(('C', MediaKind.Ssd, StorageBus.Nvme), ('E', MediaKind.Hdd, StorageBus.Usb));
        drives.Broken.Add('E');
        var loader = new DriveListLoader(new DriveOptimizer(drives, health: new FakeHealth()));
        var updates = new ConcurrentQueue<DriveProbeUpdate>();

        await loader.ProbeAllAsync(loader.Shell(), updates.Enqueue, Token).WaitAsync(Patience, Token);

        DriveProbeUpdate failed = Assert.Single(updates, u => u.Volume.Letter == 'E');
        Assert.Equal(ProbeStage.Failed, failed.Stage);
        Assert.Equal("unplugged", failed.Error);
        Assert.Equal([ProbeStage.Media, ProbeStage.Health], updates.Where(u => u.Volume.Letter == 'C').Select(u => u.Stage));
    }

    [Fact]
    public async Task The_session_cache_answers_the_next_visit_without_touching_the_drives()
    {
        using var drives = new SlowDrives(('C', MediaKind.Ssd, StorageBus.Nvme), ('E', MediaKind.Hdd, StorageBus.Usb));
        var health = new FakeHealth();
        var optimizer = new DriveOptimizer(drives, health: health);
        var loader = new DriveListLoader(optimizer);
        await loader.ProbeAllAsync(loader.Shell(), _ => { }, Token).WaitAsync(Patience, Token);

        IReadOnlyList<DriveVolume> again = loader.Shell();
        var updates = new ConcurrentQueue<DriveProbeUpdate>();
        await loader.ProbeAllAsync(again, updates.Enqueue, Token).WaitAsync(Patience, Token);

        Assert.All(again, v => Assert.True(v.Probed));
        Assert.Equal([MediaKind.Ssd, MediaKind.Hdd], again.Select(v => v.Media));
        Assert.Equal(StorageBus.Usb, again[1].Bus);
        Assert.Equal(2, drives.ProbeCount);
        Assert.Equal(2, health.Reads);
        Assert.Equal(2, updates.Count(u => u.Stage == ProbeStage.Health));
        Assert.DoesNotContain(updates, u => u.Stage == ProbeStage.Media);
        Assert.True(optimizer.Media.TryGetHealth(again[1], out DriveHealth cached));
        Assert.Equal(35, cached.TemperatureCelsius);
    }

    [Fact]
    public async Task Unplugging_or_reformatting_a_drive_drops_what_was_cached_for_it()
    {
        using var drives = new SlowDrives(('C', MediaKind.Ssd, StorageBus.Nvme), ('E', MediaKind.Hdd, StorageBus.Usb));
        var optimizer = new DriveOptimizer(drives);
        var loader = new DriveListLoader(optimizer);
        await loader.ProbeAllAsync(loader.Shell(), _ => { }, Token).WaitAsync(Patience, Token);
        Assert.Equal(2, optimizer.Media.Count);

        drives.Unplug('E');
        Assert.Single(loader.Shell());
        Assert.Equal(1, optimizer.Media.Count);

        drives.Plug('E', serial: 0xBEEF);
        IReadOnlyList<DriveVolume> shell = loader.Shell();
        Assert.False(shell.Single(v => v.Letter == 'E').Probed);
        await loader.ProbeAllAsync(shell, _ => { }, Token).WaitAsync(Patience, Token);
        Assert.Equal(3, drives.ProbeCount);
    }

    [Fact]
    public async Task Refresh_reads_telemetry_again_but_keeps_the_media_type()
    {
        using var drives = new SlowDrives(('E', MediaKind.Hdd, StorageBus.Usb));
        var health = new FakeHealth();
        var optimizer = new DriveOptimizer(drives, health: health);
        var loader = new DriveListLoader(optimizer);
        await loader.ProbeAllAsync(loader.Shell(), _ => { }, Token).WaitAsync(Patience, Token);

        optimizer.Media.ForgetHealth();
        await loader.ProbeAllAsync(loader.Shell(), _ => { }, Token).WaitAsync(Patience, Token);

        Assert.Equal(1, drives.ProbeCount);
        Assert.Equal(2, health.Reads);
    }

    [Fact]
    public async Task A_failed_probe_is_tried_again_on_the_next_load()
    {
        using var drives = new SlowDrives(('E', MediaKind.Hdd, StorageBus.Usb));
        drives.Broken.Add('E');
        var optimizer = new DriveOptimizer(drives);
        var loader = new DriveListLoader(optimizer);
        await loader.ProbeAllAsync(loader.Shell(), _ => { }, Token).WaitAsync(Patience, Token);

        drives.Broken.Clear();
        var updates = new ConcurrentQueue<DriveProbeUpdate>();
        await loader.ProbeAllAsync(loader.Shell(), updates.Enqueue, Token).WaitAsync(Patience, Token);

        Assert.Equal(2, drives.ProbeCount);
        Assert.Equal(MediaKind.Hdd, updates.Single(u => u.Stage == ProbeStage.Media).Volume.Media);
    }

    [Fact]
    public async Task Two_pages_asking_at_once_share_one_probe()
    {
        using var drives = new SlowDrives(('E', MediaKind.Hdd, StorageBus.Usb));
        drives.Hold('E');
        var optimizer = new DriveOptimizer(drives);
        DriveVolume shell = optimizer.ListVolumes().Single();

        Task<DriveVolume> first = optimizer.ProbeAsync(shell);
        Task<DriveVolume> second = optimizer.ProbeAsync(shell);
        drives.Release('E');
        DriveVolume[] probed = await Task.WhenAll(first, second).WaitAsync(Patience, Token);

        Assert.Equal(1, drives.ProbeCount);
        Assert.All(probed, v => Assert.Equal(MediaKind.Hdd, v.Media));
    }

    [Fact]
    public async Task Leaving_the_page_stops_waiting_but_the_answer_still_lands_in_the_cache()
    {
        using var drives = new SlowDrives(('E', MediaKind.Hdd, StorageBus.Usb));
        drives.Hold('E');
        var optimizer = new DriveOptimizer(drives);
        var loader = new DriveListLoader(optimizer, TimeSpan.FromMilliseconds(20));
        using var leave = CancellationTokenSource.CreateLinkedTokenSource(Token);
        DriveVolume shell = loader.Shell().Single();

        Task load = loader.ProbeAllAsync([shell], _ => { }, leave.Token);
        await leave.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(Patience, Token));

        drives.Release('E');
        await optimizer.ProbeAsync(shell).WaitAsync(Patience, Token);
        Assert.True(optimizer.Media.TryGetMedia(shell, out MediaProbe probe));
        Assert.Equal(MediaKind.Hdd, probe.Media);
        Assert.Equal(1, drives.ProbeCount);
    }

    [Fact]
    public void Probed_volumes_keep_their_identity_and_gain_the_media_facts()
    {
        var basic = new DriveVolume('E', "Backup", "NTFS", 4096, 1_000, 4_096_000, 1_000_000, MediaKind.Unknown, false, SerialNumber: 7, Probed: false);
        DriveVolume full = new MediaProbe(MediaKind.Hdd, false, StorageBus.Usb, 4800).ApplyTo(basic);

        Assert.True(full.Probed);
        Assert.Equal((MediaKind.Hdd, StorageBus.Usb, 4800), (full.Media, full.Bus, full.RotationRate));
        Assert.Equal((basic.Letter, basic.SerialNumber, basic.TotalClusters, basic.Label), (full.Letter, full.SerialNumber, full.TotalClusters, full.Label));
        Assert.True(VolumeMediaCache.SameVolume(basic, full));
        Assert.False(VolumeMediaCache.SameVolume(basic, full with { SerialNumber = 8 }));
        Assert.False(basic.CanRelocate);
        Assert.Equal(DriveAction.AnalyzeOnly, basic.DefaultAction);
    }

    private sealed class FakeHealth : IDriveHealthSource
    {
        private int _reads;

        public int Reads => _reads;

        public DriveHealth Read(DriveVolume volume)
        {
            Interlocked.Increment(ref _reads);
            return new DriveHealth(HealthSource.Ata, HealthWarning.None, 35, 100, null, null);
        }
    }

    private sealed class SlowDrives : IVolumeSystem, IDisposable
    {
        private readonly ConcurrentDictionary<char, DriveVolume> _volumes = new();
        private readonly ConcurrentDictionary<char, ManualResetEventSlim> _gates = new();
        private readonly ConcurrentDictionary<char, DriveVolume> _unplugged = new();
        private int _probes;

        public SlowDrives(params (char Letter, MediaKind Media, StorageBus Bus)[] drives)
        {
            foreach ((char letter, MediaKind media, StorageBus bus) in drives)
            {
                _volumes[letter] = new DriveVolume(letter, "Disk", "NTFS", 4096, 1_000, 4_096_000, 1_000_000, media, media == MediaKind.Ssd, bus, SerialNumber: letter);
            }
        }

        public int ProbeCount => _probes;

        public ConcurrentBag<char> Broken { get; } = [];

        public Action<char>? OnProbe { get; set; }

        public void Hold(char letter) => _gates[letter] = new ManualResetEventSlim(false);

        public void Release(char letter) => _gates[letter].Set();

        public void Unplug(char letter)
        {
            if (_volumes.TryRemove(letter, out DriveVolume? volume))
            {
                _unplugged[letter] = volume;
            }
        }

        public void Plug(char letter, uint serial)
        {
            if (_unplugged.TryRemove(letter, out DriveVolume? volume))
            {
                _volumes[letter] = volume with { SerialNumber = serial };
            }
        }

        public IReadOnlyList<DriveVolume> FixedVolumes() => [.. _volumes.Values.OrderBy(v => v.Letter)];

        public IReadOnlyList<DriveVolume> ListVolumes() =>
            [.. FixedVolumes().Select(v => v with { Media = MediaKind.Unknown, TrimSupported = false, Bus = StorageBus.Unknown, RotationRate = 0, Probed = false })];

        public MediaProbe ProbeMedia(DriveVolume volume)
        {
            Interlocked.Increment(ref _probes);
            OnProbe?.Invoke(volume.Letter);
            if (_gates.TryGetValue(volume.Letter, out ManualResetEventSlim? gate))
            {
                gate.Wait(Patience);
            }

            if (Broken.Contains(volume.Letter))
            {
                throw new IOException("unplugged");
            }

            return MediaProbe.Of(_volumes[volume.Letter]);
        }

        public int TryOpen(DriveVolume volume, out IVolumeSession? session)
        {
            session = null;
            return VolumeNative.ErrorNotSupported;
        }

        public void EnterBackground()
        {
        }

        public void LeaveBackground()
        {
        }

        public int RetrimWithSystemTool(DriveVolume volume, Action<double> percent, CancellationToken cancellationToken) => 0;

        public void Dispose()
        {
            foreach (ManualResetEventSlim gate in _gates.Values)
            {
                gate.Set();
                gate.Dispose();
            }
        }
    }
}
