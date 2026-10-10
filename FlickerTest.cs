using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace LumaWall;

// Sample the real desktop and the actual HWND DC while WM_PAINT is incomplete.
// DrawToBitmap/WM_PRINTCLIENT would create a fresh, complete image and miss this bug.
static class FlickerTest
{
    static readonly Color Red = Color.FromArgb(224, 48, 48), Blue = Color.FromArgb(48, 80, 224);
    enum PixelKind { Invalid, Red, Blue, Black, Other }
    sealed record Probe(Point Screen, Point Client, bool WindowDcValid);
    sealed class BackgroundWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; }
        }
    }
    sealed class ControlledScene : Scene
    {
        public bool ForceComposite { get; set; }
        public bool GeometryCoversViewport => base.NativeFrameCoversViewport;
        protected override bool NativeFrameCoversViewport => !ForceComposite && base.NativeFrameCoversViewport;
    }
    sealed class SlowSurface : NativeVideoSurface
    {
        volatile bool repeat, delay;
        int preparing, blue, failNext;
        long failedRenders;
        long sequence;
        public bool Preparing => Volatile.Read(ref preparing) != 0;
        public SlowSurface() { Dock = DockStyle.Fill; NativeFpsLimit = 60; EnableNativeRendering(true); }
        public void SetColor(bool value) { Volatile.Write(ref blue, value ? 1 : 0); ScheduleNativePaint(); }
        public void StartSlowFrames() { delay = repeat = true; ScheduleNativePaint(); }
        public void StopSlowFrames() { repeat = delay = false; }
        public long FailedRenders => Interlocked.Read(ref failedRenders);
        public void FailNextPaint() { Interlocked.Exchange(ref failNext, 1); ScheduleNativePaint(); }
        protected override bool TryRenderNative(IntPtr dc, Size viewport, Rectangle paintClip)
        {
            // In v0.7 the front buffer has ALREADY been cleared here. A second
            // thread can observe black before the video is ready to fill it.
            GdiFlush(); // Expose the already-issued clear before the intentional delay.
            Volatile.Write(ref preparing, 1);
            try
            {
                if (Interlocked.Exchange(ref failNext, 0) != 0)
                {
                    Thread.Sleep(20); Interlocked.Increment(ref failedRenders); return false;
                }
                if (delay) Thread.Sleep(20);
                var rectangle = new NativeRect { Right = viewport.Width, Bottom = viewport.Height };
                var brush = CreateSolidBrush(ToColorRef(Volatile.Read(ref blue) == 0 ? Red : Blue));
                if (brush == IntPtr.Zero) throw new InvalidOperationException("Cannot create flicker test brush.");
                try { if (FillRect(dc, ref rectangle, brush) == 0) return false; GdiFlush(); }
                finally { DeleteObject(brush); }
                RecordNativeFrame(Interlocked.Increment(ref sequence));
                return true;
            }
            finally
            {
                Volatile.Write(ref preparing, 0);
                if (repeat) { Interlocked.Exchange(ref blue, Volatile.Read(ref blue) == 0 ? 1 : 0); ScheduleNativePaint(); }
            }
        }
    }
    sealed class Metrics
    {
        public long Rounds, Visible, Occluded, ScreenInvalid, WindowInvalid, ScreenRed, ScreenBlue, ScreenBlack, ScreenOther;
        public long WindowSamples, WindowBlack, WindowOther, PreparingScreen, PreparingWindow, PreparingScreenBlack, PreparingWindowBlack;
        public double Seconds, MaxGapMilliseconds;
        public string FirstBad = "";
        public string Describe(string label) => $"{label}: high-frequency capture={Rounds / Math.Max(.001, Seconds):F1} rounds/s, {Seconds:F3}s, max sampler gap={MaxGapMilliseconds:F2}ms; calibrated visible screen samples={Visible}, occluded excluded={Occluded}, invalid screen/window={ScreenInvalid}/{WindowInvalid}; screen red/blue={ScreenRed}/{ScreenBlue}, BLACK={ScreenBlack}, unexpected/old-background={ScreenOther}; HWND DC valid samples={WindowSamples}, BLACK={WindowBlack}, unexpected={WindowOther}; incomplete-WM_PAINT screen/window samples={PreparingScreen}/{PreparingWindow}, BLACK={PreparingScreenBlack}/{PreparingWindowBlack}; first bad={FirstBad}";
    }
    sealed class Sampler : IDisposable
    {
        readonly IntPtr window, host;
        readonly Probe[] probes;
        readonly Func<bool>? preparing;
        readonly ManualResetEventSlim ready = new();
        readonly Task worker;
        int stopping;
        Exception? failure;
        public Metrics Result { get; } = new();
        public Sampler(IntPtr window, IntPtr host, Probe[] probes, Func<bool>? preparing = null)
        {
            this.window = window; this.host = host; this.probes = probes; this.preparing = preparing;
            worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            if (!ready.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("High-frequency capture thread did not start.");
            if (failure != null) throw new InvalidOperationException("Cannot start high-frequency capture.", failure);
        }
        void Run()
        {
            IntPtr timer = IntPtr.Zero;
            var elapsed = Stopwatch.StartNew(); long previous = 0;
            try
            {
                using var pixels = new PixelReader(window);
                timer = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x001f0003);
                if (timer == IntPtr.Zero) throw new InvalidOperationException("A high-resolution sampling timer is required; unavailable coverage cannot be reported as PASS.");
                long due = -10000;
                if (!SetWaitableTimer(timer, ref due, 1, IntPtr.Zero, IntPtr.Zero, false)) throw new InvalidOperationException("Cannot arm high-resolution sampling timer.");
                ready.Set();
                while (Volatile.Read(ref stopping) == 0)
                {
                    WaitForSingleObject(timer, 5);
                    var probe = probes[(int)(Result.Rounds % probes.Length)]; Result.Rounds++;
                    long timestamp = Stopwatch.GetTimestamp();
                    if (previous != 0) Result.MaxGapMilliseconds = Math.Max(Result.MaxGapMilliseconds, Stopwatch.GetElapsedTime(previous, timestamp).TotalMilliseconds);
                    previous = timestamp;
                    if (!Unoccluded(host, probe.Screen)) { Result.Occluded++; continue; }
                    bool during = preparing?.Invoke() == true;
                    var screen = pixels.Screen(probe.Screen);
                    var direct = probe.WindowDcValid ? pixels.Window(probe.Client) : Color.Empty;
                    during &= preparing?.Invoke() == true;
                    if (!Unoccluded(host, probe.Screen)) { Result.Occluded++; continue; }
                    var screenKind = Kind(screen);
                    if (screenKind == PixelKind.Invalid) { Result.ScreenInvalid++; continue; }
                    Result.Visible++; if (during) Result.PreparingScreen++;
                    switch (screenKind)
                    {
                        case PixelKind.Red: Result.ScreenRed++; break;
                        case PixelKind.Blue: Result.ScreenBlue++; break;
                        case PixelKind.Black: Result.ScreenBlack++; if (during) Result.PreparingScreenBlack++; Bad("screen BLACK", screen, probe, during); break;
                        default: Result.ScreenOther++; Bad("screen unexpected", screen, probe, during); break;
                    }
                    if (!probe.WindowDcValid) continue;
                    var windowKind = Kind(direct);
                    if (windowKind == PixelKind.Invalid) { Result.WindowInvalid++; continue; }
                    Result.WindowSamples++; if (during) Result.PreparingWindow++;
                    if (windowKind == PixelKind.Black) { Result.WindowBlack++; if (during) Result.PreparingWindowBlack++; Bad("HWND BLACK", direct, probe, during); }
                    else if (windowKind == PixelKind.Other) { Result.WindowOther++; Bad("HWND unexpected", direct, probe, during); }
                }
            }
            catch (Exception ex) { failure = ex; ready.Set(); }
            finally
            {
                Result.Seconds = elapsed.Elapsed.TotalSeconds;
                if (timer != IntPtr.Zero) { CancelWaitableTimer(timer); CloseHandle(timer); }
            }
        }
        void Bad(string kind, Color color, Probe probe, bool during)
        {
            if (Result.FirstBad.Length == 0) Result.FirstBad = $"{kind} RGB({color.R},{color.G},{color.B}) at {probe.Screen.X},{probe.Screen.Y}, preparing={during}";
        }
        public Metrics Stop()
        {
            Interlocked.Exchange(ref stopping, 1);
            if (!worker.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("High-frequency capture did not stop.");
            if (failure != null) throw new InvalidOperationException("High-frequency capture failed.", failure);
            return Result;
        }
        public void Dispose() { Stop(); ready.Dispose(); }
    }
    // A reusable 1×1 DIB avoids full-screen captures, allocations, stored images,
    // or copying any user content to an external service.
    sealed class PixelReader : IDisposable
    {
        readonly IntPtr window, screenDc, windowDc, target, bitmap, previous, bits;
        public PixelReader(IntPtr window)
        {
            this.window = window;
            screenDc = GetDC(IntPtr.Zero); windowDc = GetDC(window); target = CreateCompatibleDC(screenDc);
            var info = new BitmapInfo { Size = 40, Width = 1, Height = -1, Planes = 1, BitCount = 32, ImageSize = 4 };
            bitmap = CreateDIBSection(screenDc, ref info, 0, out bits, IntPtr.Zero, 0); previous = SelectObject(target, bitmap);
            if (screenDc == IntPtr.Zero || target == IntPtr.Zero || bitmap == IntPtr.Zero || bits == IntPtr.Zero) throw new InvalidOperationException("Cannot create local pixel capture buffer.");
        }
        Color Read(IntPtr source, Point point, uint mode)
        {
            if (source == IntPtr.Zero) return Color.Empty;
            Marshal.WriteInt32(bits, -1);
            if (!BitBlt(target, 0, 0, 1, 1, source, point.X, point.Y, mode)) return Color.Empty;
            GdiFlush(); uint pixel = unchecked((uint)Marshal.ReadInt32(bits));
            if (pixel == uint.MaxValue) return Color.Empty; // Clipped source did not write the destination.
            return Color.FromArgb((int)((pixel >> 16) & 255), (int)((pixel >> 8) & 255), (int)(pixel & 255));
        }
        public Color Screen(Point point) => Read(screenDc, point, 0x40cc0020);
        public Color Window(Point point) => Read(windowDc, point, 0x00cc0020);
        public Color Buffer(IntPtr source, Point point) => Read(source, point, 0x00cc0020);
        public void Dispose()
        {
            if (target != IntPtr.Zero && previous != IntPtr.Zero) SelectObject(target, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (target != IntPtr.Zero) DeleteDC(target);
            if (windowDc != IntPtr.Zero) ReleaseDC(window, windowDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
    static PixelKind Kind(Color color)
    {
        if (color.IsEmpty) return PixelKind.Invalid;
        if (color.R < 16 && color.G < 16 && color.B < 16) return PixelKind.Black;
        if (color.R > 165 && color.G < 110 && color.B < 110) return PixelKind.Red;
        if (color.B > 165 && color.R < 110 && color.G < 135) return PixelKind.Blue;
        return PixelKind.Other;
    }
    static bool Unoccluded(IntPtr host, Point point)
    {
        var top = WindowFromPoint(point);
        if (top == IntPtr.Zero) return false;
        if (top == host || IsChild(host, top)) return true;
        var root = GetAncestor(top, 2); var name = new StringBuilder(80); GetClassName(root, name, name.Capacity);
        return name.ToString() is "Progman" or "WorkerW";
    }
    static List<Probe> Candidates(IntPtr surface, IntPtr host, Rectangle bounds, PixelKind expected)
    {
        using var pixels = new PixelReader(surface); var probes = new List<Probe>();
        for (int y = bounds.Top + 24; y < bounds.Bottom - 70; y += 64)
            for (int x = bounds.Left + 24; x < bounds.Right - 24; x += 64)
            {
                var point = new Point(x, y);
                if (!Unoccluded(host, point) || Kind(pixels.Screen(point)) != expected) continue;
                var local = point; ScreenToClient(surface, ref local);
                probes.Add(new(point, local, Kind(pixels.Window(local)) == expected));
            }
        return probes;
    }
    static List<Probe> Filter(List<Probe> probes, IntPtr surface, IntPtr host, PixelKind expected)
    {
        using var pixels = new PixelReader(surface);
        return probes.Where(x => Unoccluded(host, x.Screen) && Kind(pixels.Screen(x.Screen)) == expected)
            .Select(x => x with { WindowDcValid = x.WindowDcValid && Kind(pixels.Window(x.Client)) == expected }).ToList();
    }
    static Probe[] Spread(List<Probe> probes)
    {
        if (probes.Count == 0) throw new InvalidOperationException("NO VISIBLE COVERAGE: no desktop region followed the controlled wallpaper colors. Other windows may cover the whole wallpaper; this is not a passed flicker check.");
        int count = Math.Min(4, probes.Count);
        return Enumerable.Range(0, count).Select(i => probes[i * probes.Count / count]).ToArray();
    }
    static void Validate(Metrics metrics, bool slow, bool requireColorChanges = true)
    {
        // Actual black pixels are a confirmed failure even when sampling is
        // slower than expected. Do not hide positive evidence as insufficient coverage.
        if (metrics.ScreenBlack + metrics.ScreenOther + metrics.WindowBlack + metrics.WindowOther != 0) throw new InvalidOperationException("Flicker detected: intermediate black/empty/original-background pixels occurred in a calibrated, unoccluded wallpaper region.");
        // Screen capture can synchronize to the 60Hz desktop compositor. A 1ms
        // timer cannot turn it into 1000 distinct physical screen captures.
        int minimumVisible = requireColorChanges ? 120 : 30;
        if (metrics.Visible < minimumVisible || metrics.Rounds / metrics.Seconds < 50) throw new InvalidOperationException($"INADEQUATE COVERAGE: fewer than {minimumVisible} visible samples or 50 capture rounds/s; desktop-compositor cadence was not verified.");
        if (slow && (metrics.PreparingScreen < 20 || metrics.PreparingWindow < 20)) throw new InvalidOperationException("INADEQUATE COVERAGE: must sample both the actual screen and a valid HWND DC during at least 20 incomplete WM_PAINT samples.");
        if (requireColorChanges && (metrics.ScreenRed < 10 || metrics.ScreenBlue < 10)) throw new InvalidOperationException("Color changes were not observed on the actual screen; cached/stale wallpaper cannot pass.");
    }
    static BackgroundWindow CreateWindow(Rectangle bounds) => new() { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = bounds };
    // Reuse the real-screen sampler for desktop-profile changes. The caller starts
    // with a calibrated solid red wallpaper; black or Windows-background pixels fail.
    internal static string MeasureDesktopSwitches(Scene surface, Form host, Rectangle bounds, Action changes)
    {
        var probes = Spread(Candidates(surface.Handle, host.Handle, bounds, PixelKind.Red));
        using var sampler = new Sampler(surface.Handle, host.Handle, probes);
        changes();
        var measured = sampler.Stop();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "virtual-desktop-flicker-measurements.txt"), measured.Describe("Desktop-profile switches"));
        Validate(measured, false);
        return measured.Describe("Desktop-profile switches");
    }
    public static void Run()
    {
        string details = Path.Combine(AppContext.BaseDirectory, "flicker-measurements.txt");
        string output = Path.Combine(AppContext.BaseDirectory, "flicker-test.txt");
        string folder = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "flicker-fixture-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(folder);
        var report = new List<string> { "Actual screen + HWND DC sampling; local 1×1 capture only; no window activation, no foreground-window movement, no DrawToBitmap/WM_PRINTCLIENT. Unavailable visibility or sampling coverage explicitly fails." };
        var failures = new List<string>();
        void Save() { File.WriteAllText(details, string.Join("\n\n", report)); File.WriteAllText(output, string.Join("\n\n", report)); }
        Save(); var bounds = Screen.PrimaryScreen!.Bounds;
        try
        {
            try
            {
                using var window = CreateWindow(bounds); using var surface = new SlowSurface(); window.Controls.Add(surface);
                Desktop.Attach(window, bounds); Pump(.15);
                var probes = Candidates(surface.Handle, window.Handle, bounds, PixelKind.Red);
                surface.SetColor(true); Pump(.08); probes = Filter(probes, surface.Handle, window.Handle, PixelKind.Blue);
                surface.SetColor(false); Pump(.08); probes = Filter(probes, surface.Handle, window.Handle, PixelKind.Red);
                var selected = Spread(probes);
                report.Add("SlowSurface calibration: red→blue→red actual screen matched at " + string.Join("; ", selected.Select(p => $"{p.Screen.X},{p.Screen.Y}; HWND DC={p.WindowDcValid}"))); Save();
                using var sampler = new Sampler(surface.Handle, window.Handle, selected, () => surface.Preparing);
                long painted = surface.NativePaintedFrames; surface.StartSlowFrames(); Pump(2.2); surface.StopSlowFrames();
                var measured = sampler.Stop(); report.Add(measured.Describe("20ms incomplete-frame stress") + $"; completed native paints={surface.NativePaintedFrames - painted}"); Save();
                Validate(measured, true);
                using var failureSampler = new Sampler(surface.Handle, window.Handle, selected, () => surface.Preparing);
                long failuresBefore = surface.FailedRenders, paintsBeforeFailure = surface.NativePaintedFrames;
                surface.FailNextPaint(); Pump(.7); var failureMeasured = failureSampler.Stop();
                report.Add(failureMeasured.Describe("TryRenderNative=false front-frame retention") + $"; false render callbacks={surface.FailedRenders - failuresBefore}; completed paints={surface.NativePaintedFrames - paintsBeforeFailure}"); Save();
                Validate(failureMeasured, false, false);
                if (surface.FailedRenders != failuresBefore + 1 || surface.NativePaintedFrames != paintsBeforeFailure) throw new InvalidOperationException("Exactly one failed render must keep the complete front frame and must not count as a completed paint.");
                window.Close(); Pump(.04);
            }
            catch (Exception ex) { report.Add("FAIL slow native surface: " + ex.Message); failures.Add("slow native surface: " + ex.Message); Save(); }
            try
            {
                string path = Path.Combine(folder, "red-blue-720p60.mp4");
                Wait(Task.Run(() => FlickerFixture.Create(path)));
                using var window = CreateWindow(bounds); using var scene = new ControlledScene { Paused = true, Fps = 0, AudioEnabled = false }; window.Controls.Add(scene);
                scene.LoadScene(new("Flicker colors 720p60", "video", path)); Wait(scene.WaitUntilReadyAsync());
                using (var source = scene.Video!.CopyFrame())
                    if (source == null || source.Size != new Size(1280, 720) || Kind(source.GetPixel(100, 100)) != PixelKind.Red) throw new InvalidOperationException("Flicker fixture must decode a real 1280×720 red first frame.");
                Desktop.Attach(window, bounds); Pump(.15); var probes = Candidates(scene.Handle, window.Handle, bounds, PixelKind.Red);
                scene.Paused = false;
                var timeout = Stopwatch.StartNew();
                while ((scene.Video!.PresentedPositionTicks / scene.Video.FrameDuration / 4) % 2 != 1)
                { if (timeout.Elapsed.TotalSeconds > 2) throw new TimeoutException("Decoded blue calibration frame did not arrive."); Pump(.004); }
                scene.Paused = true; Pump(.05); probes = Filter(probes, scene.Handle, window.Handle, PixelKind.Blue);
                var selected = Spread(probes);
                report.Add("Real H.264 video calibration: 1280×720, 60fps, bright red/blue blocks, visible probe positions=" + string.Join("; ", selected.Select(p => $"{p.Screen.X},{p.Screen.Y}; HWND DC={p.WindowDcValid}"))); Save();
                using var sampler = new Sampler(scene.Handle, window.Handle, selected); long frames = scene.NativePresentedFrames;
                scene.Paused = false; Pump(2.2); scene.Paused = true;
                var measured = sampler.Stop(); report.Add(measured.Describe("actual 60fps video") + $"; actual completed unique frames={scene.NativePresentedFrames - frames}; decoded duration={scene.Video!.DurationTicks / 10000000.0:F3}s, frame interval={scene.Video.FrameDuration / 10000.0:F3}ms"); Save();
                Validate(measured, false);
                if (scene.NativePresentedFrames - frames < 50) throw new InvalidOperationException("Real video must complete at least 50 distinct frames while screen sampling runs.");
                if (scene.ForceComposite || !scene.GeometryCoversViewport || scene.NativeBufferAllocations != 0)
                    throw new InvalidOperationException("Normal full-viewport playback must use its complete decoded-frame fast path without allocating a composite canvas.");
                report.Add("Normal-video fast path verified: complete decoded frame covers viewport; composite canvas allocations=0."); Save();
                VerifyTransforms(scene, window, bounds, report, Save);
                window.Close(); Pump(.04);
            }
            catch (Exception ex) { report.Add("FAIL real video: " + ex.Message); failures.Add("real video: " + ex.Message); Save(); }
        }
        finally
        {
            if (!string.Equals(Path.GetDirectoryName(folder), Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), StringComparison.OrdinalIgnoreCase)) throw new IOException("Flicker fixture cleanup escaped executable directory.");
            try { Directory.Delete(folder, true); } catch (Exception ex) { report.Add("Fixture cleanup: " + ex.Message); }
        }
        report.Insert(0, failures.Count == 0 ? "PASS: no intermediate black/empty frames in actual desktop and HWND DC samples." : "FAILED / NOT VERIFIED: " + string.Join("; ", failures)); Save();
        if (failures.Count != 0) throw new InvalidOperationException("Flicker test failed. Independent counts remain in " + details + ": " + string.Join("; ", failures));
    }
    static void VerifyTransforms(ControlledScene scene, Form window, Rectangle bounds, List<string> report, Action save)
    {
        // Normal playback above exercises the complete decoded-frame fast path.
        // Pixel geometry here must inspect a freshly composed native canvas,
        // rather than a scratch buffer left over from an earlier transform.
        scene.ForceComposite = true;
        using var decoded = scene.Video!.CopyFrame() ?? throw new InvalidOperationException("Missing paused source for transform pixel validation.");
        var source = Kind(decoded.GetPixel(100, 100));
        using var pixels = new PixelReader(scene.Handle);
        var field = typeof(NativeVideoSurface).GetField("paintBuffer", BindingFlags.Instance | BindingFlags.NonPublic);
        foreach (var spec in new[]
        {
            ("zoom-out", new WallpaperTransform(.7f), false, true),
            ("zoom+pan", new WallpaperTransform(1.2f, .1f, .1f), true, false),
            ("large-offset", new WallpaperTransform(1.2f, .3f, .1f), false, true),
            ("reset", new WallpaperTransform(), true, false)
        })
        {
            long before = scene.NativePaintedFrames; scene.SetTransform(spec.Item2); scene.Invalidate(); Pump(.1);
            if (scene.NativePaintedFrames <= before) throw new InvalidOperationException(spec.Item1 + " did not complete an actual native HWND paint.");
            if (scene.GeometryCoversViewport != spec.Item3) throw new InvalidOperationException(spec.Item1 + $" viewport coverage guard must be {spec.Item3}, received {scene.GeometryCoversViewport}.");
            var canvas = field?.GetValue(scene) as NativePaintBuffer ?? throw new InvalidOperationException("No actual native paint canvas is available for geometry validation.");
            var center = new Point(scene.Width / 2, scene.Height / 2);
            var edge = new Point(4, scene.Height / 2);
            var expectedEdge = spec.Item4 ? PixelKind.Black : source;
            if (Kind(pixels.Buffer(canvas.Dc, center)) != source || Kind(pixels.Buffer(canvas.Dc, edge)) != expectedEdge)
                throw new InvalidOperationException(spec.Item1 + " produced incorrect complete-canvas colors or failed to clear the moved/scaled canvas margins.");
            // A video scaled down can lie entirely behind the user's other apps.
            // Check screen/DC pixels only where this transform's video is visible.
            var visible = new List<Probe>();
            for (int iy = 1; iy <= 4; iy++) for (int ix = 1; ix <= 7; ix++)
            {
                var local = new Point(scene.Width * ix / 8, scene.Height * iy / 5);
                if (Kind(pixels.Buffer(canvas.Dc, local)) != source) continue;
                var global = local; ClientToScreen(scene.Handle, ref global);
                if (!bounds.Contains(global) || !Unoccluded(window.Handle, global) || Kind(pixels.Screen(global)) != source) continue;
                visible.Add(new(global, local, Kind(pixels.Window(local)) == source));
            }
            string scope = "completed native HWND paint + native-canvas pixel geometry only; no unoccluded transformed-video screen region found, no actual-screen flicker claim";
            if (visible.Count > 0)
            {
                using var sampler = new Sampler(scene.Handle, window.Handle, Spread(visible)); Pump(.7); var measured = sampler.Stop();
                report.Add(measured.Describe(spec.Item1 + " transformed video interior")); save();
                Validate(measured, false, false);
                scope = $"native pixel geometry plus actual-screen interior verified at {visible.Count} visible positions";
            }
            report.Add($"Transform {spec.Item1}: Scale={spec.Item2.Scale}, Offset={spec.Item2.OffsetX},{spec.Item2.OffsetY}; native paints={scene.NativePaintedFrames - before}; coverage guard={scene.GeometryCoversViewport} (expected {spec.Item3}); forced complete native canvas, center={source}, left-edge={expectedEdge}; {scope}."); save();
        }
        if (scene.NativeBufferAllocations != 1) throw new InvalidOperationException("Transform repaints must reuse exactly one native composite canvas for the fixed viewport.");
        report.Add("Transform composite canvas reused across all four cases: allocations=1."); save();
    }
    static void Wait(Task task)
    {
        var elapsed = Stopwatch.StartNew();
        while (!task.IsCompleted) { if (elapsed.Elapsed.TotalSeconds > 60) throw new TimeoutException("Flicker fixture/readiness timed out."); Pump(.01); }
        task.GetAwaiter().GetResult();
    }
    static void Pump(double seconds)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed.TotalSeconds < seconds)
        {
            if (!PeekMessage(out var message, IntPtr.Zero, 0, 0, 1)) { MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, 2, 0x04ff, 0x0004); continue; }
            if (message.Message == 0x0012) { PostQuitMessage(unchecked((int)message.WParam.ToUInt64())); throw new InvalidOperationException("GUI dispatcher quit during flicker verification."); }
            TranslateMessage(ref message); DispatchMessage(ref message);
        }
    }
    static uint ToColorRef(Color color) => (uint)(color.R | color.G << 8 | color.B << 16);
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XResolution, YResolution; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] struct NativeMessage
    {
        public IntPtr Window; public uint Message; public UIntPtr WParam; public IntPtr LParam; public uint Time; public Point Position; public uint Private;
    }
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr window, ref Point point);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref Point point);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] static extern int FillRect(IntPtr dc, ref NativeRect rectangle, IntPtr brush);
    [DllImport("gdi32.dll")] static extern IntPtr CreateSolidBrush(uint color);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr value);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr value);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool BitBlt(IntPtr target, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr completion, IntPtr argument, bool resume);
    [DllImport("kernel32.dll")] static extern bool CancelWaitableTimer(IntPtr timer);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds, uint mask, uint flags);
}

