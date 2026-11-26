using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Threading;

namespace Niskala.Interop.Processes;

internal sealed unsafe class RemoteMemory : IDisposable
{
    private const PROCESS_ACCESS_RIGHTS Access =
        PROCESS_ACCESS_RIGHTS.PROCESS_VM_OPERATION | PROCESS_ACCESS_RIGHTS.PROCESS_VM_READ | PROCESS_ACCESS_RIGHTS.PROCESS_VM_WRITE;

    private readonly HANDLE _process;
    private readonly int _size;
    private void* _block;

    private RemoteMemory(HANDLE process, void* block, int size)
    {
        _process = process;
        _block = block;
        _size = size;
    }

    public nint Address => (nint)_block;

    public static RemoteMemory? Open(int processId, int size)
    {
        HANDLE process = PInvoke.OpenProcess(Access, false, (uint)processId);
        if (process.IsNull)
        {
            return null;
        }

        void* block = PInvoke.VirtualAllocEx(process, null, (nuint)size, VIRTUAL_ALLOCATION_TYPE.MEM_COMMIT | VIRTUAL_ALLOCATION_TYPE.MEM_RESERVE, PAGE_PROTECTION_FLAGS.PAGE_READWRITE);
        if (block is null)
        {
            PInvoke.CloseHandle(process);
            return null;
        }

        return new RemoteMemory(process, block, size);
    }

    public bool Write<T>(int offset, T value)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + sizeof(T), _size);
        return PInvoke.WriteProcessMemory(_process, (byte*)_block + offset, &value, (nuint)sizeof(T), null);
    }

    public bool Read<T>(int offset, out T value)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + sizeof(T), _size);
        return ReadAt((nint)((byte*)_block + offset), out value);
    }

    public bool ReadAt<T>(nint address, out T value)
        where T : unmanaged
    {
        T copy = default;
        bool read = address != 0 && PInvoke.ReadProcessMemory(_process, (void*)address, &copy, (nuint)sizeof(T), null);
        value = copy;
        return read;
    }

    public string? ReadString(int offset, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(offset + (capacity * sizeof(char)), _size);
        char* text = stackalloc char[capacity];
        if (!PInvoke.ReadProcessMemory(_process, (byte*)_block + offset, text, (nuint)(capacity * sizeof(char)), null))
        {
            return null;
        }

        var span = new ReadOnlySpan<char>(text, capacity);
        int end = span.IndexOf('\0');
        return new string(end < 0 ? span : span[..end]);
    }

    public void Dispose()
    {
        if (_block is not null)
        {
            PInvoke.VirtualFreeEx(_process, _block, 0, VIRTUAL_FREE_TYPE.MEM_RELEASE);
            _block = null;
            PInvoke.CloseHandle(_process);
        }
    }
}
