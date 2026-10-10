using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LumaWall;

static class PerformanceTest
{
    const double ClipSeconds = 4;
    const int FrameRate = 30;
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void Wait(Task task, int seconds = 60)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException("Performance fixture/readiness timed out.");
            Application.DoEvents(); Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
    }
    static void Until(Func<bool> condition, string message, double seconds = 8)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException(message);
            Thread.Sleep(5);
        }
    }
    static double Position(VideoPlayback playback) => playback.Clock.PositionTicks / 10000000.0;
    static double PresentedPosition(VideoPlayback playback)
    {
        // Read around the loop counter to avoid combining adjacent loop values.
        for (int attempt = 0; attempt < 20; attempt++)
        {
            long loops = playback.LoopCount, timestamp = playback.CurrentTimestamp;
            if (loops == playback.LoopCount)
                return loops * (playback.DurationTicks > 0 ? playback.DurationTicks / 10000000.0 : ClipSeconds) + timestamp / 10000000.0;
        }
        throw new InvalidOperationException("Playback loop changed too quickly to sample.");
    }
    static long Fingerprint(Bitmap image)
    {
        long hash = 17;
        for (int y = 20; y < image.Height; y += 91)
            for (int x = 20; x < image.Width; x += 83)
                hash = unchecked(hash * 31 + image.GetPixel(x, y).ToArgb());
        return hash;
    }

    public static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "performance-fixtures-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "1080p-30fps.mp4");
        var results = new List<string>();
        using var process = Process.GetCurrentProcess();
        Wait(Task.Run(() => PerformanceFixture.Create(path, 1920, 1080, FrameRate, (int)ClipSeconds)));
        var posterTask = VideoMedia.GetPosterAsync(path); Wait(posterTask);
        using (var poster = posterTask.GetAwaiter().GetResult())
            Check(poster.Size == new Size(1920, 1080), $"Fixture must decode at real 1920×1080, received {poster.Size}.");

        using (var playback = new VideoPlayback(path, true))
        {
            Wait(playback.Ready);
            Check(playback.DurationTicks > 0 && Math.Abs(playback.DurationTicks / 10000000.0 - ClipSeconds) < .08, $"Real 30fps fixture duration must be four seconds, received {playback.DurationTicks} ticks.");
            double startPosition = Position(playback); long startFrames = playback.FrameCount;
            TimeSpan startCpu = process.TotalProcessorTime;
            playback.Paused = false;
            var normal = Stopwatch.StartNew(); Thread.Sleep(1600);
            double normalWall = normal.Elapsed.TotalSeconds, normalClock = Position(playback) - startPosition;
            double presented = PresentedPosition(playback) - startPosition;
            double fps = (playback.FrameCount - startFrames) / normalWall;
            Check(Math.Abs(normalClock - normalWall) < .12, $"Media clock must run at 1× wall time; wall={normalWall:F3}s, clock={normalClock:F3}s.");
            Check(presented >= normalWall - .25 && presented <= normalWall + .12, $"Displayed frames must follow wall time, not slowly accumulate delay; wall={normalWall:F3}s, presented={presented:F3}s.");
            Check(fps >= 24 && fps <= FrameRate + 2, $"1080p30 playback must present a smooth normal-speed cadence; measured {fps:F1}fps.");
            results.Add($"1080p normal: {fps:F1} presented fps; wall={normalWall:F3}s; clock={normalClock:F3}s; displayed={presented:F3}s; dropped={playback.DroppedFrameCount}; CPU={(process.TotalProcessorTime - startCpu).TotalSeconds / normalWall * 100:F1}% of one core.");

            playback.Paused = true;
            long frozenFrames = playback.FrameCount, frozenTimestamp = playback.CurrentTimestamp, frozenLoops = playback.LoopCount;
            double frozenPosition = Position(playback);
            using var frozen = playback.CopyFrame()!; long fingerprint = Fingerprint(frozen);
            Thread.Sleep(300);
            using (var afterPause = playback.CopyFrame()!)
                Check(Fingerprint(afterPause) == fingerprint && playback.FrameCount == frozenFrames && playback.CurrentTimestamp == frozenTimestamp
                    && playback.LoopCount == frozenLoops && Math.Abs(Position(playback) - frozenPosition) < .001,
                    "Pause must freeze pixels, presentation timestamps, loop count and the master clock.");
            playback.Paused = false;
            var resumed = Stopwatch.StartNew(); Thread.Sleep(700);
            double resumedWall = resumed.Elapsed.TotalSeconds;
            Check(Math.Abs(Position(playback) - frozenPosition - resumedWall) < .12, "Resume must continue at 1× without counting paused wall time.");

            double beforeLoop = Position(playback);
            Until(() => playback.LoopCount >= 1 && playback.CurrentTimestamp < 10000000, "1080p playback did not loop at its media duration.");
            double afterLoop = Position(playback);
            Check(afterLoop >= beforeLoop && afterLoop >= ClipSeconds - .15 && playback.CurrentTimestamp < 10000000,
                $"Loop must preserve a continuous clock and restart media timestamps; clock={afterLoop:F3}s; timestamp={playback.CurrentTimestamp}.");
            results.Add($"pause/resume and loop: continuous clock={afterLoop:F3}s; loops={playback.LoopCount}.");

            // A sparse/slow renderer must not become the video playback clock.
            // Render only about four times per second while decoding remains 30fps.
            using var canvas = new Bitmap(3440, 1440, PixelFormat.Format32bppRgb);
            using var graphics = Graphics.FromImage(canvas);
            double slowStart = Position(playback), mediaStart = PresentedPosition(playback);
            long slowFrames = playback.FrameCount; int draws = 0;
            var slow = Stopwatch.StartNew();
            while (slow.Elapsed.TotalSeconds < 1.7)
            {
                Check(playback.Draw(graphics, canvas.Size), "Slow-consumer draw did not produce a video frame.");
                draws++; Thread.Sleep(220);
            }
            double slowWall = slow.Elapsed.TotalSeconds, slowClock = Position(playback) - slowStart;
            double slowPresented = PresentedPosition(playback) - mediaStart;
            double slowFps = (playback.FrameCount - slowFrames) / slowWall;
            Check(Math.Abs(slowClock - slowWall) < .15 && Math.Abs(slowPresented - slowWall) < .3,
                $"A slow consumer must not reduce media speed; wall={slowWall:F3}s, clock={slowClock:F3}s, displayed={slowPresented:F3}s.");
            Check(slowFps >= 24 && playback.FrameCount - slowFrames > draws * 3, $"Sparse drawing must not throttle frame publication; frame fps={slowFps:F1}; draws={draws}.");
            Check(canvas.GetPixel(1000, 900).ToArgb() != Color.Black.ToArgb(), "Slow-consumer benchmark must draw actual decoded video pixels.");
            results.Add($"slow consumer: {draws} draws in {slowWall:F3}s; {slowFps:F1} presented fps; clock={slowClock:F3}s; displayed={slowPresented:F3}s; dropped={playback.DroppedFrameCount}; seeks={playback.SeekCount}.");
            Check(playback.Error == null, "Playback must not report an error during timing/performance tests.");
        }

        // The real app decodes both the silent preview and desktop at once.
        using (var preview = new VideoPlayback(path, true))
        using (var desktop = new VideoPlayback(path, true))
        using (var previewCanvas = new Bitmap(400, 225, PixelFormat.Format32bppRgb))
        using (var desktopCanvas = new Bitmap(3440, 1440, PixelFormat.Format32bppRgb))
        using (var previewGraphics = Graphics.FromImage(previewCanvas))
        using (var desktopGraphics = Graphics.FromImage(desktopCanvas))
        {
            Wait(Task.WhenAll(preview.Ready, desktop.Ready));
            Check(!preview.AudioEnabled && !desktop.AudioEnabled, "Performance instances must stay silent unless desktop audio is explicitly activated.");
            long previewFrames = preview.FrameCount, desktopFrames = desktop.FrameCount;
            TimeSpan startCpu = process.TotalProcessorTime;
            preview.Paused = false; desktop.Paused = false;
            var dual = Stopwatch.StartNew();
            var renderTimes = new List<double>(); double maxPreviewGap = 0, maxDesktopGap = 0;
            while (dual.Elapsed.TotalSeconds < 1.8)
            {
                var render = Stopwatch.StartNew();
                Check(preview.Draw(previewGraphics, previewCanvas.Size) && desktop.Draw(desktopGraphics, desktopCanvas.Size), "Both benchmark instances must draw decoded frames.");
                renderTimes.Add(render.Elapsed.TotalMilliseconds);
                maxPreviewGap = Math.Max(maxPreviewGap, Position(preview) - PresentedPosition(preview));
                maxDesktopGap = Math.Max(maxDesktopGap, Position(desktop) - PresentedPosition(desktop));
                Thread.Sleep(15);
            }
            double wall = dual.Elapsed.TotalSeconds;
            double previewFps = (preview.FrameCount - previewFrames) / wall, desktopFps = (desktop.FrameCount - desktopFrames) / wall;
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "performance-progress.txt"), string.Join("\n", results)
                + $"\ndual: preview={previewFps:F1}; desktop={desktopFps:F1}; gap={maxPreviewGap:F3}/{maxDesktopGap:F3}; drawmax={renderTimes.Max():F1}ms; errors={preview.Error}/{desktop.Error}; dropped={preview.DroppedFrameCount}/{desktop.DroppedFrameCount}; seek={preview.SeekCount}/{desktop.SeekCount}.");
            Check(previewFps >= 24 && desktopFps >= 24, $"Two real 1080p30 decoders must preserve cadence; preview={previewFps:F1}fps, desktop={desktopFps:F1}fps.");
            Check(Math.Abs(Position(preview) - wall) < .15 && Math.Abs(Position(desktop) - wall) < .15
                && maxPreviewGap < .3 && maxDesktopGap < .3,
                $"Two instances must remain at 1× without cumulative display lag; max preview gap={maxPreviewGap:F3}s, desktop gap={maxDesktopGap:F3}s.");
            Check(previewCanvas.GetPixel(200, 100).ToArgb() != Color.Black.ToArgb() && desktopCanvas.GetPixel(1000, 900).ToArgb() != Color.Black.ToArgb(), "Both benchmark targets must contain real video pixels.");
            renderTimes.Sort();
            double p95 = renderTimes[(int)Math.Ceiling(renderTimes.Count * .95) - 1];
            results.Add($"simultaneous 1080p30 preview+desktop: preview={previewFps:F1}fps; desktop={desktopFps:F1}fps; max display gaps={maxPreviewGap:F3}/{maxDesktopGap:F3}s; 400×225 + 3440×1440 draw p95={p95:F1}ms; CPU={(process.TotalProcessorTime - startCpu).TotalSeconds / wall * 100:F1}% of one core; dropped={preview.DroppedFrameCount}/{desktop.DroppedFrameCount}.");
        }

        using (var scene = new Scene { Size = new(3440, 1440), Paused = true, Fps = 30 })
        {
            scene.LoadScene(new("Performance", "video", path)); Wait(scene.WaitUntilReadyAsync());
            using var target = new Bitmap(scene.Width, scene.Height, PixelFormat.Format32bppRgb);
            scene.DrawToBitmap(target, new(Point.Empty, target.Size));
            var times = new List<double>(); var paintTimes = new List<double>();
            for (int i = 0; i < 12; i++)
            {
                var draw = Stopwatch.StartNew(); scene.DrawToBitmap(target, new(Point.Empty, target.Size)); times.Add(draw.Elapsed.TotalMilliseconds); paintTimes.Add(scene.LastRenderMilliseconds);
            }
            times.Sort(); double median = times[times.Count / 2], p95 = times[(int)Math.Ceiling(times.Count * .95) - 1];
            paintTimes.Sort(); double medianPaint = paintTimes[paintTimes.Count / 2], p95Paint = paintTimes[(int)Math.Ceiling(paintTimes.Count * .95) - 1];
            Check(medianPaint < 40 && p95Paint < 100, $"Native 3440×1440 scene painting must leave the UI responsive; median={medianPaint:F1}ms, p95={p95Paint:F1}ms.");
            results.Add($"3440×1440 native scene painting: median={medianPaint:F1}ms, p95={p95Paint:F1}ms; full DrawToBitmap captures including buffer copies: median={median:F1}ms, p95={p95:F1}ms (12 captures). No visible window or desktop attachment was created.");
            scene.ClearScene();
        }
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(exclusive.Length > 0, "Performance decoders must release the fixture file.");
        Check(string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), StringComparison.OrdinalIgnoreCase), "Fixture cleanup must stay inside executable directory.");
        Directory.Delete(root, true);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "performance-test.txt"), "PASS: real 1920×1080 H.264 at 30fps; 1× wall-clock presentation; pause/resume; continuous looping; slow consumer independence; simultaneous full-resolution preview+desktop decoders; native rendering timings; file release.\n" + string.Join("\n", results));
    }
}

