using Microsoft.Extensions.Logging;

namespace Niskala.Cli;

internal sealed class StderrLoggerProvider : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly TextWriter _writer = Console.Error;

    public ILogger CreateLogger(string categoryName) => new StderrLogger(this, categoryName[(categoryName.LastIndexOf('.') + 1)..]);

    public void Dispose() => _writer.Flush();

    private sealed class StderrLogger(StderrLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string level = logLevel switch
            {
                LogLevel.Trace => "trce",
                LogLevel.Debug => "dbug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "fail",
                _ => "crit",
            };

            lock (owner._gate)
            {
                owner._writer.WriteLine($"{level} {category}[{eventId.Id}] {formatter(state, exception)}");
                if (exception is not null)
                {
                    owner._writer.WriteLine("     " + exception.Message);
                }
            }
        }
    }
}
