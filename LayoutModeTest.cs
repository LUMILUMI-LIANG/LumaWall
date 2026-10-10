using System.Diagnostics;
using System.Drawing.Imaging;

namespace LumaWall;

static class LayoutModeTest
{
    sealed class InspectableScene : Scene
    {
        public bool UsesFullCoverPath => NativeFrameCoversViewport;
        public bool DrawNative(IntPtr dc) => TryRenderNative(dc, ClientSize, new(Point.Empty, ClientSize));
    }
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static Bitmap Capture(Scene scene)
    {
        var bitmap = new Bitmap(scene.Width, scene.Height); scene.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size)); return bitmap;
    }
    static void Pixel(Bitmap bitmap, int x, int y, Color expected, string message)
    {
        var actual = bitmap.GetPixel(x, y);
        Check(Math.Abs(actual.R - expected.R) <= 3 && Math.Abs(actual.G - expected.G) <= 3 && Math.Abs(actual.B - expected.B) <= 3,
            $"{message}: ({x},{y}) expected {expected}, received {actual}.");
    }
    static void Wait(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("Layout fixture readiness timed out.");
            Application.DoEvents(); Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
    }
    static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > 5) throw new TimeoutException("Layout transition timed out.");
            Application.DoEvents(); Thread.Sleep(5);
        }
    }
    public static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "layout-fixtures-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string small = Path.Combine(root, "quadrants.png"), gif = Path.Combine(root, "quadrants.gif"), wide = Path.Combine(root, "wide.png"), portrait = Path.Combine(root, "portrait.png"), video = Path.Combine(root, "video.mp4");
        try
        {
            using (var image = new Bitmap(80, 40))
            {
                using var g = Graphics.FromImage(image);
                g.FillRectangle(Brushes.Red, 0, 0, 40, 20); g.FillRectangle(Brushes.Green, 40, 0, 40, 20);
                g.FillRectangle(Brushes.Blue, 0, 20, 40, 20); g.FillRectangle(Brushes.Yellow, 40, 20, 40, 20);
                image.Save(small, ImageFormat.Png); image.Save(gif, ImageFormat.Gif);
            }
            using (var image = new Bitmap(320, 90))
            {
                using var g = Graphics.FromImage(image);
                g.FillRectangle(Brushes.Green, 0, 0, 80, 90); g.FillRectangle(Brushes.Red, 80, 0, 80, 90);
                g.FillRectangle(Brushes.Blue, 160, 0, 80, 90); g.FillRectangle(Brushes.Yellow, 240, 0, 80, 90);
                image.Save(wide, ImageFormat.Png);
            }
            using (var image = new Bitmap(90, 320))
            {
                using var g = Graphics.FromImage(image);
                g.FillRectangle(Brushes.Green, 0, 0, 90, 80); g.FillRectangle(Brushes.Red, 0, 80, 90, 80);
                g.FillRectangle(Brushes.Blue, 0, 160, 90, 80); g.FillRectangle(Brushes.Yellow, 0, 240, 90, 80);
                image.Save(portrait, ImageFormat.Png);
            }
            TestImages(small, gif, wide, portrait);
            TestPreviewImages(small, wide);
            TestNativeGeometry(small);
            Wait(Task.Run(() => VideoTestFixture.Create(video))); TestVideo(video); TestPreviewVideo(video);
            TestDenseVideoTiles();
            TestTransition(small);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "layout-mode-test.txt"),
                "PASS: Fill/Fit/Stretch/Center/Tile image and GIF pixels; wide and portrait aspect ratios; transformed tile size/origin and canvas clipping; monitor-relative Center/Tile preview geometry; downsampled video retains original natural size; high-density native tiles use a bounded draw with correct colors, monitor-relative spacing and transformed clipping; safe full-cover native guard; native/GDI video geometry parity across all modes and transforms; changing video layout and preview reference preserves decoder, clock and transforms; layout during crossfade. Isolated fixtures only; no desktop attachment or user settings.");
        }
        finally
        {
            Check(string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), StringComparison.OrdinalIgnoreCase), "Layout fixture cleanup escaped executable directory.");
            Directory.Delete(root, true);
        }
    }
    static void TestPreviewImages(string small, string wide)
    {
        using var scene = new InspectableScene { Size = new(240, 180), Paused = true, LayoutReferenceSize = new(480, 360) };
        scene.LoadScene(new("Preview quadrants", "image", small)); scene.LayoutMode = WallpaperLayoutMode.Center;
        using (var bitmap = Capture(scene))
        {
            Pixel(bitmap, 90, 85, Color.Black, "Center preview scales natural image width against target monitor");
            Pixel(bitmap, 105, 85, Color.Red, "Center preview origin"); Pixel(bitmap, 125, 85, Color.Green, "Center preview right half");
            Pixel(bitmap, 105, 95, Color.Blue, "Center preview height"); Pixel(bitmap, 125, 95, Color.Yellow, "Center preview bottom right");
        }
        scene.LayoutMode = WallpaperLayoutMode.Tile;
        using (var bitmap = Capture(scene))
        {
            Pixel(bitmap, 5, 5, Color.Red, "Tile preview first cell"); Pixel(bitmap, 25, 5, Color.Green, "Tile preview natural cell width");
            Pixel(bitmap, 45, 5, Color.Red, "Tile preview has same count of columns as desktop");
            Pixel(bitmap, 5, 25, Color.Red, "Tile preview has same count of rows as desktop"); Pixel(bitmap, 25, 35, Color.Yellow, "Tile preview natural cell height");
        }
        scene.SetTransform(new(.5f, .25f, 0));
        using (var bitmap = Capture(scene))
        {
            Pixel(bitmap, 110, 50, Color.Black, "Reference tile remains canvas-clipped after pan");
            Pixel(bitmap, 122, 47, Color.Red, "Reference tile composes with zoom/pan origin");
            Pixel(bitmap, 132, 47, Color.Green, "Reference tile composes with zoom scale");
            Pixel(bitmap, 142, 47, Color.Red, "Reference tile retains desktop density after zoom");
        }
        scene.LoadScene(new("Preview wide", "image", wide)); scene.LayoutMode = WallpaperLayoutMode.Center;
        Check(scene.LayoutReferenceSize == new Size(480, 360), "Loading media must retain the target-monitor preview reference.");
        using (var bitmap = Capture(scene))
        {
            Pixel(bitmap, 20, 75, Color.Black, "Large Center preview matches desktop margin");
            Pixel(bitmap, 60, 75, Color.Green, "Large Center preview shows uncropped left band");
            Pixel(bitmap, 110, 75, Color.Red, "Large Center preview red band"); Pixel(bitmap, 150, 75, Color.Blue, "Large Center preview blue band");
            Pixel(bitmap, 180, 75, Color.Yellow, "Large Center preview shows uncropped right band");
        }
        scene.LayoutMode = WallpaperLayoutMode.Tile;
        using (var bitmap = Capture(scene))
        {
            Pixel(bitmap, 20, 10, Color.Green, "Wide reference tile left band"); Pixel(bitmap, 140, 10, Color.Yellow, "Wide reference tile right band");
            Pixel(bitmap, 180, 10, Color.Green, "Wide reference tile horizontal repeat"); Pixel(bitmap, 20, 60, Color.Green, "Wide reference tile vertical repeat");
        }
        scene.LayoutReferenceSize = new(-1, 360);
        Check(scene.LayoutReferenceSize.IsEmpty, "Invalid reference dimensions must normalize to own-viewport geometry.");
    }
    static void TestImages(string small, string gif, string wide, string portrait)
    {
        using var scene = new InspectableScene { Size = new(240, 180), Paused = true };
        scene.LoadScene(new("Quadrants", "image", small));
        foreach (var mode in new[] { WallpaperLayoutMode.Fill, WallpaperLayoutMode.Stretch })
        {
            scene.LayoutMode = mode; using var bitmap = Capture(scene);
            Pixel(bitmap, 10, 10, Color.Red, mode + " top left"); Pixel(bitmap, 230, 10, Color.Green, mode + " top right");
            Pixel(bitmap, 10, 170, Color.Blue, mode + " bottom left"); Pixel(bitmap, 230, 170, Color.Yellow, mode + " bottom right");
            Check(scene.UsesFullCoverPath, mode + " must allow a complete full-viewport frame.");
        }
        scene.LayoutMode = WallpaperLayoutMode.Fit;
        using (var bitmap = Capture(scene))
        {
            Pixel(bitmap, 120, 10, Color.Black, "Fit top bar"); Pixel(bitmap, 120, 170, Color.Black, "Fit bottom bar");
            Pixel(bitmap, 10, 50, Color.Red, "Fit preserves whole image"); Pixel(bitmap, 230, 140, Color.Yellow, "Fit lower right");
        }
        Check(!scene.UsesFullCoverPath, "Fit backgrounds must use complete scratch composition.");
        scene.LayoutMode = WallpaperLayoutMode.Center;
        using (var bitmap = Capture(scene))
        {
            Pixel(bitmap, 10, 90, Color.Black, "Center retains original image size"); Pixel(bitmap, 90, 80, Color.Red, "Center origin");
            Pixel(bitmap, 130, 80, Color.Green, "Center right half"); Pixel(bitmap, 90, 100, Color.Blue, "Center bottom half");
        }
        Check(!scene.UsesFullCoverPath, "Center must compose its background.");
        scene.LayoutMode = WallpaperLayoutMode.Tile;
        using (var bitmap = Capture(scene))
        {
            Pixel(bitmap, 10, 10, Color.Red, "Tile first cell"); Pixel(bitmap, 90, 10, Color.Red, "Tile repeats at original width");
            Pixel(bitmap, 10, 50, Color.Red, "Tile repeats at original height"); Pixel(bitmap, 130, 70, Color.Yellow, "Tile preserves each cell's pixels");
        }
        Check(!scene.UsesFullCoverPath, "Tile must compose its entire background.");
        scene.SetTransform(new(.5f, .25f, 0));
        using (var bitmap = Capture(scene))
        {
            Pixel(bitmap, 100, 90, Color.Black, "Tile moved-canvas left margin"); Pixel(bitmap, 130, 20, Color.Black, "Tile scaled-canvas top margin");
            Pixel(bitmap, 130, 50, Color.Red, "Tile transform origin"); Pixel(bitmap, 150, 50, Color.Green, "Tile transformed width");
            Pixel(bitmap, 170, 50, Color.Red, "Tile repeat scales with canvas");
        }
        scene.SetTransform(new()); scene.LoadScene(new("GIF quadrants", "image", gif)); scene.LayoutMode = WallpaperLayoutMode.Fit;
        using (var bitmap = Capture(scene)) { Pixel(bitmap, 120, 10, Color.Black, "GIF Fit bar"); Pixel(bitmap, 10, 50, Color.Red, "GIF Fit content"); }
        scene.Size = new(320, 180); scene.LoadScene(new("Wide", "image", wide));
        scene.LayoutMode = WallpaperLayoutMode.Fill;
        using (var bitmap = Capture(scene)) { Pixel(bitmap, 20, 90, Color.Red, "Fill crops wide green edge"); Pixel(bitmap, 300, 90, Color.Blue, "Fill crops wide yellow edge"); }
        scene.LayoutMode = WallpaperLayoutMode.Stretch;
        using (var bitmap = Capture(scene)) { Pixel(bitmap, 20, 90, Color.Green, "Stretch retains wide left edge"); Pixel(bitmap, 300, 90, Color.Yellow, "Stretch retains wide right edge"); }
        scene.LoadScene(new("Portrait", "image", portrait)); scene.LayoutMode = WallpaperLayoutMode.Fit;
        using (var bitmap = Capture(scene)) { Pixel(bitmap, 20, 60, Color.Black, "Portrait Fit left bar"); Pixel(bitmap, 300, 60, Color.Black, "Portrait Fit right bar"); Pixel(bitmap, 160, 60, Color.Red, "Portrait Fit entire image"); }
        scene.LayoutMode = (WallpaperLayoutMode)99; Check(scene.LayoutMode == WallpaperLayoutMode.Fill, "Invalid modes must normalize to Fill.");
    }
    static void TestNativeGeometry(string small)
    {
        using var scene = new InspectableScene { Size = new(240, 180), Paused = true };
        scene.LoadScene(new("Geometry", "image", small));
        foreach (var mode in Enum.GetValues<WallpaperLayoutMode>())
        {
            scene.LayoutMode = mode; scene.SetTransform(new());
            Check(scene.UsesFullCoverPath == (mode is WallpaperLayoutMode.Fill or WallpaperLayoutMode.Stretch), mode + " default full-cover guard.");
            scene.SetTransform(new(.7f)); Check(!scene.UsesFullCoverPath, mode + " zoom-out must use complete background composition.");
            scene.SetTransform(new(1.2f, .1f, .1f));
            Check(scene.UsesFullCoverPath == (mode is WallpaperLayoutMode.Fill or WallpaperLayoutMode.Stretch), mode + " zoom/pan full-cover guard.");
            scene.SetTransform(new(1.2f, .3f, .1f)); Check(!scene.UsesFullCoverPath, mode + " exposed canvas left edge must not take fast path.");
        }
    }
    static void TestVideo(string path)
    {
        using var scene = new InspectableScene { Size = new(240, 180), Paused = true, AudioEnabled = false };
        scene.LoadScene(new("Real video", "video", path)); Wait(scene.WaitUntilReadyAsync());
        var source = scene.Video!; long frames = source.FrameCount, clock = source.Clock.PositionTicks;
        foreach (var transform in new[] { new WallpaperTransform(), new WallpaperTransform(.65f, .12f, -.08f), new WallpaperTransform(1.2f, -.15f, .1f) })
            foreach (var mode in Enum.GetValues<WallpaperLayoutMode>())
            {
                scene.SetTransform(transform); scene.LayoutMode = mode;
                using var managed = Capture(scene); using var native = new Bitmap(scene.Width, scene.Height);
                using (var graphics = Graphics.FromImage(native))
                {
                    graphics.Clear(Color.Black); var dc = graphics.GetHdc();
                    try { Check(scene.DrawNative(dc), "Native video draw failed for " + mode); }
                    finally { graphics.ReleaseHdc(dc); }
                }
                for (int y = 3; y < scene.Height; y += 11) for (int x = 3; x < scene.Width; x += 13)
                    Pixel(native, x, y, managed.GetPixel(x, y), $"Native/GDI video parity {mode}, transform={transform}");
                Check(ReferenceEquals(scene.Video, source) && scene.Transform == transform && source.Paused && source.FrameCount == frames && source.Clock.PositionTicks == clock,
                    "Layout changes must preserve video decoder, pause, clock, current frame and editing transform.");
            }
    }
    static void TestPreviewVideo(string path)
    {
        using var scene = new InspectableScene { Size = new(160, 120), Paused = true, AudioEnabled = false,
            VideoMaximumWidth = 80, LayoutReferenceSize = new(640, 480) };
        scene.LoadScene(new("Downsampled preview", "video", path)); Wait(scene.WaitUntilReadyAsync());
        var source = scene.Video!; long frames = source.FrameCount, clock = source.Clock.PositionTicks;
        Check(source.SourceSize == new Size(320, 180), "Video preview must expose original dimensions before decoder scaling.");
        using (var frame = source.CopyFrame()) Check(frame?.Width == 80, "Video reference test must exercise an actually downsampled decoder.");
        foreach (var mode in Enum.GetValues<WallpaperLayoutMode>())
        {
            scene.LayoutMode = mode;
            using var managed = Capture(scene); using var native = new Bitmap(scene.Width, scene.Height);
            using (var graphics = Graphics.FromImage(native))
            {
                graphics.Clear(Color.Black); var dc = graphics.GetHdc();
                try { Check(scene.DrawNative(dc), "Native reference video draw failed for " + mode); }
                finally { graphics.ReleaseHdc(dc); }
            }
            for (int y = 3; y < scene.Height; y += 11) for (int x = 3; x < scene.Width; x += 13)
                Pixel(native, x, y, managed.GetPixel(x, y), "Downsampled reference video native/GDI parity " + mode);
            if (mode == WallpaperLayoutMode.Center)
            {
                Pixel(managed, 20, 50, Color.Black, "Center video preview original natural width margin");
                Pixel(managed, 60, 50, Color.Red, "Center video preview original-size content");
                Pixel(managed, 60, 90, Color.Black, "Center video preview original natural height margin");
            }
            if (mode == WallpaperLayoutMode.Tile)
            {
                var repeated = managed.GetPixel(82, 3);
                Check(repeated.G > 180 && repeated.R < 40 && repeated.B < 40, "Video tile preview must repeat every 80 monitor-relative pixels, independent of decoded storage.");
                repeated = managed.GetPixel(3, 47);
                Check(repeated.G > 180 && repeated.R < 40 && repeated.B < 40, "Video tile preview must repeat at original 180px height scaled to 45px, not decoder-rounded height.");
                Pixel(managed, 100, 20, Color.Red, "Video tile original-size cell content");
            }
        }
        scene.LayoutMode = WallpaperLayoutMode.Center; scene.LayoutReferenceSize = new(320, 240);
        using (var bitmap = Capture(scene)) Pixel(bitmap, 30, 35, Color.Red, "Changing target monitor updates natural-size video preview geometry.");
        Check(ReferenceEquals(scene.Video, source) && source.Paused && source.FrameCount == frames && source.Clock.PositionTicks == clock,
            "Changing preview reference must preserve the live decoder, pause state, clock and frame.");
    }
    static void TestTransition(string small)
    {
        using var scene = new TransitionScene { Size = new(240, 180), Paused = true, LayoutMode = WallpaperLayoutMode.Fit };
        scene.LoadScene(new("Incoming", "image", small));
        var snapshot = new Bitmap(scene.Width, scene.Height); using (var graphics = Graphics.FromImage(snapshot)) graphics.Clear(Color.Red);
        scene.BeginTransition(snapshot, .5);
        using (var first = Capture(scene)) Pixel(first, 210, 10, Color.Red, "Layout transition starts with complete previous snapshot.");
        scene.Paused = false; Until(() => scene.TransitionProgress >= .35); scene.Paused = true;
        scene.LayoutMode = WallpaperLayoutMode.Stretch;
        using (var blend = Capture(scene))
        {
            var color = blend.GetPixel(210, 10);
            Check(color.R > 20 && color.G > 15 && color.B < 10, "A mode change during fade must draw the new Stretch content under the previous snapshot.");
        }
        scene.Paused = false; Until(() => scene.TransitionProgress >= 1);
        using var finished = Capture(scene); Pixel(finished, 210, 10, Color.Green, "Completed layout transition exposes current Stretch mode.");
    }
    static void TestDenseVideoTiles()
    {
        var viewport = new Size(1920, 1080);
        using (var source = new Bitmap(1, 1))
        {
            source.SetPixel(0, 0, Color.Lime); using var frame = new VideoDrawing(source); using var target = new Bitmap(viewport.Width, viewport.Height);
            using var graphics = Graphics.FromImage(target); graphics.Clear(Color.Black); var dc = graphics.GetHdc();
            var watch = Stopwatch.StartNew();
            try { Check(frame.DrawHdc(dc, viewport, new(), new(Point.Empty, viewport), WallpaperLayoutMode.Tile), "Dense single-pixel video tile draw failed."); }
            finally { graphics.ReleaseHdc(dc); }
            Check(watch.Elapsed.TotalSeconds < 1.5, "A two-million-cell video tile must use a bounded fill, not millions of blits: " + watch.Elapsed.TotalMilliseconds + "ms.");
            Pixel(target, 0, 0, Color.Lime, "Dense tile first pixel"); Pixel(target, 1919, 1079, Color.Lime, "Dense tile entire viewport");
            Pixel(target, 956, 538, Color.Lime, "Dense tile center");
        }
        using (var source = new Bitmap(16, 16))
        {
            using (var graphics = Graphics.FromImage(source))
            {
                graphics.FillRectangle(Brushes.Red, 0, 0, 8, 8); graphics.FillRectangle(Brushes.Green, 8, 0, 8, 8);
                graphics.FillRectangle(Brushes.Blue, 0, 8, 8, 8); graphics.FillRectangle(Brushes.Yellow, 8, 8, 8, 8);
            }
            using var frame = new VideoDrawing(source);
            var reference = new Size(3840, 2160);
            using (var target = new Bitmap(viewport.Width, viewport.Height))
            {
                using var graphics = Graphics.FromImage(target); graphics.Clear(Color.Black); var dc = graphics.GetHdc();
                try { Check(frame.DrawHdc(dc, viewport, new(), new(Point.Empty, viewport), WallpaperLayoutMode.Tile, reference, source.Size), "Dense reference tile draw failed."); }
                finally { graphics.ReleaseHdc(dc); }
                Pixel(target, 1, 1, Color.Red, "Dense reference tile first quadrant"); Pixel(target, 5, 1, Color.Green, "Dense reference tile scaled width");
                Pixel(target, 1, 5, Color.Blue, "Dense reference tile scaled height"); Pixel(target, 5, 5, Color.Yellow, "Dense reference tile lower right");
                Pixel(target, 9, 1, Color.Red, "Dense reference tile horizontal repeat"); Pixel(target, 1, 9, Color.Red, "Dense reference tile vertical repeat");
            }
            using (var target = new Bitmap(viewport.Width, viewport.Height))
            {
                using var graphics = Graphics.FromImage(target); graphics.Clear(Color.Black); var dc = graphics.GetHdc();
                try { Check(frame.DrawHdc(dc, viewport, new(.5f, .25f, 0), new(Point.Empty, viewport), WallpaperLayoutMode.Tile, reference, source.Size), "Dense transformed reference tile draw failed."); }
                finally { graphics.ReleaseHdc(dc); }
                Pixel(target, 959, 275, Color.Black, "Dense transformed tile retains clipped left margin");
                Pixel(target, 960, 269, Color.Black, "Dense transformed tile retains clipped top margin");
                Pixel(target, 961, 271, Color.Red, "Dense transformed tile origin"); Pixel(target, 963, 271, Color.Green, "Dense transformed tile zoom");
                Pixel(target, 965, 271, Color.Red, "Dense transformed tile repeat"); Pixel(target, 1919, 900, Color.Black, "Dense transformed tile outside canvas");
            }
        }
    }
}
