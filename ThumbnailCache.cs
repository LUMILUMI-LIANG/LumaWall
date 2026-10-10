using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text;

namespace LumaWall;

// Work owns the decoder and its slot until native decoding has really finished,
// even when a timeout or disposal stops the caller waiting for it.
static class ThumbnailCache
{
    static readonly SemaphoreSlim workers = new(4), videoWorkers = new(2);
    internal static int DiskHits, Generated, ActiveVideos, PeakVideos;
    internal const int Width = 480, Height = 270;
    internal static string Signature(Wallpaper item)
    {
        string source = item.Key;
        if (item.Path != null)
        {
            var file = new FileInfo(item.Path);
            source += "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("thumb-v2|" + source)));
    }
    internal static async Task<Bitmap> LoadAsync(Wallpaper item, string signature, string? folder, CancellationToken token)
    {
        bool videoSlot = false;
        try
        {
            if (item.Kind == "video") { await videoWorkers.WaitAsync(token).ConfigureAwait(false); videoSlot = true; }
            await workers.WaitAsync(token).ConfigureAwait(false);
        }
        catch { if (videoSlot) videoWorkers.Release(); throw; }
        var work = Task.Run(() =>
        {
            bool counted = false;
            try
            {
                token.ThrowIfCancellationRequested();
                string? cachePath = string.IsNullOrWhiteSpace(folder) ? null : Path.Combine(folder, signature + ".png");
                if (cachePath != null)
                {
                    try
                    {
                        using var cached = Image.FromFile(cachePath);
                        if (cached.Width == Width && cached.Height == Height)
                        {
                            token.ThrowIfCancellationRequested();
                            Interlocked.Increment(ref DiskHits); return new Bitmap(cached);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                }
                Bitmap thumbnail;
                if (item.Kind == "video")
                {
                    int active = Interlocked.Increment(ref ActiveVideos); counted = true;
                    int peak;
                    do { peak = Volatile.Read(ref PeakVideos); if (active <= peak) break; }
                    while (Interlocked.CompareExchange(ref PeakVideos, active, peak) != peak);
                    using var decoder = new VideoDecoder(item.Path!);
                    var frame = decoder.ReadFrame(token) ?? throw new InvalidDataException("视频中没有可显示的画面。");
                    using (frame.Bitmap) thumbnail = Resize(frame.Bitmap);
                }
                else if (item.Path != null)
                {
                    using var source = Image.FromFile(item.Path);
                    thumbnail = Resize(source);
                }
                else thumbnail = DrawBuiltIn(item.Kind);
                try
                {
                    token.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref Generated);
                    if (cachePath != null) Store(thumbnail, cachePath);
                    token.ThrowIfCancellationRequested(); return thumbnail;
                }
                catch { thumbnail.Dispose(); throw; }
            }
            finally
            {
                if (counted) Interlocked.Decrement(ref ActiveVideos);
                if (videoSlot) videoWorkers.Release();
                workers.Release();
            }
        }, CancellationToken.None);
        try { return await work.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false); }
        catch
        {
            _ = work.ContinueWith(done =>
            {
                if (done.Status == TaskStatus.RanToCompletion) done.Result.Dispose();
                else _ = done.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
    internal static Bitmap Resize(Image source)
    {
        var result = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        try
        {
            using var g = Graphics.FromImage(result); g.Clear(Palette.Field);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            float scale = Math.Max(Width / (float)source.Width, Height / (float)source.Height);
            g.DrawImage(source, (Width - source.Width * scale) / 2, (Height - source.Height * scale) / 2, source.Width * scale, source.Height * scale);
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    static void Store(Bitmap bitmap, string path)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            bitmap.Save(temporary, ImageFormat.Png); File.Move(temporary, path, true);
        }
        catch { }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }
    static Bitmap DrawBuiltIn(string kind)
    {
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap); g.SmoothingMode = SmoothingMode.AntiAlias;
        bool ocean = kind == "ocean", space = kind == "space";
        using var bg = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), Color.FromArgb(8, 13, 32), ocean ? Color.FromArgb(8, 61, 79) : Color.FromArgb(26, 20, 61), 90);
        g.FillRectangle(bg, 0, 0, Width, Height);
        var random = new Random(42);
        using var star = new SolidBrush(Color.FromArgb(120, 205, 228, 255));
        for (int i = 0; i < 120; i++) g.FillEllipse(star, (float)random.NextDouble() * Width, (float)random.NextDouble() * Height * .8f, i % 7 == 0 ? 3 : 1.5f, i % 7 == 0 ? 3 : 1.5f);
        if (space)
        {
            float r = Height * .34f, x = Width * .57f, y = Height * .27f;
            using var planet = new LinearGradientBrush(new RectangleF(x, y, r, r), Color.FromArgb(162, 111, 216), Color.FromArgb(21, 26, 57), 35);
            g.FillEllipse(planet, x, y, r, r);
            using var ring = new Pen(Color.FromArgb(100, 192, 169, 237), Height * .015f);
            g.DrawEllipse(ring, x - r * .4f, y + r * .3f, r * 1.8f, r * .4f);
        }
        else for (int layer = 0; layer < 12; layer++)
        {
            using var path = new GraphicsPath(); var points = new PointF[81];
            for (int i = 0; i <= 80; i++) points[i] = new(Width * i / 80f, Height * (ocean ? .6f : .34f) + (float)Math.Sin(i * .075 + layer * .12) * Height * .12f + layer * Height * .013f);
            path.AddCurve(points);
            using var pen = new Pen(ocean ? Color.FromArgb(55, 66, 201, 214) : Color.FromArgb(32, 85 + layer * 6, 232, 183), Height * .035f);
            g.DrawPath(pen, path);
        }
        using var ground = new SolidBrush(Color.FromArgb(9, 15, 27));
        g.FillPolygon(ground, new PointF[] { new(0, Height), new(0, Height * .87f), new(Width * .18f, Height * .75f), new(Width * .34f, Height * .91f), new(Width * .62f, Height * .81f), new(Width, Height * .94f), new(Width, Height) });
        return bitmap;
    }
}
