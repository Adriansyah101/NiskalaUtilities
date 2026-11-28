using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using Niskala.Interop.Volumes;

namespace Niskala.Optimizer.Drives;

public readonly record struct VolumeEntry(string Name, bool IsDirectory, bool IsReparsePoint, long Size, DateTime LastWriteUtc);

public interface IFileMover : IDisposable
{
    int Move(long vcn, long lcn, uint clusters);
}

public interface IVolumeSession : IDisposable
{
    ClusterBitmap ReadBitmap();

    int List(string directory, List<VolumeEntry> entries);

    int ReadExtents(string path, List<Extent> extents);

    int BeginMove(string path, out IFileMover? mover);

    int ReadAllocated(long start, long clusters, List<FreeRun> allocated);

    long FileRecords();

    IoSample? SampleIo();

    int TrimFreeSpace(long bytes, long chunk, Action<long> trimmed, CancellationToken cancellationToken);
}

public interface IVolumeSystem
{
    IReadOnlyList<DriveVolume> FixedVolumes();

    int TryOpen(DriveVolume volume, out IVolumeSession? session);

    void EnterBackground();

    void LeaveBackground();

    int RetrimWithSystemTool(DriveVolume volume, Action<double> percent, CancellationToken cancellationToken);
}

public static partial class RetrimOutput
{
    public static double? Percent(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        Match match = PercentPattern().Match(line);
        return match.Success && int.TryParse(match.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value <= 100 ? value : null;
    }

    [GeneratedRegex(@"(?<!\d)(\d{1,3})\s?%")]
    private static partial Regex PercentPattern();
}

public static class AllocatedRuns
{
    public static void Collect(ReadOnlySpan<byte> bits, long firstLcn, long start, long clusters, List<FreeRun> allocated)
    {
        ArgumentNullException.ThrowIfNull(allocated);
        long end = Math.Min(start + clusters, firstLcn + ((long)bits.Length * 8));
        long run = -1;
        for (long lcn = Math.Max(start, firstLcn); lcn < end; lcn++)
        {
            long bit = lcn - firstLcn;
            bool used = (bits[(int)(bit / 8)] & (1 << (int)(bit % 8))) != 0;
            if (used && run < 0)
            {
                run = lcn;
            }
            else if (!used && run >= 0)
            {
                allocated.Add(new FreeRun(run, lcn - run));
                run = -1;
            }
        }

        if (run >= 0)
        {
            allocated.Add(new FreeRun(run, end - run));
        }
    }
}

public sealed class SystemVolumes : IVolumeSystem
{
    public const string RetrimTool = "defrag.exe";

    private const string ScratchName = "niskala-trim.tmp";

    private SystemVolumes()
    {
    }

    public static SystemVolumes Instance { get; } = new();

    public IReadOnlyList<DriveVolume> FixedVolumes()
    {
        var volumes = new List<DriveVolume>();
        foreach (char letter in VolumeNative.FixedDrives())
        {
            if (VolumeNative.TryGetFacts(letter, out VolumeFacts facts) != 0)
            {
                continue;
            }

            MediaEvidence evidence = MediaEvidence.None;
            bool trim = false;
            if (VolumeNative.TryOpenVolume(letter, readData: false, out SafeFileHandle handle) == 0)
            {
                using (handle)
                {
                    bool? seekPenalty = VolumeNative.TryQuerySeekPenalty(handle, out bool seek) == 0 ? seek : null;
                    trim = VolumeNative.TryQueryTrim(handle, out bool enabled) == 0 && enabled;
                    evidence = VolumeNative.TryQueryDevice(handle, out StorageDeviceFacts device) == 0
                        ? new MediaEvidence(seekPenalty, (StorageBus)device.BusType, MediaClassifier.RotationNotReported, $"{device.Vendor} {device.Product}".Trim())
                        : evidence with { SeekPenalty = seekPenalty };
                }
            }

            if (MediaClassifier.WantsRotationRate(evidence) && VolumeNative.TryQueryRotationRate(letter, out int rotationRate) == 0)
            {
                evidence = evidence with { RotationRate = rotationRate };
            }

            volumes.Add(new DriveVolume(
                letter,
                facts.Label,
                facts.FileSystem,
                facts.BytesPerCluster,
                facts.TotalClusters,
                facts.TotalBytes,
                facts.FreeBytes,
                MediaClassifier.Classify(evidence),
                trim,
                evidence.Bus,
                evidence.RotationRate));
        }

        return volumes;
    }

    public int TryOpen(DriveVolume volume, out IVolumeSession? session)
    {
        ArgumentNullException.ThrowIfNull(volume);
        session = null;
        int error = VolumeNative.TryOpenVolume(volume.Letter, readData: true, out SafeFileHandle handle);
        if (error != 0)
        {
            handle.Dispose();
            return error;
        }

        VolumeNative.TrySetLowIoPriority(handle);
        session = new Session(volume, handle);
        return 0;
    }

    public void EnterBackground() => VolumeNative.EnterBackgroundMode();

