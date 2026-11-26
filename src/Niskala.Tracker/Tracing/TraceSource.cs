using System.Globalization;
using Niskala.Interop.Etw;

namespace Niskala.Tracker.Tracing;

public interface ITraceSession : IDisposable
{
    void Track(int processId);

    EtwStats Stop();
}

public interface ITraceSource
{
    ITraceSession Start(Func<EtwEvent, bool> onEvent);
}

public sealed class TrackerUnavailableException(string message, int error) : Exception(message)
{
    public int Error { get; } = error;
}

public sealed class EtwTraceSource : ITraceSource
{
    public static EtwTraceSource Instance { get; } = new();

    public ITraceSession Start(Func<EtwEvent, bool> onEvent)
    {
        int error = EtwSession.TryStart(onEvent, out EtwSession? session);
        return error switch
        {
            0 => new Session(session!),
            EtwSession.ErrorAccessDenied => throw new TrackerUnavailableException("Recording an install needs elevation: run Niskala from an elevated prompt.", error),
            EtwSession.ErrorModuleNotFound => throw new TrackerUnavailableException("Niskala.EtwHelper.dll is missing from the program's folder and its lib folder.", error),
            _ => throw new TrackerUnavailableException(string.Create(CultureInfo.InvariantCulture, $"The kernel trace could not start (Win32 error {error})."), error),
        };
    }

    private sealed class Session(EtwSession session) : ITraceSession
    {
        public void Track(int processId) => session.Track(processId);

        public EtwStats Stop() => session.Stop();

        public void Dispose() => session.Dispose();
    }
}
