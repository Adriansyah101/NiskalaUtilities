using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace Niskala.Interop.Shell;

public enum RecycleStatus
{
    Recycled,
    NotRecyclable,
    Failed,
}

public sealed record RecycleResult(RecycleStatus Status, int HResult, string? RecycledPath);

public static class RecycleBinNative
{
    private const FILEOPERATION_FLAGS Flags =
        FILEOPERATION_FLAGS.FOF_ALLOWUNDO | FILEOPERATION_FLAGS.FOFX_RECYCLEONDELETE | FILEOPERATION_FLAGS.FOF_NOCONFIRMATION
        | FILEOPERATION_FLAGS.FOF_NOERRORUI | FILEOPERATION_FLAGS.FOF_SILENT | FILEOPERATION_FLAGS.FOFX_EARLYFAILURE;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    public static RecycleResult Recycle(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        RecycleResult? result = null;

        var thread = new Thread(() => result = RecycleOnSta(path)) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result!;
    }

    private static unsafe RecycleResult RecycleOnSta(string path)
    {
        IFileOperation* operation = null;
        IShellItem* item = null;
        nint sinkPointer = 0;
        var sink = new RecycleSink();
        uint cookie = 0;
        try
        {
            HRESULT created = FileOperation.CreateInstance(out operation);
            if (created.Failed)
            {
                return new RecycleResult(RecycleStatus.Failed, created.Value, null);
            }

            HRESULT parsed = PInvoke.SHCreateItemFromParsingName(path, null, out item);
            if (parsed.Failed)
            {
                return new RecycleResult(RecycleStatus.Failed, parsed.Value, null);
            }

            nint unknown = Wrappers.GetOrCreateComInterfaceForObject(sink, CreateComInterfaceFlags.None);
            Guid iid = typeof(IRecycleProgressSink).GUID;
            int queried = Marshal.QueryInterface(unknown, in iid, out sinkPointer);
            Marshal.Release(unknown);
            if (queried < 0)
            {
                return new RecycleResult(RecycleStatus.Failed, queried, null);
            }

            operation->SetOperationFlags(Flags);
            operation->Advise((IFileOperationProgressSink*)sinkPointer, &cookie);
            operation->DeleteItem(item, null);
            operation->PerformOperations();
        }
        catch (COMException ex)
        {
            if (!sink.Refused && sink.DeleteResult >= 0)
            {
                return new RecycleResult(RecycleStatus.Failed, ex.HResult, null);
            }
        }
        finally
        {
            if (operation is not null)
            {
                if (cookie != 0)
                {
                    operation->Unadvise(cookie);
                }

                operation->Release();
            }

            if (item is not null)
            {
                item->Release();
            }

            if (sinkPointer != 0)
            {
                Marshal.Release(sinkPointer);
            }
        }

        return sink switch
        {
            { Refused: true } => new RecycleResult(RecycleStatus.NotRecyclable, sink.DeleteResult, null),
            { DeleteResult: < 0 } => new RecycleResult(RecycleStatus.Failed, sink.DeleteResult, null),
            _ => new RecycleResult(RecycleStatus.Recycled, 0, sink.RecycledPath),
        };
    }
}

[GeneratedComInterface]
[Guid("04B0F1A7-9490-44BC-96E1-4296A31252E2")]
internal partial interface IRecycleProgressSink
{
    [PreserveSig]
    int StartOperations();

    [PreserveSig]
    int FinishOperations(int result);

    [PreserveSig]
    int PreRenameItem(uint flags, nint item, nint newName);

    [PreserveSig]
    int PostRenameItem(uint flags, nint item, nint newName, int result, nint created);

    [PreserveSig]
    int PreMoveItem(uint flags, nint item, nint destination, nint newName);

    [PreserveSig]
    int PostMoveItem(uint flags, nint item, nint destination, nint newName, int result, nint created);

    [PreserveSig]
    int PreCopyItem(uint flags, nint item, nint destination, nint newName);

    [PreserveSig]
    int PostCopyItem(uint flags, nint item, nint destination, nint newName, int result, nint created);

    [PreserveSig]
    int PreDeleteItem(uint flags, nint item);

    [PreserveSig]
    int PostDeleteItem(uint flags, nint item, int result, nint created);

    [PreserveSig]
    int PreNewItem(uint flags, nint destination, nint newName);

    [PreserveSig]
    int PostNewItem(uint flags, nint destination, nint newName, nint templateName, uint attributes, int result, nint created);

    [PreserveSig]
    int UpdateProgress(uint total, uint done);

    [PreserveSig]
    int ResetTimer();

    [PreserveSig]
    int PauseTimer();

    [PreserveSig]
    int ResumeTimer();
}

[GeneratedComClass]
internal sealed partial class RecycleSink : IRecycleProgressSink
{
    private const int Ok = 0;
    private const int Abort = unchecked((int)0x80004004);

    public bool Refused { get; private set; }

    public int DeleteResult { get; private set; }

    public string? RecycledPath { get; private set; }

    public int StartOperations() => Ok;

    public int FinishOperations(int result) => Ok;

    public int PreRenameItem(uint flags, nint item, nint newName) => Ok;

    public int PostRenameItem(uint flags, nint item, nint newName, int result, nint created) => Ok;

    public int PreMoveItem(uint flags, nint item, nint destination, nint newName) => Ok;

    public int PostMoveItem(uint flags, nint item, nint destination, nint newName, int result, nint created) => Ok;

    public int PreCopyItem(uint flags, nint item, nint destination, nint newName) => Ok;

    public int PostCopyItem(uint flags, nint item, nint destination, nint newName, int result, nint created) => Ok;

    public int PreDeleteItem(uint flags, nint item)
    {
        if ((flags & (uint)_TRANSFER_SOURCE_FLAGS.TSF_DELETE_RECYCLE_IF_POSSIBLE) != 0)
        {
            return Ok;
        }

        Refused = true;
        return Abort;
    }

    public unsafe int PostDeleteItem(uint flags, nint item, int result, nint created)
    {
        DeleteResult = result;
        if (created != 0)
        {
            try
            {
                ((IShellItem*)created)->GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out PWSTR name);
                RecycledPath = name.ToString();
                Marshal.FreeCoTaskMem((nint)name.Value);
            }
            catch (COMException)
            {
                RecycledPath = null;
            }
        }

        return Ok;
    }

    public int PreNewItem(uint flags, nint destination, nint newName) => Ok;

    public int PostNewItem(uint flags, nint destination, nint newName, nint templateName, uint attributes, int result, nint created) => Ok;

    public int UpdateProgress(uint total, uint done) => Ok;

    public int ResetTimer() => Ok;

    public int PauseTimer() => Ok;

    public int ResumeTimer() => Ok;
}
