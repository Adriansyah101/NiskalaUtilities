using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Niskala.Interop.Icons;

public sealed record IconPixels(int Width, int Height, byte[] Bgra);

public static class IconNative
{
    private const uint SmallIconSize = 16;

    public static unsafe IconPixels? TryExtractIcon(string path, int index, int size)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        HICON icon;
        HRESULT result;
        fixed (char* p = path)
        {
            result = PInvoke.SHDefExtractIcon(p, index, 0, &icon, null, (uint)size | (SmallIconSize << 16));
        }

        if (result != HRESULT.S_OK || icon.IsNull)
        {
            return null;
        }

        try
        {
            return ReadIcon(icon);
        }
        finally
        {
            PInvoke.DestroyIcon(icon);
        }
    }

    public static unsafe IconPixels? TryGetShellIcon(string path, int size)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        IShellItemImageFactory* factory = null;
        Guid iid = IShellItemImageFactory.IID_Guid;
        HRESULT result;
        fixed (char* p = path)
        {
            result = PInvoke.SHCreateItemFromParsingName(p, null, &iid, (void**)&factory);
        }

        if (result.Failed || factory is null)
        {
            return null;
        }

        try
        {
            HBITMAP bitmap;
            try
            {
                factory->GetImage(new SIZE { cx = size, cy = size }, SIIGBF.SIIGBF_ICONONLY | SIIGBF.SIIGBF_BIGGERSIZEOK, &bitmap);
            }
            catch (COMException)
            {
                return null;
            }

            try
            {
                IconPixels? pixels = ReadBitmap(bitmap);
                if (pixels is not null && IsPremultiplied(pixels.Bgra))
                {
                    Unpremultiply(pixels.Bgra);
                }

                return pixels;
            }
            finally
            {
                PInvoke.DeleteObject(bitmap);
            }
        }
        finally
        {
            factory->Release();
        }
    }

    private static unsafe IconPixels? ReadIcon(HICON icon)
    {
        ICONINFO info;
        if (!PInvoke.GetIconInfo(icon, &info))
        {
            return null;
        }

        try
        {
            if (info.hbmColor.IsNull)
            {
                return null;
            }

            IconPixels? color = ReadBitmap(info.hbmColor);
            if (color is null || HasAlpha(color.Bgra))
            {
                return color;
            }

            IconPixels? mask = info.hbmMask.IsNull ? null : ReadBitmap(info.hbmMask);
            ApplyMask(color, mask);
            return color;
        }
        finally
        {
            if (!info.hbmColor.IsNull)
            {
                PInvoke.DeleteObject(info.hbmColor);
            }

            if (!info.hbmMask.IsNull)
            {
                PInvoke.DeleteObject(info.hbmMask);
            }
        }
    }

    private static unsafe IconPixels? ReadBitmap(HBITMAP bitmap)
    {
        BITMAP header;
        if (PInvoke.GetObject(bitmap, sizeof(BITMAP), &header) == 0 || header.bmWidth <= 0 || header.bmHeight == 0)
        {
            return null;
        }

        int width = header.bmWidth;
        int height = Math.Abs(header.bmHeight);
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };

        byte[] pixels = new byte[width * height * 4];
        HDC dc = PInvoke.CreateCompatibleDC(HDC.Null);
        try
        {
            int lines;
            fixed (byte* p = pixels)
            {
                lines = PInvoke.GetDIBits(dc, bitmap, 0, (uint)height, p, &info, DIB_USAGE.DIB_RGB_COLORS);
            }

            return lines == height ? new IconPixels(width, height, pixels) : null;
        }
        finally
        {
            PInvoke.DeleteDC(dc);
        }
    }

    private static bool HasAlpha(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
        {
            if (bgra[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void ApplyMask(IconPixels color, IconPixels? mask)
    {
        bool usable = mask is not null && mask.Width == color.Width && mask.Height >= color.Height;
        for (int i = 3; i < color.Bgra.Length; i += 4)
        {
            color.Bgra[i] = usable && mask!.Bgra[i - 3] != 0 ? (byte)0 : (byte)255;
        }
    }

    private static bool IsPremultiplied(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte alpha = bgra[i + 3];
            if (bgra[i] > alpha || bgra[i + 1] > alpha || bgra[i + 2] > alpha)
            {
                return false;
            }
        }

        return true;
    }

    private static void Unpremultiply(byte[] bgra)
    {
        for (int i = 0; i < bgra.Length; i += 4)
        {
            int alpha = bgra[i + 3];
            if (alpha is 0 or 255)
            {
                continue;
            }

            bgra[i] = (byte)(bgra[i] * 255 / alpha);
            bgra[i + 1] = (byte)(bgra[i + 1] * 255 / alpha);
            bgra[i + 2] = (byte)(bgra[i + 2] * 255 / alpha);
        }
    }
}