static class PerformanceFixture
{
    static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71"),
        Bitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"),
        Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSinkWriterFromURL(string path, IntPtr stream, IntPtr attributes, out IntPtr writer);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(int size, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AddStream(IntPtr self, IntPtr type, out uint index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetInputType(IntPtr self, uint index, IntPtr type, IntPtr attributes);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int WriteSample(IntPtr self, uint index, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AddBuffer(IntPtr self, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetLength(IntPtr self, uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetTime(IntPtr self, long value);
    static void Format(IntPtr type, Guid subtype, int width, int height, int fps)
    {
        MfNative.SetGuid(type, MfNative.MajorType, MfNative.VideoType); MfNative.SetGuid(type, MfNative.Subtype, subtype);
        MfNative.SetUInt32(type, Interlace, 2);
        MfNative.SetUInt64(type, MfNative.FrameSize, (ulong)(uint)width << 32 | (uint)height);
        MfNative.SetUInt64(type, MfNative.FrameRate, (ulong)(uint)fps << 32 | 1);
        MfNative.SetUInt64(type, PixelAspect, 1UL << 32 | 1);
    }
    public static void Create(string path, int width, int height, int fps, int seconds, bool highProfile = false)
    {
        bool initialized = false, started = false; IntPtr writer = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero;
        try
        {
            MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, 0)); initialized = true;
            MfNative.Check(MfNative.MFStartup(0x00020070, 0)); started = true;
            MfNative.Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out writer));
            MfNative.Check(MfNative.MFCreateMediaType(out output)); Format(output, H264, width, height, fps);
            MfNative.SetUInt32(output, Bitrate, 8000000);
            if (highProfile) MfNative.SetUInt32(output, new Guid("ad76a80b-2d5c-4e0b-b375-64e520137036"), 100);
            MfNative.Check(MfNative.Method<AddStream>(writer, 3)(writer, output, out uint stream));
            MfNative.Check(MfNative.MFCreateMediaType(out input)); Format(input, MfNative.Rgb32, width, height, fps);
            MfNative.Check(MfNative.Method<SetInputType>(writer, 4)(writer, stream, input, IntPtr.Zero));
            MfNative.Check(MfNative.Method<MfNative.SimpleCall>(writer, 5)(writer));
            byte[] pixels = new byte[checked(width * height * 4)];
            for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 48; pixels[i + 1] = 68; pixels[i + 2] = 96; }
            int previousStripe = -1, stripeWidth = width / 12;
            for (int frame = 0; frame < checked(fps * seconds); frame++)
            {
                int stripe = frame * 23 % (width - stripeWidth);
                for (int y = 0; y < height; y++)
                {
                    if (previousStripe >= 0) for (int x = previousStripe; x < previousStripe + stripeWidth; x++)
                    { int offset = ((height - 1 - y) * width + x) * 4; pixels[offset] = 48; pixels[offset + 1] = 68; pixels[offset + 2] = 96; }
                    for (int x = stripe; x < stripe + stripeWidth; x++)
                    { int offset = ((height - 1 - y) * width + x) * 4; pixels[offset] = 32; pixels[offset + 1] = 210; pixels[offset + 2] = 245; }
                }
                previousStripe = stripe;
                IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero; bool locked = false;
                try
                {
                    MfNative.Check(MFCreateSample(out sample)); MfNative.Check(MFCreateMemoryBuffer(pixels.Length, out buffer));
                    MfNative.Check(MfNative.Method<MfNative.BufferLock>(buffer, 3)(buffer, out IntPtr destination, out _, out _)); locked = true;
                    Marshal.Copy(pixels, 0, destination, pixels.Length); MfNative.Check(MfNative.Method<MfNative.SimpleCall>(buffer, 4)(buffer)); locked = false;
                    MfNative.Check(MfNative.Method<SetLength>(buffer, 6)(buffer, (uint)pixels.Length));
                    MfNative.Check(MfNative.Method<AddBuffer>(sample, 42)(sample, buffer));
                    long timestamp = frame * 10000000L / fps, duration = (frame + 1) * 10000000L / fps - timestamp;
                    MfNative.Check(MfNative.Method<SetTime>(sample, 36)(sample, timestamp)); MfNative.Check(MfNative.Method<SetTime>(sample, 38)(sample, duration));
                    MfNative.Check(MfNative.Method<WriteSample>(writer, 6)(writer, stream, sample));
                }
                finally
                {
                    if (locked) MfNative.Method<MfNative.SimpleCall>(buffer, 4)(buffer);
                    MfNative.Release(ref buffer); MfNative.Release(ref sample);
                }
            }
            MfNative.Check(MfNative.Method<MfNative.SimpleCall>(writer, 11)(writer));
        }
        finally
        {
            MfNative.Release(ref input); MfNative.Release(ref output); MfNative.Release(ref writer);
            if (started) MfNative.MFShutdown(); if (initialized) MfNative.CoUninitialize();
        }
    }
}
