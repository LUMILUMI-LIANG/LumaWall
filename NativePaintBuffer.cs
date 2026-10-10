using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LumaWall;

// A viewport-sized surface owned by the UI thread. Reuse native storage rather
// than creating a GDI+ Bitmap/HDC pair for every frame.
sealed class NativePaintBuffer : IDisposable
{
    IntPtr dc, bitmap, previousBitmap, pixels;
    public Size Size { get; }
    public IntPtr Dc => dc;

    public NativePaintBuffer(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        Size = size;
        try
        {
            var info = new BitmapInfo
            {
                HeaderSize = 40, Width = size.Width, Height = -size.Height,
                Planes = 1, BitCount = 32, ImageSize = checked((uint)(size.Width * size.Height * 4L))
            };
            bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previousBitmap = SelectObject(dc, bitmap);
            if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        catch { Dispose(); throw; }
    }

    public unsafe void Clear()
    {
        // Finish any pending native access before writing the same DIB pixels.
        GdiFlush();
        new Span<uint>((void*)pixels, checked(Size.Width * Size.Height)).Fill(0xff000000);
    }

    public bool Present(IntPtr target, Rectangle clip)
    {
        clip = Rectangle.Intersect(new(Point.Empty, Size), clip);
        if (clip.Width <= 0 || clip.Height <= 0) return false;
        // The visible surface receives exactly one completed image. Never
        // clear it or expose the intermediate state of preparing the next one.
        bool copied = BitBlt(target, clip.X, clip.Y, clip.Width, clip.Height,
            dc, clip.X, clip.Y, 0x00CC0020);
        return GdiFlush() && copied;
    }

    public void Dispose()
    {
        GdiFlush();
        if (dc != IntPtr.Zero)
        {
            if (previousBitmap != IntPtr.Zero && previousBitmap != new IntPtr(-1)) SelectObject(dc, previousBitmap);
            DeleteDC(dc); dc = IntPtr.Zero; previousBitmap = IntPtr.Zero;
        }
        if (bitmap != IntPtr.Zero) { DeleteObject(bitmap); bitmap = IntPtr.Zero; pixels = IntPtr.Zero; }
        GC.SuppressFinalize(this);
    }
    ~NativePaintBuffer() { Dispose(); }

    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo
    {
        public uint HeaderSize;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int PixelsPerMeterX, PixelsPerMeterY;
        public uint ColorsUsed, ColorsImportant, Color;
    }
    [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info,
        uint usage, out IntPtr pixels, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool BitBlt(IntPtr target, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint operation);
}