static class FlickerFixture
{
    static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71"), Bitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"),
        Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"), PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSinkWriterFromURL(string path, IntPtr stream, IntPtr attributes, out IntPtr writer);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(int size, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AddStream(IntPtr self, IntPtr type, out uint index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetInputType(IntPtr self, uint index, IntPtr type, IntPtr attributes);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int WriteSample(IntPtr self, uint index, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AddBuffer(IntPtr self, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetLength(IntPtr self, uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetTime(IntPtr self, long value);
    static void Format(IntPtr type, Guid subtype)
    {
        MfNative.SetGuid(type, MfNative.MajorType, MfNative.VideoType); MfNative.SetGuid(type, MfNative.Subtype, subtype);
        MfNative.SetUInt32(type, Interlace, 2); MfNative.SetUInt64(type, MfNative.FrameSize, 1280UL << 32 | 720);
        MfNative.SetUInt64(type, MfNative.FrameRate, 60UL << 32 | 1); MfNative.SetUInt64(type, PixelAspect, 1UL << 32 | 1);
    }
    public static void Create(string path)
    {
        bool initialized = false, started = false; IntPtr writer = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero;
        try
        {
            MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, 0)); initialized = true;
            MfNative.Check(MfNative.MFStartup(0x00020070, 0)); started = true;
            MfNative.Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out writer));
            MfNative.Check(MfNative.MFCreateMediaType(out output)); Format(output, H264); MfNative.SetUInt32(output, Bitrate, 8000000);
            MfNative.Check(MfNative.Method<AddStream>(writer, 3)(writer, output, out uint stream));
            MfNative.Check(MfNative.MFCreateMediaType(out input)); Format(input, MfNative.Rgb32);
            MfNative.Check(MfNative.Method<SetInputType>(writer, 4)(writer, stream, input, IntPtr.Zero));
            MfNative.Check(MfNative.Method<MfNative.SimpleCall>(writer, 5)(writer));
            byte[][] colors = [new byte[1280 * 720 * 4], new byte[1280 * 720 * 4]];
            for (int i = 0; i < colors[0].Length; i += 4)
            {
                colors[0][i] = 48; colors[0][i + 1] = 48; colors[0][i + 2] = 224;
                colors[1][i] = 224; colors[1][i + 1] = 80; colors[1][i + 2] = 48;
            }
            for (int frame = 0; frame < 180; frame++)
            {
                var pixels = colors[(frame / 4) % 2]; IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero; bool locked = false;
                try
                {
                    MfNative.Check(MFCreateSample(out sample)); MfNative.Check(MFCreateMemoryBuffer(pixels.Length, out buffer));
                    MfNative.Check(MfNative.Method<MfNative.BufferLock>(buffer, 3)(buffer, out IntPtr destination, out _, out _)); locked = true;
                    Marshal.Copy(pixels, 0, destination, pixels.Length); MfNative.Check(MfNative.Method<MfNative.SimpleCall>(buffer, 4)(buffer)); locked = false;
                    MfNative.Check(MfNative.Method<SetLength>(buffer, 6)(buffer, (uint)pixels.Length)); MfNative.Check(MfNative.Method<AddBuffer>(sample, 42)(sample, buffer));
                    long timestamp = frame * 10000000L / 60, duration = (frame + 1) * 10000000L / 60 - timestamp;
                    MfNative.Check(MfNative.Method<SetTime>(sample, 36)(sample, timestamp)); MfNative.Check(MfNative.Method<SetTime>(sample, 38)(sample, duration));
                    MfNative.Check(MfNative.Method<WriteSample>(writer, 6)(writer, stream, sample));
                }
                finally { if (locked) MfNative.Method<MfNative.SimpleCall>(buffer, 4)(buffer); MfNative.Release(ref buffer); MfNative.Release(ref sample); }
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