    public void LeaveBackground() => VolumeNative.LeaveBackgroundMode();

    public int RetrimWithSystemTool(DriveVolume volume, Action<double> percent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(percent);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, RetrimTool))
        {
            WorkingDirectory = Environment.SystemDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.Latin1,
            StandardErrorEncoding = Encoding.Latin1,
        };
        foreach (string argument in (string[])[volume.Name, "/L", "/U", "/V"])
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException($"{RetrimTool} did not start.");
        using CancellationTokenRegistration stop = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        });
        process.ErrorDataReceived += (_, e) => Report(e.Data);
        process.BeginErrorReadLine();
        while (process.StandardOutput.ReadLine() is { } line)
        {
            Report(line);
        }

        process.WaitForExit();
        cancellationToken.ThrowIfCancellationRequested();
        return process.ExitCode;

        void Report(string? line)
        {
            if (RetrimOutput.Percent(line) is { } value)
            {
                percent(value);
            }
        }
    }

    private sealed class Session(DriveVolume volume, SafeFileHandle handle) : IVolumeSession
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<VolumeExtent> _raw = [];
        private readonly List<FoundEntry> _found = [];

        public ClusterBitmap ReadBitmap()
        {
            int error = VolumeNative.TryReadBitmap(handle, out ulong[] words, out long clusters);
            if (error != 0)
            {
                throw new IOException($"The cluster map of {volume.Name} could not be read (Win32 error {error}).", error);
            }

            return ClusterBitmap.FromWords(words, clusters);
        }

        public int List(string directory, List<VolumeEntry> entries)
        {
            _found.Clear();
            int error = VolumeNative.TryList(directory, _found);
            foreach (FoundEntry entry in _found)
            {
                entries.Add(new VolumeEntry(
                    entry.Name,
                    entry.Attributes.HasFlag(FileAttributes.Directory),
                    entry.Attributes.HasFlag(FileAttributes.ReparsePoint),
                    entry.Size,
                    entry.LastWriteUtc));
            }

            return error;
        }

        public int ReadExtents(string path, List<Extent> extents)
        {
            int error = VolumeNative.TryOpenEntry(path, out SafeFileHandle entry);
            using (entry)
            {
                return error != 0 ? error : Convert(entry, extents);
            }
        }

        public int BeginMove(string path, out IFileMover? mover)
        {
            mover = null;
            int error = VolumeNative.TryOpenEntry(path, out SafeFileHandle file);
            if (error != 0)
            {
                file.Dispose();
                return error;
            }

            VolumeNative.TrySetLowIoPriority(file);
            mover = new Mover(handle, file);
            return 0;
        }

        public int ReadAllocated(long start, long clusters, List<FreeRun> allocated)
        {
            ArgumentNullException.ThrowIfNull(allocated);
            int error = VolumeNative.TryReadBitmapRange(handle, start, clusters, out long first, out byte[] bits);
            if (error == 0)
            {
                AllocatedRuns.Collect(bits, first, start, clusters, allocated);
            }

            return error;
        }

        public long FileRecords() => VolumeNative.TryGetNtfsLayout(handle, out NtfsLayout layout) == 0 ? layout.FileRecords : 0;

        public IoSample? SampleIo() => VolumeNative.TryReadCounters(handle, out DiskCounters counters) == 0
            ? new IoSample(counters.BytesRead, counters.BytesWritten, counters.QueueDepth, _clock.Elapsed)
            : null;

        public int TrimFreeSpace(long bytes, long chunk, Action<long> trimmed, CancellationToken cancellationToken)
        {
            int error = VolumeNative.TryCreateScratch(Path.Combine(volume.Root, ScratchName), out SafeFileHandle scratch);
            using (scratch)
            {
                if (error != 0)
                {
                    return error;
                }

                VolumeNative.TrySetLowIoPriority(scratch);
                for (long done = 0; done < bytes;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long size = Math.Min(chunk, bytes - done);
                    error = VolumeNative.TrySetLength(scratch, done + size);
                    if (error == 0)
                    {
                        error = VolumeNative.TryTrim(scratch, done, size);
                    }

                    if (error != 0)
                    {
                        return error;
                    }

                    done += size;
                    trimmed(done);
                }

                return 0;
            }
        }

        public void Dispose() => handle.Dispose();

        private int Convert(SafeFileHandle entry, List<Extent> extents)
        {
            _raw.Clear();
            int error = VolumeNative.TryGetExtents(entry, _raw);
            foreach (VolumeExtent raw in _raw)
            {
                extents.Add(new Extent(raw.Vcn, raw.Lcn, raw.NextVcn - raw.Vcn));
            }

            return error;
        }
    }

    private sealed class Mover(SafeFileHandle volume, SafeFileHandle file) : IFileMover
    {
        public int Move(long vcn, long lcn, uint clusters) => VolumeNative.TryMove(volume, file, vcn, lcn, clusters);

        public void Dispose() => file.Dispose();
    }
}
