using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Niskala.Core.Diagnostics;

public sealed class JsonLinesFileLoggerProvider(string directory, string prefix, LogLevel minimumLevel = LogLevel.Information) : ILoggerProvider
{
    private readonly Lock _gate = new();
    private readonly LogLevel _minimumLevel = minimumLevel;
    private FileStream? _stream;
    private DateOnly _streamDate;
    private bool _disabled;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Niskala", "logs");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _stream?.Dispose();
            _stream = null;
        }
    }

    private void Write(LogLevel level, string category, EventId eventId, string message, Exception? exception)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("ts", now);
            json.WriteString("level", level.ToString());
            json.WriteString("cat", category);
            if (eventId.Id != 0)
            {
                json.WriteNumber("id", eventId.Id);
            }

            if (!string.IsNullOrEmpty(eventId.Name))
            {
                json.WriteString("event", eventId.Name);
            }

            json.WriteString("msg", message);
            if (exception is not null)
            {
                json.WriteString("ex", exception.ToString());
            }

            json.WriteEndObject();
        }

        buffer.WriteByte((byte)'\n');

        lock (_gate)
        {
            if (_disabled)
            {
                return;
            }

            try
            {
                FileStream stream = GetStream(DateOnly.FromDateTime(now.LocalDateTime));
                buffer.Position = 0;
                buffer.CopyTo(stream);
                stream.Flush();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _disabled = true;
                Console.Error.WriteLine(Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd());
            }
        }
    }

    private FileStream GetStream(DateOnly date)
    {
        if (_stream is not null && _streamDate == date)
        {
            return _stream;
        }

        _stream?.Dispose();
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, $"{prefix}-{date:yyyyMMdd}.jsonl");
        _stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _streamDate = date;
        return _stream;
    }

    private sealed class FileLogger(JsonLinesFileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                owner.Write(logLevel, category, eventId, formatter(state, exception), exception);
            }
        }
    }
}
