using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace LumaWall;

// Measures real background WM_PAINT completion. Never uses DrawToBitmap as the
// live rendering path and never opens or activates a foreground application.
static class RenderedPlaybackTest
{
    sealed record Clip(string Label, string Path, int Width, int Height, double Fps, double Duration, int Limit = 0, bool WithPreview = false,
        WallpaperLayoutMode Layout = WallpaperLayoutMode.Fill, WallpaperTransform? Transform = null);
    [StructLayout(LayoutKind.Sequential)] struct NativeMessage
    {
        public IntPtr Window; public uint Message; public UIntPtr WParam; public IntPtr LParam;
        public uint Time; public Point Position; public uint Private;
    }
    [DllImport("user32.dll")] static extern bool PeekMessage(out NativeMessage message, IntPtr window, uint minimum, uint maximum, uint remove);
    [DllImport("user32.dll")] static extern bool TranslateMessage(ref NativeMessage message);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref NativeMessage message);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int exitCode);
    [DllImport("user32.dll")] static extern uint MsgWaitForMultipleObjectsEx(uint count, IntPtr handles, uint milliseconds, uint wakeMask, uint flags);
    sealed class BackgroundWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; }
        }
    }
    sealed class MeasuredScene : Scene
    {
        readonly List<double> paintTimes = [], completedAt = [], distinctAt = [];
        readonly HashSet<long> stableLegacyFrames = [];
        readonly MemberInfo? nativePaints, nativeDistinct;
        long measurementStart, legacyPaintSerial, legacySource;
        bool measuring, legacyStable;
        public bool UsesNativeCounters => nativePaints != null && nativeDistinct != null;
        public int CompletedPaints => completedAt.Count;
        public int UniqueCompletions => distinctAt.Count;
        public int UnknownLegacySources { get; private set; }
        public MeasuredScene()
        {
            nativePaints = FindMember("NativePaintedFrames"); nativeDistinct = FindMember("NativePresentedFrames");
        }
        MemberInfo? FindMember(string name)
        {
            for (Type? type = GetType(); type != null; type = type.BaseType)
            {
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                if (type.GetProperty(name, flags) is { } property) return property;
                if (type.GetField(name, flags) is { } field) return field;
            }
            return null;
        }
        long Read(MemberInfo? member) => member switch
        {
            PropertyInfo property => Convert.ToInt64(property.GetValue(this)),
            FieldInfo field => Convert.ToInt64(field.GetValue(this)), _ => 0
        };
        public void StartMeasurement()
        {
            paintTimes.Clear(); completedAt.Clear(); distinctAt.Clear(); stableLegacyFrames.Clear(); UnknownLegacySources = 0;
            measurementStart = Stopwatch.GetTimestamp(); measuring = true;
        }
        public void StopMeasurement() => measuring = false;
        protected override void OnPaint(PaintEventArgs e)
        {
            long sourceBefore = Video?.PresentedPositionTicks ?? -1;
            base.OnPaint(e);
            // Only stable source identity is counted as a known legacy frame.
            // A producer update during painting cannot be called the drawn frame.
            legacySource = sourceBefore;
            legacyStable = sourceBefore >= 0 && sourceBefore == (Video?.PresentedPositionTicks ?? -2);
            legacyPaintSerial++;
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg != 0x000F) { base.WndProc(ref message); return; }
            long start = Stopwatch.GetTimestamp(), beforeLegacy = legacyPaintSerial;
            long beforeNative = Read(nativePaints), beforeDistinct = Read(nativeDistinct);
            base.WndProc(ref message);
            long end = Stopwatch.GetTimestamp();
            if (!measuring) return;
            bool painted = UsesNativeCounters ? Read(nativePaints) > beforeNative : legacyPaintSerial > beforeLegacy;
            if (!painted) return;
            paintTimes.Add(Stopwatch.GetElapsedTime(start, end).TotalMilliseconds);
            double complete = Stopwatch.GetElapsedTime(measurementStart, end).TotalMilliseconds; completedAt.Add(complete);
            if (UsesNativeCounters)
            {
                if (Read(nativeDistinct) > beforeDistinct) distinctAt.Add(complete);
            }
            else if (legacyStable)
            {
                if (stableLegacyFrames.Add(legacySource)) distinctAt.Add(complete);
            }
            else UnknownLegacySources++;
        }
        static double Percentile(IEnumerable<double> values, double percentile)
        {
            var sorted = values.Order().ToArray();
            return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)];
        }
        public (string Report, bool Smooth) Results(double wall, double expectedFps)
        {
            var intervals = new List<double>();
            for (int i = 1; i < completedAt.Count; i++) intervals.Add(completedAt[i] - completedAt[i - 1]);
            var frameIntervals = new List<double>();
            for (int i = 1; i < distinctAt.Count; i++) frameIntervals.Add(distinctAt[i] - distinctAt[i - 1]);
            double first = completedAt.Count > 0 ? completedAt[0] : wall * 1000;
            double tail = completedAt.Count > 0 ? wall * 1000 - completedAt[^1] : wall * 1000;
            double worst = Math.Max(Math.Max(first, tail), intervals.Count > 0 ? intervals.Max() : 0);
            double firstFrame = distinctAt.Count > 0 ? distinctAt[0] : wall * 1000;
            double frameTail = distinctAt.Count > 0 ? wall * 1000 - distinctAt[^1] : wall * 1000;
            double worstFrame = Math.Max(Math.Max(firstFrame, frameTail), frameIntervals.Count > 0 ? frameIntervals.Max() : 0);
            double uniqueFps = UniqueCompletions / wall, paintFps = CompletedPaints / wall;
            bool smooth = UsesNativeCounters && uniqueFps >= expectedFps * .78 && worstFrame < 180 && Percentile(frameIntervals, .95) < 2500 / expectedFps;
            string identity = UsesNativeCounters ? "native completed-frame counters" : "legacy stable-source lower bound; changing source identities excluded";
            return ($"WM_PAINT completed={CompletedPaints} ({paintFps:F2}fps); unique completed frames={UniqueCompletions} ({uniqueFps:F2}fps); identity={identity}; unknownLegacyPaintSources={UnknownLegacySources}; complete paint median={Percentile(paintTimes, .5):F2}ms/P95={Percentile(paintTimes, .95):F2}ms/max={paintTimes.DefaultIfEmpty().Max():F2}ms; completed interval median={Percentile(intervals, .5):F2}ms/P95={Percentile(intervals, .95):F2}ms; maximum paint stall including endpoints={worst:F2}ms; distinct frame interval median={Percentile(frameIntervals, .5):F2}ms/P95={Percentile(frameIntervals, .95):F2}ms; maximum distinct frame stall including endpoints={worstFrame:F2}ms; smoothness={(smooth ? "smooth" : "below target or legacy identity incomplete")}", smooth);
        }
    }
    static void Wait(Task task, double timeout = 90)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed.TotalSeconds > timeout) throw new TimeoutException("Rendered playback fixture/readiness timed out.");
            Application.DoEvents(); Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
    }
    static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    static void Pump(double seconds, Action? sample = null)
    {
        var watch = Stopwatch.StartNew();
        // Dispatch one message at a time. DoEvents may never return when a slow
        // renderer continuously receives more frame messages than it completes.
        while (watch.Elapsed.TotalSeconds < seconds)
        {
            sample?.Invoke();
            if (!PeekMessage(out var message, IntPtr.Zero, 0, 0, 1))
            {
                uint remaining = (uint)Math.Clamp(Math.Ceiling((seconds - watch.Elapsed.TotalSeconds) * 1000), 1, 100);
                // Match GetMessage's immediate wake on PostMessage/WM_PAINT.
                // Sleep(1) can last 15.6ms and artificially cap rendered FPS.
                MsgWaitForMultipleObjectsEx(0, IntPtr.Zero, remaining, 0x04FF, 0x0004);
                continue;
            }
            if (message.Message == 0x0012) { PostQuitMessage(unchecked((int)message.WParam.ToUInt64())); break; }
            TranslateMessage(ref message); DispatchMessage(ref message);
        }
    }
    static Clip Metadata(string path, string label, int limit = 0)
    {
        return Wait(Task.Run(() =>
        {
            using var decoder = new VideoDecoder(path);
            using var first = decoder.ReadFrame(CancellationToken.None) ?? throw new InvalidDataException("Video has no real decoded frame.");
            return new Clip(label, path, first.Bitmap.Width, first.Bitmap.Height, 10000000.0 / decoder.FrameDuration, decoder.DurationTicks / 10000000.0, limit);
        }));
    }
    public static void Run() => Run(false, false);
    public static void Run(bool includeUserVideo) => Run(includeUserVideo, false);
    public static void Run(bool includeUserVideo, bool requireSmoothness)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "rendered-playback-test.txt");
        string details = Path.Combine(AppContext.BaseDirectory, "rendered-playback-measurements.txt");
        void SaveReport(string text) { File.WriteAllText(output, text); File.WriteAllText(details, text); }
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "rendered-fixtures-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var clips = new List<Clip>(); var report = new List<string>(); var failures = new List<string>();
        foreach (var spec in new[] { (1920, 1080, 30), (1280, 720, 60), (1920, 1080, 60) })
        {
            string path = Path.Combine(root, $"{spec.Item1}x{spec.Item2}-{spec.Item3}fps.mp4");
            Wait(Task.Run(() => PerformanceFixture.Create(path, spec.Item1, spec.Item2, spec.Item3, 4)));
            clips.Add(Metadata(path, $"fixture {spec.Item1}×{spec.Item2}/{spec.Item3}fps"));
        }
        clips.Add(clips[0] with { Label = clips[0].Label + " + simultaneous 400×225 preview (single shared decoder)", WithPreview = true });
        if (includeUserVideo)
        {
            string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaWall", "settings.json");
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath)) ?? new();
            report.Add($"Read-only user settings: configured FPS limit={settings.Fps}; imported video metadata below. User settings/media are never modified.");
            foreach (var item in settings.Library.Where(x => x.Kind == "video" && x.Path != null && File.Exists(x.Path)).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var clip = Metadata(item.Path!, "user video " + item.Name + " (" + Path.GetFileName(item.Path) + ")", settings.Fps);
                    clips.Add(clip);
                    clips.Add(clip with { Label = clip.Label + " + simultaneous 400×225 preview (single shared decoder)", WithPreview = true });
                    if (settings.Monitors.TryGetValue(Screen.PrimaryScreen!.DeviceName, out var profile))
                        clips.Add(clip with { Label = clip.Label + " + saved layout/transform", Layout = profile.LayoutModes.GetValueOrDefault(item.Key), Transform = profile.Transforms.GetValueOrDefault(item.Key) });
                }
                catch (Exception ex) { report.Add($"Metadata failure: {item.Name}: {ex.Message}"); failures.Add("Cannot decode user video metadata: " + item.Name); }
            }
        }
        SaveReport("MEASURING: actual native background window WM_PAINT, not decoder publication.\n" + string.Join("\n", report));
        foreach (var clip in clips)
        {
            try
            {
                var measured = Measure(clip);
                report.Add(measured.Report); if (requireSmoothness && !measured.Smooth) failures.Add(clip.Label);
            }
            catch (Exception ex) { report.Add($"ERROR {clip.Label}: {ex}"); failures.Add(clip.Label + ": " + ex.Message); }
            SaveReport("MEASURED: full WM_PAINT completion includes base handler and double-buffer/native submission; no screen occlusion claim.\n" + string.Join("\n\n", report));
        }
        if (!string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture cleanup escaped output directory.");
        Directory.Delete(root, true);
        if (failures.Count > 0) throw new InvalidOperationException("Rendered playback failures: " + string.Join("; ", failures) + ". Detailed measurements: " + details);
    }
    static (string Report, bool Smooth) Measure(Clip clip)
    {
        var bounds = Screen.PrimaryScreen!.Bounds;
        using var window = new BackgroundWindow { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = bounds };
        using var scene = new MeasuredScene { Paused = true, Fps = clip.Limit, AudioEnabled = false };
        window.Controls.Add(scene); scene.LoadScene(new(clip.Label, "video", clip.Path)); Wait(scene.WaitUntilReadyAsync());
        scene.LayoutMode = clip.Layout; scene.SetTransform(clip.Transform ?? new());
        MeasuredScene? preview = null;
        Size previewDecodedSize = Size.Empty;
        if (clip.WithPreview)
        {
            preview = new MeasuredScene { Dock = DockStyle.None, Bounds = new(20, 20, 400, 225), Paused = true, Fps = clip.Limit, AudioEnabled = false };
            window.Controls.Add(preview); preview.BringToFront(); preview.LoadVideoPreview(new(clip.Label, "video", clip.Path), scene.Video!); Wait(preview.WaitUntilReadyAsync());
            if (!ReferenceEquals(preview.Video, scene.Video) || !preview.IsSharedVideo) throw new InvalidOperationException("Same-video UI preview must share exactly one desktop VideoPlayback.");
            using var decodedPreview = preview.Video!.CopyFrame();
            using var decodedDesktop = scene.Video!.CopyFrame();
            previewDecodedSize = decodedPreview?.Size ?? Size.Empty;
            if (previewDecodedSize.Width <= 0 || previewDecodedSize != decodedDesktop?.Size) throw new InvalidOperationException("Shared UI preview must borrow the same full-resolution decoded source frame.");
            preview.Paused = false;
            if (!scene.Video.Paused) throw new InvalidOperationException("Resuming a borrowed preview must not resume its paused desktop owner.");
            preview.Paused = true;
        }
        Desktop.Attach(window, bounds);
        scene.Paused = false;
        if (preview != null)
        {
            preview.Paused = true;
            if (scene.Video!.Paused) throw new InvalidOperationException("Pausing a borrowed preview must not pause its playing desktop owner.");
            preview.Paused = false;
        }
        Pump(.4); scene.StartMeasurement(); preview?.StartMeasurement();
        using var process = Process.GetCurrentProcess(); TimeSpan cpu = process.TotalProcessorTime;
        long sourceFrames = scene.Video!.FrameCount, sourceTicks = scene.Video.PresentedPositionTicks, scenePaints = scene.PaintedFrames;
        long previewSourceFrames = preview?.Video?.FrameCount ?? 0;
        long clockStart = scene.Video.Clock.PositionTicks;
        double maximumLag = 0;
        var wall = Stopwatch.StartNew();
        Pump(3, () => maximumLag = Math.Max(maximumLag, Math.Max(0, scene.Video.Clock.PositionTicks - scene.Video.PresentedPositionTicks) / 10000.0));
        double seconds = wall.Elapsed.TotalSeconds; scene.StopMeasurement(); preview?.StopMeasurement();
        double clockSeconds = (scene.Video.Clock.PositionTicks - clockStart) / 10000000.0;
        long decodedDelta = scene.Video.FrameCount - sourceFrames;
        double publishedSeconds = (scene.Video.PresentedPositionTicks - sourceTicks) / 10000000.0;
        double expectedFps = Math.Min(clip.Fps, FrameRateOptions.Limit(clip.Limit));
        var result = scene.Results(seconds, expectedFps);
        string title = $"{clip.Label}: real decoded {clip.Width}×{clip.Height}; media FPS={clip.Fps:F3}; duration={clip.Duration:F3}s; target FPS={expectedFps:F2}; native desktop={bounds.Width}×{bounds.Height}; interval={seconds:F3}s";
        bool realtime = Math.Abs(clockSeconds - seconds) < .10 && Math.Abs(publishedSeconds - seconds) < .20 && maximumLag < 250;
        string producer = $"Producer published={decodedDelta} ({decodedDelta / seconds:F2}fps); published media advanced={publishedSeconds:F3}s; master clock advanced={clockSeconds:F3}s; maximum published-frame lag={maximumLag:F2}ms; realtime={realtime}; Scene.OnPaint delta={scene.PaintedFrames - scenePaints}; aggregate CPU={(process.TotalProcessorTime - cpu).TotalSeconds / seconds * 100:F1}% of one core; decoder dropped={scene.Video.DroppedFrameCount}; D3D11 video path={scene.Video.Accelerated}";
        if (scene.CompletedPaints == 0) throw new InvalidOperationException(title + "; zero actual completed WM_PAINTs; " + producer + "; " + result.Report);
        string previewReport = ""; bool previewSmooth = true;
        if (preview != null)
        {
            var measuredPreview = preview.Results(seconds, expectedFps); previewSmooth = measuredPreview.Smooth;
            previewReport = $"\nLive preview: single shared decoder, borrowed frame {previewDecodedSize.Width}×{previewDecodedSize.Height}; viewport=400×225; shared producer published={preview.Video!.FrameCount - previewSourceFrames}; {measuredPreview.Report}";
            if (preview.CompletedPaints == 0) throw new InvalidOperationException(title + "; zero actual preview WM_PAINTs; " + previewReport);
        }
        scene.Paused = true; if (preview != null) preview.Paused = true;
        if (preview != null)
        {
            preview.ClearScene();
            using var survivingOwnerFrame = scene.Video!.CopyFrame();
            if (survivingOwnerFrame == null) throw new InvalidOperationException("Releasing the borrowed preview must leave its desktop owner frame alive.");
        }
        window.Close(); Pump(.05);
        return (title + "\n" + result.Report + "\n" + producer + previewReport, result.Smooth && previewSmooth && realtime);
    }

    public static void RunHighRefresh()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "high-refresh-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var report = new List<string>(); var failures = new List<string>();
        string output = Path.Combine(AppContext.BaseDirectory, "high-refresh-test.txt");
        try
        {
            foreach (var spec in new[] { (1280, 720, 120), (1920, 1080, 120), (320, 180, 240), (3840, 2160, 60) })
            {
                string path = Path.Combine(root, $"{spec.Item1}x{spec.Item2}-{spec.Item3}" + (spec.Item3 == 240 ? ".avi" : ".mp4"));
                try { Wait(Task.Run(() => { if (spec.Item3 == 240) HighRefreshFixture.CreateAvi(path, spec.Item1, spec.Item2, spec.Item3, 2); else PerformanceFixture.Create(path, spec.Item1, spec.Item2, spec.Item3, 4, true); }), 150); }
                catch (Exception ex) { throw new InvalidOperationException($"Native fixture encoding failed for {spec.Item1}x{spec.Item2}/{spec.Item3}fps.", ex); }
                var clip = Metadata(path, $"high refresh {spec.Item1}×{spec.Item2}/{spec.Item3}fps");
                if (Math.Abs(clip.Fps - spec.Item3) > 1) throw new InvalidDataException("Fixture must contain the requested native high frame rate.");
                foreach (var configured in spec.Item3 == 120 && spec.Item1 == 1280 ? new[] { clip, clip with { Label = clip.Label + " capped to60", Limit = 60 } } : new[] { clip })
                {
                    var measured = Measure(configured); report.Add(measured.Report);
                    if (!measured.Smooth) failures.Add(configured.Label);
                    string measurements = "MEASURING: real high-fps/4K frames, native background HWND paints and 1x time.\n" + string.Join("\n\n", report);
                    File.WriteAllText(output, measurements);
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "high-refresh-measurements.txt"), measurements);
                }
            }
            if (failures.Count > 0) throw new InvalidOperationException("High refresh/realtime checks below target: " + string.Join("; ", failures));
            File.WriteAllText(output, "PASS: real 120/240fps and 4K60 native files; actual completed distinct paints; 60fps limit preserves120fps media speed; 1x master/published time and bounded frame lag; full source resolution. No claim about physical monitor refresh or occluded screen pixels.\n\n" + string.Join("\n\n", report));
        }
        finally { TestFiles.RemoveFixture(root); }
    }
}
