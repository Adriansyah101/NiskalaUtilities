using System.Runtime.InteropServices;

namespace Niskala.Interop.Volumes;

public sealed unsafe class AlignedBuffer : IDisposable
{
    public const int DefaultAlignment = 4096;

    private byte* _data;

    public AlignedBuffer(int bytes, int alignment = DefaultAlignment)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "The alignment must be a power of two.");
        }

        _data = (byte*)NativeMemory.AlignedAlloc((nuint)bytes, (nuint)alignment);
        Length = bytes;
    }

    ~AlignedBuffer()
    {
        Release();
    }

    public int Length { get; }

    public Span<byte> Span
    {
        get
        {
            ObjectDisposedException.ThrowIf(_data is null, this);
            return new Span<byte>(_data, Length);
        }
    }

    public void Dispose()
    {
        Release();
        GC.SuppressFinalize(this);
    }

    private void Release()
    {
        if (_data is not null)
        {
            NativeMemory.AlignedFree(_data);
            _data = null;
        }
    }
}
