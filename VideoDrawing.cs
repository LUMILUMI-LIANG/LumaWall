using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;

namespace LumaWall;

// Reusable native storage. Decoding writes directly into the DIB; the managed
// bitmap is a view of those same pixels, rather than a second frame allocation.
sealed class VideoDrawing : IDisposable
{
    IntPtr bitmap, sourceDc, previousBitmap, pixels;
    readonly Size sourceSize;
    readonly object drawGate = new();
    public Bitmap Bitmap { get; private set; } = null!;
    public Size Size => sourceSize;

    public VideoDrawing(Size size)
    {
        sourceSize = size;
        try
        {
            var info = new BitmapInfo
            {
                HeaderSize = 40, Width = size.Width, Height = -size.Height,
                Planes = 1, BitCount = 32, ImageSize = checked((uint)(size.Width * size.Height * 4L))
            };
            bitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out pixels, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || pixels == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            sourceDc = CreateCompatibleDC(IntPtr.Zero);
            if (sourceDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            previousBitmap = SelectObject(sourceDc, bitmap);
            if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
            Bitmap = new Bitmap(size.Width, size.Height, checked(size.Width * 4), PixelFormat.Format32bppRgb, pixels);
        }
        catch { Dispose(); throw; }
    }

    public VideoDrawing(Bitmap source) : this(source.Size)
    {
        var bits = source.LockBits(new(Point.Empty, source.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
        try { CopyFrom(bits.Scan0, bits.Stride); }
        finally { source.UnlockBits(bits); }
    }

    public unsafe void CopyFrom(IntPtr source, int stride)
    {
        MfNative.Check(MfNative.MFCopyImage(pixels, sourceSize.Width * 4, source, stride, sourceSize.Width * 4, sourceSize.Height));
        // MF RGB32's fourth byte is unspecified. Native blits also copy that
        // byte into ARGB snapshots, so make it opaque before publishing a frame.
        var opaquePixels = new Span<int>((void*)pixels, checked(sourceSize.Width * sourceSize.Height));
        var mask = new Vector<int>(unchecked((int)0xff000000));
        int offset = 0;
        for (; offset <= opaquePixels.Length - Vector<int>.Count; offset += Vector<int>.Count)
            (new Vector<int>(opaquePixels.Slice(offset, Vector<int>.Count)) | mask).CopyTo(opaquePixels.Slice(offset, Vector<int>.Count));
        for (; offset < opaquePixels.Length; offset++) opaquePixels[offset] |= unchecked((int)0xff000000);
    }

    public void CopyNv12From(IntPtr source, int stride, int storageHeight, Rectangle aperture, int matrix, bool fullRange)
        => Nv12Converter.Copy(pixels, sourceSize.Width * 4, source, stride, storageHeight, aperture, matrix, fullRange);

    public bool Draw(Graphics graphics, Size viewport, WallpaperLayoutMode mode = WallpaperLayoutMode.Fill,
        Size referenceSize = default, Size logicalSourceSize = default)
    {
        // A frame can be captured while its desktop control is painting. This
        // lock belongs to that frame only and never blocks publication of others.
        lock (drawGate) return DrawCore(graphics, viewport, mode, referenceSize, logicalSourceSize);
    }

    bool DrawCore(Graphics graphics, Size viewport, WallpaperLayoutMode mode, Size referenceSize, Size logicalSourceSize)
    {
        if (sourceDc == IntPtr.Zero || viewport.Width <= 0 || viewport.Height <= 0) return false;
        using var transform = graphics.Transform;
        var elements = transform.Elements;
        // Wallpaper editing uses positive axis-aligned scale/translation. Keep
        // the managed renderer as a fallback for other Graphics transforms.
        if (elements[0] <= 0 || elements[3] <= 0 || Math.Abs(elements[1]) > .00001f || Math.Abs(elements[2]) > .00001f || graphics.PageScale != 1) return false;
        var geometry = WallpaperLayout.Create(sourceSize, viewport, mode, elements[0], elements[3], elements[4], elements[5], referenceSize, logicalSourceSize);
        var physicalClip = Rectangle.Intersect(geometry.CanvasClip,
            WallpaperLayout.MapClip(graphics.ClipBounds, elements[0], elements[3], elements[4], elements[5]));
        if (physicalClip.Width <= 0 || physicalClip.Height <= 0) return true;

        IntPtr target = graphics.GetHdc();
        try { return DrawGeometry(target, geometry, physicalClip); }
        finally { graphics.ReleaseHdc(target); }
    }

    // Live windows already have their destination DC. Avoid asking GDI+ for a
    // temporary bitmap-backed DC and copying a full desktop back on ReleaseHdc.
    public bool DrawHdc(IntPtr target, Size viewport, WallpaperTransform transform, Rectangle paintClip,
        WallpaperLayoutMode mode = WallpaperLayoutMode.Fill, Size referenceSize = default, Size logicalSourceSize = default)
    {
        lock (drawGate)
        {
            if (sourceDc == IntPtr.Zero || viewport.Width <= 0 || viewport.Height <= 0) return false;
            var geometry = WallpaperLayout.Create(sourceSize, viewport, mode, transform, referenceSize, logicalSourceSize);
            var clip = Rectangle.Intersect(Rectangle.Intersect(new(Point.Empty, viewport), paintClip), geometry.CanvasClip);
            return DrawGeometry(target, geometry, clip);
        }
    }

    bool DrawGeometry(IntPtr target, WallpaperLayout.Geometry geometry, Rectangle clip)
    {
        if (!geometry.Tiled) return DrawPhysical(target, geometry.Destination, clip);
        if (clip.Width <= 0 || clip.Height <= 0 || geometry.TileWidth <= 0 || geometry.TileHeight <= 0) return true;
        // A small video can repeat tens of thousands of times on an ultrawide
        // screen. Fill that already prepared target once instead of issuing a
        // native call per cell; previews retain the same monitor-relative count.
        double cells = (Math.Ceiling(clip.Width / geometry.TileWidth) + 1) * (Math.Ceiling(clip.Height / geometry.TileHeight) + 1);
        if (cells > 4096) return DrawDenseTiles(target, geometry, clip);
        int saved = SaveDC(target); if (saved == 0) return false;
        try
        {
            IntersectClipRect(target, clip.Left, clip.Top, clip.Right, clip.Bottom); SetStretchBltMode(target, 3);
            int firstX = Math.Max(0, (int)Math.Floor((clip.Left - geometry.OriginX) / geometry.TileWidth));
            int firstY = Math.Max(0, (int)Math.Floor((clip.Top - geometry.OriginY) / geometry.TileHeight));
            int endX = (int)Math.Ceiling((clip.Right - geometry.OriginX) / geometry.TileWidth);
            int endY = (int)Math.Ceiling((clip.Bottom - geometry.OriginY) / geometry.TileHeight);
            for (int row = firstY; row < endY; row++) for (int column = firstX; column < endX; column++)
            {
                int left = (int)Math.Round(geometry.OriginX + column * geometry.TileWidth);
                int top = (int)Math.Round(geometry.OriginY + row * geometry.TileHeight);
                int right = (int)Math.Round(geometry.OriginX + (column + 1) * geometry.TileWidth);
                int bottom = (int)Math.Round(geometry.OriginY + (row + 1) * geometry.TileHeight);
                if (right <= left || bottom <= top) continue;
                if (!StretchBlt(target, left, top, right - left, bottom - top, sourceDc, 0, 0, sourceSize.Width, sourceSize.Height, 0x00cc0020)) return false;
            }
            return true;
        }
        finally { GdiFlush(); RestoreDC(target, saved); }
    }

    bool DrawDenseTiles(IntPtr target, WallpaperLayout.Geometry geometry, Rectangle clip)
    {
        int saved = SaveDC(target); if (saved == 0) return false;
        try
        {
            using var graphics = Graphics.FromHdc(target);
            graphics.SetClip(clip, CombineMode.Intersect);
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            using var texture = new TextureBrush(Bitmap, WrapMode.Tile);
            using var matrix = new Matrix((float)(geometry.TileWidth / sourceSize.Width), 0, 0,
                (float)(geometry.TileHeight / sourceSize.Height), (float)geometry.OriginX, (float)geometry.OriginY);
            texture.Transform = matrix;
            graphics.FillRectangle(texture, clip);
            graphics.Flush(FlushIntention.Sync);
            return true;
        }
        finally { GdiFlush(); RestoreDC(target, saved); }
    }

    bool DrawPhysical(IntPtr target, Rectangle destination, Rectangle physicalClip)
    {
        if (destination.Width <= 0 || destination.Height <= 0 || physicalClip.Width <= 0 || physicalClip.Height <= 0) return true;
        int saved = SaveDC(target);
        if (saved == 0) return false;
        try
        {
            IntersectClipRect(target, physicalClip.Left, physicalClip.Top, physicalClip.Right, physicalClip.Bottom);
            // HALFTONE downscaling a full-HD frame can cost more than the whole
            // frame budget, even for a small preview. Use native color scaling
            // for every live frame so preview painting cannot starve playback.
            SetStretchBltMode(target, 3);
            return StretchBlt(target, destination.X, destination.Y, destination.Width, destination.Height,
                sourceDc, 0, 0, sourceSize.Width, sourceSize.Height, 0x00CC0020);
        }
        finally
        {
            // Complete this thread's GDI batch before the last frame lease can
            // return its storage to the decoder for reuse.
            GdiFlush(); RestoreDC(target, saved);
        }
    }

    public void Dispose()
    {
        Bitmap?.Dispose(); Bitmap = null!;
        if (sourceDc != IntPtr.Zero)
        {
            if (previousBitmap != IntPtr.Zero && previousBitmap != new IntPtr(-1)) SelectObject(sourceDc, previousBitmap);
            DeleteDC(sourceDc); sourceDc = IntPtr.Zero; previousBitmap = IntPtr.Zero;
        }
        if (bitmap != IntPtr.Zero) { DeleteObject(bitmap); bitmap = IntPtr.Zero; pixels = IntPtr.Zero; }
        GC.SuppressFinalize(this);
    }
    ~VideoDrawing() { Dispose(); }

    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo
    {
        public uint HeaderSize;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int PixelsPerMeterX, PixelsPerMeterY;
        public uint ColorsUsed, ColorsImportant;
        public uint Color;
    }
    [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll", SetLastError = true)] static extern IntPtr SelectObject(IntPtr hdc, IntPtr handle);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);
    [DllImport("gdi32.dll")] static extern int SaveDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern bool RestoreDC(IntPtr hdc, int saved);
    [DllImport("gdi32.dll")] static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] static extern int SetStretchBltMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, out Point previous);
    [DllImport("gdi32.dll", SetLastError = true)] static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
}
