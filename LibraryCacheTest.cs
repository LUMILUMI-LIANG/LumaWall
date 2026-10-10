using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;

namespace LumaWall;

static class LibraryCacheTest
{
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void Until(Func<bool> condition, string message)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > 20) throw new TimeoutException(message);
            Application.DoEvents(); Thread.Sleep(5);
        }
    }
    static void Wait(Task work) { Until(() => work.IsCompleted, "Fixture generation timed out."); work.GetAwaiter().GetResult(); }
    static Dictionary<string, Bitmap> Thumbnails(ThumbnailLibrary library) => (Dictionary<string, Bitmap>)typeof(ThumbnailLibrary).GetField("thumbnails", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(library)!;
    public static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "library-cache-fixtures-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string cache = Path.Combine(root, "cache");
        var items = new List<Wallpaper>();
        for (int i = 0; i < 180; i++)
        {
            string path = Path.Combine(root, i + ".png");
            using var image = new Bitmap(40, 24); using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.FromArgb(255, 20 + i, 40, 60)); image.Save(path, ImageFormat.Png);
            items.Add(new("图片 " + i, "image", path));
        }
        using (var library = new ThumbnailLibrary { Size = new(620, 240), CacheFolder = cache })
        {
            int generated = ThumbnailCache.Generated;
            library.SetItems(items);
            Check(ReferenceEquals(library.Items, items), "SetItems must retain the supplied list for name updates.");
            Check(library.CachedThumbnailCount == 0 && library.LoadingCount == 0 && ThumbnailCache.Generated == generated, "Library population must not decode media or allocate thumbnails before a viewport exists.");
            _ = library.Handle;
            Until(() => library.LoadingCount == 0 && library.PosterReady(items[0].Key), "Visible image thumbnails did not complete.");
            Check(library.CachedThumbnailCount <= 8 && !library.PosterReady(items[100].Key), "Only viewport and a small prefetch area should be decoded.");
            for (int i = 0; i < items.Count; i += 6)
            {
                library.SelectedIndex = i;
                Until(() => library.LoadingCount == 0 && library.PosterReady(items[i].Key), "Selected thumbnail did not complete.");
                Check(library.CachedThumbnailCount <= 64, "In-memory thumbnail cache exceeded 64 bitmaps.");
            }
            Check(library.CachedThumbnailCount == 64, "Large library browsing must use a bounded LRU cache.");
            int before = ThumbnailCache.Generated;
            var bitmap = Thumbnails(library)[items[174].Key]; items[174] = items[174] with { Name = "重命名后的显示名称" };
            library.SetItems(items);
            Check(ReferenceEquals(bitmap, Thumbnails(library)[items[174].Key]) && ThumbnailCache.Generated == before, "Name changes must retain decoded thumbnails.");
        }
        using (var reopened = new ThumbnailLibrary { Size = new(620, 240), CacheFolder = cache })
        {
            int hits = ThumbnailCache.DiskHits;
            reopened.SetItems(items); _ = reopened.Handle;
            Until(() => reopened.LoadingCount == 0 && reopened.PosterReady(items[0].Key), "Disk cache reload did not complete.");
            Check(ThumbnailCache.DiskHits > hits, "Reopening must read existing thumbnails from disk.");
            string path = items[0].Path!, oldSignature = ThumbnailCache.Signature(items[0]);
            using (var image = new Bitmap(40, 24)) { using var g = Graphics.FromImage(image); g.Clear(Color.Lime); image.Save(path, ImageFormat.Png); }
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
            Check(ThumbnailCache.Signature(items[0]) != oldSignature, "Source size or modification time must invalidate the cache.");
            reopened.SetItems(items);
            Until(() => reopened.LoadingCount == 0 && reopened.PosterReady(items[0].Key), "Modified file thumbnail did not reload.");
            Check(Thumbnails(reopened)[items[0].Key].GetPixel(240, 135).G > 200, "Changed source must replace cached pixels.");
            var missing = new Wallpaper("失效文件", "image", Path.Combine(root, "missing.png"));
            reopened.SetItems([missing]);
            Check(reopened.PosterState(missing.Key).StartsWith("failed:", StringComparison.Ordinal), "Missing sources must expose a failure placeholder.");
        }
        string video = Path.Combine(root, "video.mp4"); Wait(Task.Run(() => VideoTestFixture.Create(video)));
        var videos = new List<Wallpaper>();
        for (int i = 0; i < 10; i++) { string path = Path.Combine(root, "video-" + i + ".mp4"); File.Copy(video, path); videos.Add(new("视频 " + i, "video", path)); }
        ThumbnailCache.PeakVideos = 0;
        using (var library = new ThumbnailLibrary { Size = new(620, 400), CacheFolder = cache })
        {
            library.SetItems(videos); _ = library.Handle;
            Until(() => library.LoadingCount == 0 && library.PosterReady(videos[0].Key), "Video poster batch did not complete.");
            Check(ThumbnailCache.PeakVideos is > 0 and <= 2, "Actual video decoder concurrency must never exceed two.");
            Check(Thumbnails(library)[videos[0].Key].GetPixel(420, 180).R > 160, "Video posters must contain decoded pixels.");
        }
        var cancelled = new ThumbnailLibrary { Size = new(620, 400) };
        cancelled.SetItems(videos); _ = cancelled.Handle; cancelled.Dispose();
        Until(() => ThumbnailCache.ActiveVideos == 0, "Cancelled native video decoders did not release their slots.");
        Application.DoEvents();
        Check(cancelled.CachedThumbnailCount == 0 && cancelled.LoadingCount == 0, "Disposal must release cached bitmaps and prevent late UI publication.");
        // All files belong to this new GUID fixture directory; never touch app data.
        Check(root.StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase), "Cleanup must stay in the test output directory.");
        Directory.Delete(root, true);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "library-cache-test.txt"), "PASS: viewport-only asynchronous thumbnails; direct list identity; 64 bitmap LRU; display-name reuse; persistent disk hits; modification-time invalidation; missing-source placeholder; two actual video decoder slots; disposal cancellation and bitmap ownership.");
    }
}
