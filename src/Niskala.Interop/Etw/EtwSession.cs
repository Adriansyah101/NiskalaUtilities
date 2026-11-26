using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Niskala.Interop.Etw;

public enum EtwEventKind
{
    ProcessStart = 1,
    ProcessStop = 2,
    FileCreated = 10,
    FileWritten = 11,
    FileDeleted = 12,
    FileRenamed = 13,
    KeyCreated = 20,
    KeyDeleted = 21,
    ValueSet = 22,
    ValueDeleted = 23,
}

[Flags]
public enum EtwEventFlags
{
    None = 0,
    Directory = 1,
    ParentTracked = 2,
    NewKey = 4,
    Previous = 8,
}

public sealed record EtwEvent(EtwEventKind Kind, int ProcessId, DateTime TimeUtc, string Path)
{
    public int ParentProcessId { get; init; }

    public EtwEventFlags Flags { get; init; }

    public string? Name { get; init; }

    public int ValueType { get; init; }

    public int PreviousType { get; init; }

    public int PreviousSize { get; init; }

    public byte[]? PreviousData { get; init; }
}

public sealed record EtwStats(long EventsSeen, long EventsForwarded, int EventsLost, int BuffersLost, int PendingDropped, int UnnamedEvents);

public sealed unsafe partial class EtwSession : IDisposable
{
    public const int ErrorAccessDenied = 5;
    public const int ErrorModuleNotFound = 126;

    private const string Library = "Niskala.EtwHelper.dll";

    private readonly Func<EtwEvent, bool> _handler;
    private GCHandle _self;
    private nint _native;
    private Exception? _failure;

    static EtwSession() => NativeLibrary.SetDllImportResolver(typeof(EtwSession).Assembly, Resolve);

    private EtwSession(Func<EtwEvent, bool> handler) => _handler = handler;

    public static int TryStart(Func<EtwEvent, bool> handler, out EtwSession? session)
    {
        ArgumentNullException.ThrowIfNull(handler);
        session = null;
        var created = new EtwSession(handler);
        created._self = GCHandle.Alloc(created);
        int error;
        try
        {
            error = (int)NiskalaEtwStart(&OnEvent, GCHandle.ToIntPtr(created._self), out created._native);
        }
        catch (DllNotFoundException)
        {
            error = ErrorModuleNotFound;
        }

        if (error != 0)
        {
            created._self.Free();
            return error;
        }

        session = created;
        return 0;
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == Library && NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "lib", Library), out nint handle) ? handle : 0;

    public void Track(int processId)
    {
        ObjectDisposedException.ThrowIf(_native == 0, this);
        _ = NiskalaEtwTrack(_native, (uint)processId);
    }

    public EtwStats Stop()
    {
        ObjectDisposedException.ThrowIf(_native == 0, this);
        _ = NiskalaEtwStop(_native, out NativeStats stats);
        _native = 0;
        _self.Free();
        if (_failure is { } failure)
        {
            throw new InvalidOperationException("The trace event handler failed.", failure);
        }

        return new EtwStats((long)stats.EventsSeen, (long)stats.EventsForwarded, (int)stats.EventsLost, (int)stats.BuffersLost, (int)stats.PendingDropped, (int)stats.UnnamedEvents);
    }

    public void Dispose()
    {
        if (_native != 0)
        {
            _ = NiskalaEtwStop(_native, out _);
            _native = 0;
            _self.Free();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnEvent(nint context, NativeEvent* native)
    {
        var session = (EtwSession)GCHandle.FromIntPtr(context).Target!;
        if (session._failure is not null)
        {
            return 0;
        }

        try
        {
            return session._handler(Convert(native)) ? 1 : 0;
        }
        catch (Exception ex)
        {
            session._failure = ex;
            return 0;
        }
    }

    private static EtwEvent Convert(NativeEvent* e) => new(
        (EtwEventKind)e->Kind,
        (int)e->ProcessId,
        DateTime.FromFileTimeUtc(e->Timestamp),
        e->Path is null ? string.Empty : new string(e->Path))
    {
        ParentProcessId = (int)e->ParentProcessId,
        Flags = (EtwEventFlags)e->Flags,
        Name = e->Name is null ? null : new string(e->Name),
        ValueType = (int)e->ValueType,
        PreviousType = (int)e->PreviousType,
        PreviousSize = (int)e->PreviousSize,
        PreviousData = ((EtwEventFlags)e->Flags).HasFlag(EtwEventFlags.Previous)
            ? new ReadOnlySpan<byte>(e->PreviousData, (int)e->PreviousCaptured).ToArray()
            : null,
    };

    [LibraryImport(Library)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint NiskalaEtwStart(delegate* unmanaged[Stdcall]<nint, NativeEvent*, int> callback, nint context, out nint session);

    [LibraryImport(Library)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint NiskalaEtwTrack(nint session, uint processId);

    [LibraryImport(Library)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvStdcall)])]
    private static partial uint NiskalaEtwStop(nint session, out NativeStats stats);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEvent
    {
        public uint Kind;
        public uint ProcessId;
        public uint ParentProcessId;
        public uint Flags;
        public long Timestamp;
        public char* Path;
        public char* Name;
        public uint ValueType;
        public uint PreviousType;
        public uint PreviousSize;
        public uint PreviousCaptured;
        public byte* PreviousData;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStats
    {
        public ulong EventsSeen;
        public ulong EventsForwarded;
        public uint EventsLost;
        public uint BuffersLost;
        public uint PendingDropped;
        public uint UnnamedEvents;
    }
}
