using System.Globalization;
using Microsoft.Extensions.Logging;
using Niskala.Guardian.Journal;
using Niskala.Interop.Wmi;

namespace Niskala.Guardian.RestorePoints;

public interface IRestorePointService
{
    Task<RestorePointRecord> CreateAsync(string description, CancellationToken cancellationToken = default);
}

public sealed class WmiRestorePointService(ILogger<WmiRestorePointService> logger, TimeProvider? time = null, Func<bool>? isElevated = null) : IRestorePointService
{
    private const string Namespace = @"root\default";
    private const int ApplicationUninstall = 1;
    private const int BeginSystemChange = 100;
    private const int MaxDescriptionLength = 64;
    private const int ErrorServiceDisabled = 1058;

    private static readonly TimeSpan ListingWait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ListingPoll = TimeSpan.FromMilliseconds(250);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Func<bool> _isElevated = isElevated ?? (static () => Environment.IsPrivilegedProcess);

    public async Task<RestorePointRecord> CreateAsync(string description, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(description);
        string text = description.Length <= MaxDescriptionLength ? description : description[..MaxDescriptionLength];
        if (!_isElevated())
        {
            return new RestorePointRecord(false, null, text, "Not elevated: no restore point was made.");
        }

        (int? before, int returned) = await Task.Run(() => Request(text), cancellationToken).ConfigureAwait(false);
        if (returned != 0)
        {
            GuardianLog.RestorePointFailed(logger, returned);
            return new RestorePointRecord(false, before, text, returned == ErrorServiceDisabled
                ? "System Restore is turned off; no restore point was made."
                : string.Create(CultureInfo.InvariantCulture, $"No restore point was made (error 0x{returned:X8})."));
        }

        int? after = await ListedAfterAsync(before, cancellationToken).ConfigureAwait(false);
        return Record(text, before, after);
    }

    private static (int? Before, int Returned) Request(string description)
    {
        int? before = Latest();
        int error = WmiNative.TryInvoke(
            Namespace,
            "SystemRestore",
            "CreateRestorePoint",
            [new("Description", description), new("RestorePointType", ApplicationUninstall), new("EventType", BeginSystemChange)],
            ["ReturnValue"],
            out Dictionary<string, object?> outputs);
        return (before, error != 0 ? error : outputs["ReturnValue"] as int? ?? -1);
    }

    private async Task<int?> ListedAfterAsync(int? before, CancellationToken cancellationToken)
    {
        long start = _time.GetTimestamp();
        while (true)
        {
            int? latest = Latest();
            if ((latest is { } sequence && sequence != before) || _time.GetElapsedTime(start) >= ListingWait)
            {
                return latest;
            }

            await Task.Delay(ListingPoll, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    private RestorePointRecord Record(string description, int? before, int? after)
    {
        if (after is { } sequence && sequence != before)
        {
            GuardianLog.RestorePointCreated(logger, sequence);
            return new RestorePointRecord(true, sequence, description, null);
        }

        return new RestorePointRecord(false, before, description, before is null
            ? "Windows made no restore point; System Protection may be off for the system drive."
            : string.Create(CultureInfo.InvariantCulture, $"Windows made no new restore point; it skips one when another was made recently (within 24 hours by default). The latest is #{before}."));
    }

    private static int? Latest() =>
        WmiNative.TryQuery(Namespace, "SELECT SequenceNumber FROM SystemRestore", ["SequenceNumber"], out List<Dictionary<string, object?>> rows) == 0
            ? rows.Select(r => r["SequenceNumber"] as int?).Max()
            : null;
}
