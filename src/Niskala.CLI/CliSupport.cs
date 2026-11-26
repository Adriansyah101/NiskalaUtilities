using System.Globalization;
using Microsoft.Extensions.Logging;
using Niskala.Core.Diagnostics;
using Niskala.Host;

namespace Niskala.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int Cancelled = 130;
    public const int ElevationRequired = 740;
    public const int RestartRequired = 3010;
}

internal static class TextFormat
{
    public static string Bytes(long? bytes) => bytes switch
    {
        null => "-",
        >= 1L << 30 => (bytes.Value / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1L << 20 => (bytes.Value / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes.Value / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
    };

    public static string Pad(string? text, int width)
    {
        text ??= string.Empty;
        return text.Length <= width ? text.PadRight(width) : string.Concat(text.AsSpan(0, width - 1), "…");
    }
}

internal static class Confirmation
{
    public static async Task<int?> RequireAsync(string question, bool assumeYes, CancellationToken cancellationToken)
    {
        if (assumeYes)
        {
            return null;
        }

        if (Console.IsInputRedirected)
        {
            await Console.Error.WriteLineAsync("Input is redirected: pass --yes to confirm.").ConfigureAwait(false);
            return ExitCodes.Usage;
        }

        await Console.Error.WriteAsync($"{question} [y/N] ").ConfigureAwait(false);
        string? answer = (await Console.In.ReadLineAsync(cancellationToken).ConfigureAwait(false))?.Trim();
        if (string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        await Console.Error.WriteLineAsync("Nothing was changed.").ConfigureAwait(false);
        return ExitCodes.Cancelled;
    }
}

internal static class CommandRunner
{
    public static async Task<int> RunAsync(string name, bool verbose, Func<NiskalaHost, CancellationToken, Task<int>> body, CancellationToken cancellationToken)
    {
        using NiskalaHost host = CliHost.Create(verbose);
        try
        {
            return await body(host, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("Cancelled.").ConfigureAwait(false);
            return ExitCodes.Cancelled;
        }
        catch (Exception ex)
        {
            CliLog.CommandFailed(host.LoggerFactory.CreateLogger("Niskala.Cli"), name, ex);
            await Console.Error.WriteLineAsync($"niskala {name} failed: {ex.Message}").ConfigureAwait(false);
            await Console.Error.WriteLineAsync($"Details: {JsonLinesFileLoggerProvider.DefaultDirectory}").ConfigureAwait(false);
            return ExitCodes.Failure;
        }
    }
}

internal static partial class CliLog
{
    [LoggerMessage(EventId = 9001, Level = LogLevel.Error, Message = "Command {Command} failed")]
    public static partial void CommandFailed(ILogger logger, string command, Exception exception);
}
