using Microsoft.Extensions.Logging;
using Niskala.Cleaner;
using Niskala.Optimizer.Resources;

namespace Niskala.Host;

internal sealed class StartupReview(ResourceControl control) : IStartupReview
{
    private static readonly ResourceKind[] Kinds = [ResourceKind.RunEntry, ResourceKind.RunOnceEntry, ResourceKind.StartupFolder, ResourceKind.Service, ResourceKind.ScheduledTask];

    public IReadOnlyList<StartupHindrance> Review(CancellationToken cancellationToken = default) =>
        [.. control.List(Kinds)
            .Where(i => i.Enabled && i.CanToggle)
            .Select(i => new StartupHindrance(i.Id, i.Name, i.Kind.ToString(), i.Impact, i.Publisher))];
}

internal static partial class HostLog
{
    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "Rules file {Source}: {Problem}")]
    public static partial void RuleProblem(ILogger logger, string source, string problem);
}
