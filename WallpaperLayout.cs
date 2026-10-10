using System.Drawing.Drawing2D;

namespace LumaWall;

public enum WallpaperLayoutMode { Fill, Fit, Stretch, Center, Tile }

// All renderers use these same logical rectangles and device-pixel rounding.
// Layout precedes the existing canvas zoom/pan; overflow stays canvas-clipped.
static class WallpaperLayout
{
    internal readonly record struct Bounds(double X, double Y, double Width, double Height);
    internal readonly record struct Geometry(Rectangle Destination, Rectangle CanvasClip,
        double OriginX, double OriginY, double TileWidth, double TileHeight, bool Tiled);

    public static WallpaperLayoutMode Normalize(WallpaperLayoutMode mode) =>
        Enum.IsDefined(mode) ? mode : WallpaperLayoutMode.Fill;

    public static Bounds Destination(Size source, Size viewport, WallpaperLayoutMode mode,
        Size referenceSize = default, Size logicalSourceSize = default)
    {
        if (logicalSourceSize.Width > 0 && logicalSourceSize.Height > 0) source = logicalSourceSize;
        if (source.Width <= 0 || source.Height <= 0 || viewport.Width <= 0 || viewport.Height <= 0) return default;
        mode = Normalize(mode);
        if (mode == WallpaperLayoutMode.Stretch) return new(0, 0, viewport.Width, viewport.Height);
        double naturalWidth = source.Width, naturalHeight = source.Height;
        // Center and Tile use original media pixels on the target monitor.
        // A preview scales those units into its viewport; decoder downsampling
        // changes storage resolution but never the wallpaper's natural size.
        if (mode is WallpaperLayoutMode.Center or WallpaperLayoutMode.Tile
            && referenceSize.Width > 0 && referenceSize.Height > 0)
        {
            naturalWidth *= viewport.Width / (double)referenceSize.Width;
            naturalHeight *= viewport.Height / (double)referenceSize.Height;
        }
        if (mode == WallpaperLayoutMode.Tile) return new(0, 0, naturalWidth, naturalHeight);
        if (mode == WallpaperLayoutMode.Center)
            return new((viewport.Width - naturalWidth) / 2, (viewport.Height - naturalHeight) / 2, naturalWidth, naturalHeight);
        double scale = mode == WallpaperLayoutMode.Fit
            ? Math.Min(viewport.Width / (double)source.Width, viewport.Height / (double)source.Height)
            : Math.Max(viewport.Width / (double)source.Width, viewport.Height / (double)source.Height);
        double width = source.Width * scale, height = source.Height * scale;
        return new((viewport.Width - width) / 2, (viewport.Height - height) / 2, width, height);
    }

    public static Geometry Create(Size source, Size viewport, WallpaperLayoutMode mode, WallpaperTransform transform,
        Size referenceSize = default, Size logicalSourceSize = default)
    {
        var current = transform.Normalize();
        // Use exactly the same float translation as WallpaperTransform.CreateMatrix.
        // Taking a different rounding path at a half-pixel would shift snapshots.
        float x = viewport.Width * ((1 - current.Scale) / 2 + current.OffsetX);
        float y = viewport.Height * ((1 - current.Scale) / 2 + current.OffsetY);
        return Create(source, viewport, mode, current.Scale, current.Scale, x, y, referenceSize, logicalSourceSize);
    }

    public static Geometry Create(Size source, Size viewport, WallpaperLayoutMode mode,
        double scaleX, double scaleY, double translateX, double translateY,
        Size referenceSize = default, Size logicalSourceSize = default)
    {
        var destination = Destination(source, viewport, mode, referenceSize, logicalSourceSize);
        var mapped = Rectangle.FromLTRB((int)Math.Round(destination.X * scaleX + translateX),
            (int)Math.Round(destination.Y * scaleY + translateY),
            (int)Math.Round((destination.X + destination.Width) * scaleX + translateX),
            (int)Math.Round((destination.Y + destination.Height) * scaleY + translateY));
        return new(mapped, MapClip(new(0, 0, viewport.Width, viewport.Height), scaleX, scaleY, translateX, translateY),
            translateX, translateY, destination.Width * scaleX, destination.Height * scaleY, Normalize(mode) == WallpaperLayoutMode.Tile);
    }

    public static Rectangle MapClip(RectangleF clip, double scaleX, double scaleY, double translateX, double translateY) =>
        Rectangle.FromLTRB((int)Math.Ceiling(clip.Left * scaleX + translateX), (int)Math.Ceiling(clip.Top * scaleY + translateY),
            (int)Math.Floor(clip.Right * scaleX + translateX), (int)Math.Floor(clip.Bottom * scaleY + translateY));

    // Fit/Center/Tile always compose in the scratch buffer, including their
    // backgrounds. Only a full Fill/Stretch canvas can use the one-blit path.
    public static bool CoversViewport(Size viewport, WallpaperLayoutMode mode, WallpaperTransform transform)
    {
        if (viewport.Width <= 0 || viewport.Height <= 0 || Normalize(mode) is not (WallpaperLayoutMode.Fill or WallpaperLayoutMode.Stretch)) return false;
        var clip = Create(new(1, 1), viewport, WallpaperLayoutMode.Stretch, transform).CanvasClip;
        return clip.Left <= 0 && clip.Top <= 0 && clip.Right >= viewport.Width && clip.Bottom >= viewport.Height;
    }

    public static void DrawImage(Graphics graphics, Image image, Size viewport, WallpaperLayoutMode mode,
        Size referenceSize = default, Size logicalSourceSize = default)
    {
        var destination = Destination(image.Size, viewport, mode, referenceSize, logicalSourceSize);
        if (Normalize(mode) == WallpaperLayoutMode.Tile)
        {
            using var texture = new TextureBrush(image, WrapMode.Tile);
            texture.ScaleTransform((float)(destination.Width / image.Width), (float)(destination.Height / image.Height));
            graphics.FillRectangle(texture, new Rectangle(Point.Empty, viewport));
            return;
        }
        graphics.DrawImage(image, (float)destination.X, (float)destination.Y, (float)destination.Width, (float)destination.Height);
    }

    public static void Draw(Graphics graphics, Image image, Size viewport, WallpaperLayoutMode mode,
        Size referenceSize = default, Size logicalSourceSize = default) => DrawImage(graphics, image, viewport, mode, referenceSize, logicalSourceSize);
}
