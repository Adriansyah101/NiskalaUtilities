using Microsoft.Extensions.Logging;
using Niskala.Core.Diagnostics;
using Niskala.Host;

namespace Niskala.Cli;

internal static class CliHost
{
    public static NiskalaHost Create(bool verbose) => new(LoggerFactory.Create(builder =>
    {
        builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Information);
        builder.AddProvider(new JsonLinesFileLoggerProvider(JsonLinesFileLoggerProvider.DefaultDirectory, "niskala-cli"));
        if (verbose)
        {
            builder.AddProvider(new StderrLoggerProvider());
        }
    }));
}
