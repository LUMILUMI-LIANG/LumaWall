using System.Drawing.Drawing2D;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace LumaWall;

record Wallpaper(string Name, string Kind, string? Path = null)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => Path == null ? "builtin:" + Kind : "file:" + System.IO.Path.GetFullPath(Path);
}

record WallpaperTransform(float Scale = 1f, float OffsetX = 0f, float OffsetY = 0f)
{
    public const float MinimumScale = .25f, MaximumScale = 3f;

    public WallpaperTransform Normalize() => new(
        float.IsFinite(Scale) ? Math.Clamp(Scale, MinimumScale, MaximumScale) : 1f,
        float.IsFinite(OffsetX) ? Math.Clamp(OffsetX, -1f, 1f) : 0f,
        float.IsFinite(OffsetY) ? Math.Clamp(OffsetY, -1f, 1f) : 0f);

    public WallpaperTransform Pan(float horizontal, float vertical)
    {
        var current = Normalize();
        return (current with { OffsetX = current.OffsetX + horizontal, OffsetY = current.OffsetY + vertical }).Normalize();
    }

    // Anchor coordinates are fractions of the viewport, measured from its center.
    public WallpaperTransform ZoomAt(float requestedScale, PointF anchor)
    {
        var current = Normalize();
        float scale = (current with { Scale = requestedScale }).Normalize().Scale;
        float ratio = scale / current.Scale;
        return new WallpaperTransform(scale,
            anchor.X - (anchor.X - current.OffsetX) * ratio,
            anchor.Y - (anchor.Y - current.OffsetY) * ratio).Normalize();
    }

    internal Matrix CreateMatrix(Size viewport)
    {
        var current = Normalize();
        return new Matrix(current.Scale, 0, 0, current.Scale,
            viewport.Width * ((1 - current.Scale) / 2 + current.OffsetX),
            viewport.Height * ((1 - current.Scale) / 2 + current.OffsetY));
    }
}
partial class Settings
{
    public List<Wallpaper> Library { get; set; } = [];
    public List<string> DeletedBuiltIns { get; set; } = [];
    public Dictionary<string, WallpaperTransform> Transforms { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int Fps { get; set; } = 0;
    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public PlaylistOptions Playlist { get; set; } = new();
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Velopack.VelopackApp.Build().Run();
        DarkTheme.Initialize();
        ApplicationConfiguration.Initialize();
        (string Flag, Action Run)[] tests = [
            ("--tray-lifecycle-test", TrayLifecycleTest.Run),
            ("--file-location-test", LibraryFileLocationTest.Run),
            ("--browser-gallery-test", BrowserGalleryTest.Run),
            ("--browser-gallery-live-test", BrowserGalleryTest.Live),
            ("--browser-gallery-download-live-test", BrowserGalleryTest.DownloadLive),
            ("--online-browser-test", OnlineBrowserTest.Run),
            ("--online-browser-live-test", OnlineBrowserTest.Live),
            ("--chinese-search-test", ChineseSearchTest.Run),
            ("--chinese-search-live-test", ChineseSearchTest.Live),
            ("--chinese-name-live-test", ChineseSearchTest.NameLive),
            ("--virtual-desktop-flicker-test", VirtualDesktopFlickerTest.Run),
            ("--iteration-v013-test", IterationV013Test.Run),
            ("--iteration-v013-live-test", IterationV013Test.Live),
            ("--ui-size-preview", UiSizePreview.Run),
            ("--online-library-test", OnlineLibraryTest.Run),
            ("--online-live-test", OnlineLiveTest.Run),
            ("--nv12-test", Nv12ConversionTest.Run),
            ("--frame-timing-test", FrameTimingTest.Run),
            ("--high-refresh-test", HighRefreshTest.Run),
            ("--iteration-test", IterationTest.Run),
            ("--layout-mode-test", LayoutModeTest.Run),
            ("--library-cache-test", LibraryCacheTest.Run),
            ("--lifecycle-test", LifecycleTest.Run),
            ("--rename-test", RenameTest.Run),
            ("--flicker-test", FlickerTest.Run),
            ("--preview-performance-test", PreviewPerformanceTest.Run),
            ("--rendered-playback-test", () => RenderedPlaybackTest.Run()),
            ("--rendered-user-test", () => RenderedPlaybackTest.Run(true)),
            ("--rendered-verify-test", () => RenderedPlaybackTest.Run(true, true)),
            ("--timing-stress-test", TimingStressTest.Run),
            ("--audio-test", AudioTest.Run), ("--performance-test", PerformanceTest.Run),
            ("--video-visible-test", () => VisibleDesktopTest.Run(true)),
            ("--playlist-test", PlaylistTest.Run), ("--media-import-test", MediaImportTest.Run),
            ("--playback-test", PlaybackIntegrationTest.Run), ("--video-test", VideoTest.Run),
            ("--media-recovery-audit", MediaLibraryTest.Audit), ("--batch-library-test", BatchLibraryTest.Run), ("--media-library-test", MediaLibraryTest.Run), ("--contrast-test", ContrastTest.Run), ("--library-view-test", LibraryViewTest.Run), ("--ui-test", UiIntegrationTest.Run), ("--transform-test", TransformTest.Run),
            ("--visible-test", () => VisibleDesktopTest.Run()), ("--delete-test", DeletionTest.Run),
            ("--self-test", () => SelfTest.Run(args.Contains("--desktop-test"))) ];
        foreach (var test in tests)
            if (args.Contains(test.Flag)) { Environment.Exit(RunTest(test.Flag, test.Run)); return; }
        using var instance = new SingleInstanceGuard();
        if (!instance.IsPrimary) { instance.NotifyExisting(); return; }
        if (LegacyInstanceRunning())
        {
            MessageBox.Show("旧版 Luma Wall 仍在运行，请从旧版托盘菜单退出后再启动新版。", "Luma Wall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        using var window = new MainWindow();
        instance.ActivationRequested += () =>
        {
            try { if (!window.IsDisposed && window.IsHandleCreated) window.BeginInvoke((Action)(() => window.PresentLibrary())); } catch (InvalidOperationException) { }
        };
        if (args.Contains("--startup")) window.Shown += (_, _) => window.BeginInvoke((Action)(() => window.Hide()));
        Application.Run(window);
    }
    static bool LegacyInstanceRunning()
    {
        foreach (var process in System.Diagnostics.Process.GetProcessesByName("LumaWall"))
            using (process)
                try
                {
                    if (process.Id == Environment.ProcessId || process.SessionId != System.Diagnostics.Process.GetCurrentProcess().SessionId) continue;
                    string? path = process.MainModule?.FileName;
                    if (path != null && Version.TryParse(System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion, out var version) && version < new Version(0, 10)) return true;
                }
                catch { }
        return false;
    }
    static int RunTest(string flag, Action test)
    {
        // Async UI tests need the same persistent dispatcher as the real app.
        // A standalone DoEvents loop can dispose its temporary marshaling context.
        int exitCode = 0;
        using var host = new Form { ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000), Size = new(1, 1) };
        host.Load += (_, _) => host.BeginInvoke((Action)(() =>
        {
            try { test(); }
            catch (Exception ex)
            {
                exitCode = 1;
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, flag.TrimStart('-') + ".txt"), ex + "\n" + Desktop.LastAttachment);
            }
            finally { host.Close(); }
        }));
        Application.Run(host); return exitCode;
    }
}

class Scene : NativeVideoSurface
{
    internal int PaintedFrames { get; private set; }
    readonly System.Windows.Forms.Timer timer = new();
    Wallpaper wallpaper = new("极光之境", "aurora");
    Image? media;
    VideoPlayback? video;
    bool ownsVideo = true;
    Exception? reportedVideoError, reportedAudioError;
    long observedVideoFrames = -1;
    int requestedFrameInterval = 33;
    internal double LastRenderMilliseconds { get; private set; }
    double time;
    bool empty;
    bool editingEnabled, dragging;
    Point dragStart;
    WallpaperTransform transform = new(), dragOrigin = new();
    public WallpaperTransform Transform => transform;
    WallpaperLayoutMode layoutMode;
    public WallpaperLayoutMode LayoutMode
    {
        get => layoutMode;
        set
        {
            var normalized = WallpaperLayout.Normalize(value);
            if (layoutMode == normalized) return;
            layoutMode = normalized; Invalidate(); ScheduleNativePaint();
        }
    }
    public event Action<WallpaperTransform>? TransformChanged;
    Size layoutReferenceSize;
    public Size LayoutReferenceSize
    {
        get => layoutReferenceSize;
        set
        {
            var normalized = value.Width > 0 && value.Height > 0 ? value : Size.Empty;
            if (layoutReferenceSize == normalized) return;
            layoutReferenceSize = normalized; Invalidate(); ScheduleNativePaint();
        }
    }
    public bool EditingEnabled
    {
        get => editingEnabled;
        set
        {
            editingEnabled = value;
            TabStop = value;
            if (!value) { dragging = false; Capture = false; }
            Cursor = value && !empty ? Cursors.SizeAll : Cursors.Default;
        }
    }
    volatile bool paused;
    volatile bool renderingSuspended;
    public int VideoMaximumWidth { get; set; }
    public bool RenderingSuspended
    {
        get => renderingSuspended;
        set
        {
            renderingSuspended = value;
            if (video != null)
            {
                if (ownsVideo) video.Paused = paused || value;
                EnableNativeRendering(!value);
                if (!value) Invalidate();
            }
        }
    }
    public bool Paused
    {
        get => paused;
        set
        {
            paused = value;
            if (video != null && ownsVideo) video.Paused = value || RenderingSuspended;
            if (!value && !RenderingSuspended) ScheduleNativePaint();
        }
    }
    bool audioEnabled;
    float volume = 1;
    public bool AudioEnabled
    {
        get => audioEnabled;
        set { audioEnabled = !IsSharedVideo && value; if (video != null && ownsVideo) video.AudioEnabled = audioEnabled; }
    }
    public float Volume { get => volume; set { volume = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1; if (video != null && ownsVideo) video.Volume = volume; } }
    public bool Ready => !empty && (video == null || video.Ready.IsCompletedSuccessfully);
    public Exception? Error => video?.Error;
    public event Action<Exception>? PlaybackFailed;
    public event Action<Exception>? AudioFailed;
    internal VideoPlayback? Video => video;
    internal bool IsSharedVideo => video != null && !ownsVideo;
    public Task WaitUntilReadyAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (empty) throw new InvalidOperationException("还没有加载壁纸。");
        return video?.Ready.WaitAsync(TimeSpan.FromSeconds(15), token) ?? Task.CompletedTask;
    }
    public int Fps
    {
        // Video frames use the independent native scheduler. Keep the GDI
        // animation/error timer bounded so idle previews do not consume240fps.
        set { NativeFpsLimit = value; requestedFrameInterval = Math.Max(16, (int)Math.Ceiling(1000.0 / FrameRateOptions.Limit(value))); AdaptFrameInterval(); }
    }
    protected override bool NativeRenderingAllowed => video != null && !empty && !RenderingSuspended;
    protected override bool NativeFrameCoversViewport => WallpaperLayout.CoversViewport(ClientSize, LayoutMode, Transform);
    protected override long NativeFrameId => video?.PresentedPositionTicks ?? -1;
    protected override bool TryRenderNative(IntPtr dc, Size viewport, Rectangle paintClip)
    {
        if (video == null) return false;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        bool drawn = video.DrawHdc(dc, viewport, Transform, paintClip, LayoutMode, LayoutReferenceSize, out long frameId);
        if (drawn) { PaintedFrames++; RecordNativeFrame(frameId); }
        AccountRenderTime(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return drawn;
    }
    void VideoFrameChanged()
    {
        if (!RenderingSuspended && (ownsVideo || !Paused)) ScheduleNativePaint();
    }
    void ReleaseVideo()
    {
        EnableNativeRendering(false);
        var previous = video;
        bool release = ownsVideo;
        video = null; ownsVideo = true;
        if (previous != null) { previous.FramePublished -= VideoFrameChanged; if (release) previous.Dispose(); }
    }
    void AdaptFrameInterval() => timer.Interval = Math.Max(requestedFrameInterval, (int)Math.Ceiling(LastRenderMilliseconds) + 2);
    protected void AccountRenderTime(double milliseconds)
    {
        LastRenderMilliseconds = milliseconds; AdaptFrameInterval();
    }
    public Scene()
    {
        DoubleBuffered = true; Dock = DockStyle.Fill;
        SetStyle(ControlStyles.Selectable, true); TabStop = false;
        timer.Interval = 33;
        timer.Tick += (_, _) =>
        {
            if (video != null)
            {
                long frames = video.FrameCount;
                if (frames != observedVideoFrames) observedVideoFrames = frames;
            }
            else if (!Paused && (media == null || ImageAnimator.CanAnimate(media))) { time += timer.Interval / 1000.0; Invalidate(); }
            if (video?.Error is { } error && reportedVideoError != error) { reportedVideoError = error; PlaybackFailed?.Invoke(error); }
            if (AudioEnabled && video?.AudioError is { } audioError && reportedAudioError != audioError) { reportedAudioError = audioError; AudioFailed?.Invoke(audioError); }
        };
        timer.Start();
    }
    public void LoadScene(Wallpaper item)
    {
        Image? next = null;
        bool isVideo = item.Kind == "video";
        if (isVideo && item.Path == null) throw new InvalidDataException("视频壁纸缺少文件路径。");
        if (item.Path != null && !isVideo) { using var source = Image.FromFile(item.Path); next = (Image)source.Clone(); }
        ReleaseVideo(); reportedVideoError = reportedAudioError = null; observedVideoFrames = -1;
        if (media != null) { ImageAnimator.StopAnimate(media, FrameChanged); media.Dispose(); }
        media = next; wallpaper = item; time = 0; empty = false; transform = new();
        if (isVideo)
        {
            video = new VideoPlayback(item.Path!, Paused || RenderingSuspended, VideoMaximumWidth) { AudioEnabled = AudioEnabled, Volume = Volume };
            video.FramePublished += VideoFrameChanged;
            EnableNativeRendering(!RenderingSuspended);
        }
        dragging = false; Capture = false; Cursor = EditingEnabled ? Cursors.SizeAll : Cursors.Default; timer.Start();
        if (media != null && ImageAnimator.CanAnimate(media)) ImageAnimator.Animate(media, FrameChanged);
        if (media != null && !ImageAnimator.CanAnimate(media)) timer.Stop();
        Invalidate();
    }
    // Move a ready media source into the existing HWND. Its last complete pixels remain
    // visible until Update paints the incoming frame; no background window is recreated.
    // The staging scene receives the old resources and retires them after presentation.
    internal void AdoptPreparedScene(Scene prepared)
    {
        if (ReferenceEquals(this, prepared) || !prepared.Ready) throw new InvalidOperationException("新壁纸的首帧尚未就绪。");
        EnableNativeRendering(false); prepared.EnableNativeRendering(false);
        AudioEnabled = false; Paused = true;
        if (video != null) video.FramePublished -= VideoFrameChanged;
        if (prepared.video != null) prepared.video.FramePublished -= prepared.VideoFrameChanged;
        if (media != null) ImageAnimator.StopAnimate(media, FrameChanged);
        if (prepared.media != null) ImageAnimator.StopAnimate(prepared.media, prepared.FrameChanged);
        (video, prepared.video) = (prepared.video, video);
        (ownsVideo, prepared.ownsVideo) = (prepared.ownsVideo, ownsVideo);
        (media, prepared.media) = (prepared.media, media);
        (wallpaper, prepared.wallpaper) = (prepared.wallpaper, wallpaper);
        (empty, prepared.empty) = (prepared.empty, empty);
        transform = prepared.transform; layoutMode = prepared.layoutMode;
        layoutReferenceSize = prepared.layoutReferenceSize; time = 0;
        reportedVideoError = reportedAudioError = null; observedVideoFrames = -1;
        PlaybackFailed = null; AudioFailed = null;
        Volume = prepared.Volume; renderingSuspended = false;
        if (video != null) { video.FramePublished += VideoFrameChanged; video.Paused = true; video.AudioEnabled = false; }
        if (prepared.video != null) prepared.video.FramePublished += prepared.VideoFrameChanged;
        if (media != null && ImageAnimator.CanAnimate(media)) ImageAnimator.Animate(media, FrameChanged);
        if (media != null && !ImageAnimator.CanAnimate(media)) timer.Stop(); else timer.Start();
        EnableNativeRendering(video != null); Invalidate();
    }
    public void LoadVideoPreview(Wallpaper item, VideoPlayback source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (item.Kind != "video" || item.Path == null) throw new ArgumentException("共享预览需要视频壁纸。", nameof(item));
        if (ownsVideo && ReferenceEquals(video, source)) throw new InvalidOperationException("不能将此场景拥有的视频播放实例改为借用预览。");
        ReleaseVideo(); reportedVideoError = reportedAudioError = null; observedVideoFrames = -1;
        if (media != null) { ImageAnimator.StopAnimate(media, FrameChanged); media.Dispose(); media = null; }
        wallpaper = item; time = 0; empty = false; transform = new();
        ownsVideo = false; audioEnabled = false; video = source;
        video.FramePublished += VideoFrameChanged;
        EnableNativeRendering(!RenderingSuspended);
        dragging = false; Capture = false; Cursor = EditingEnabled ? Cursors.SizeAll : Cursors.Default;
        timer.Start(); Invalidate();
    }
    public void ClearScene()
    {
        timer.Stop();
        ReleaseVideo(); reportedVideoError = reportedAudioError = null;
        if (media != null) { ImageAnimator.StopAnimate(media, FrameChanged); media.Dispose(); media = null; }
        empty = true; dragging = false; Capture = false; Cursor = Cursors.Default; Invalidate();
    }
    public void SetTransform(WallpaperTransform value)
    {
        var normalized = value.Normalize();
        if (transform == normalized) return;
        transform = normalized; Invalidate();
    }
    void EditTransform(WallpaperTransform value)
    {
        var previous = transform;
        SetTransform(value);
        if (previous != transform) TransformChanged?.Invoke(transform);
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!EditingEnabled || empty || e.Button != MouseButtons.Left) return;
        Focus(); dragStart = e.Location; dragOrigin = transform; dragging = true; Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging || Width <= 0 || Height <= 0) return;
        EditTransform(dragOrigin.Pan((e.X - dragStart.X) / (float)Width, (e.Y - dragStart.Y) / (float)Height));
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) { dragging = false; Capture = false; }
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) dragging = false;
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (!EditingEnabled || empty || Width <= 0 || Height <= 0) return;
        float requestedScale = transform.Scale * (float)Math.Pow(1.1, e.Delta / 120.0);
        EditTransform(transform.ZoomAt(requestedScale, new(e.X / (float)Width - .5f, e.Y / (float)Height - .5f)));
    }
    void FrameChanged(object? sender, EventArgs e) { }
    protected override void OnPaint(PaintEventArgs e)
    {
        PaintedFrames++;
        var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
        if (Width == 0 || Height == 0) return;
        if (empty) { g.Clear(Color.FromArgb(8, 13, 32)); return; }
        g.Clear(Color.Black);
        long renderStart = System.Diagnostics.Stopwatch.GetTimestamp();
        var state = g.Save();
        try
        {
            using var matrix = transform.CreateMatrix(ClientSize);
            g.MultiplyTransform(matrix);
            // Both the filled image and generated scene use the same movable canvas.
            g.SetClip(ClientRectangle, CombineMode.Intersect);
            PaintScene(g);
        }
        finally
        {
            g.Restore(state);
            AccountRenderTime(System.Diagnostics.Stopwatch.GetElapsedTime(renderStart).TotalMilliseconds);
            // Fps is an upper limit: leave time for input/messages even when a
            // large frame takes longer to paint than the requested interval.
        }
    }
    void PaintScene(Graphics g)
    {
        if (video != null) { video.Draw(g, ClientSize, LayoutMode, LayoutReferenceSize); return; }
        if (media != null)
        {
            if (!Paused) ImageAnimator.UpdateFrames(media);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            WallpaperLayout.DrawImage(g, media, ClientSize, LayoutMode, LayoutReferenceSize); return;
        }
        bool ocean = wallpaper.Kind == "ocean", space = wallpaper.Kind == "space";
        using var bg = new LinearGradientBrush(ClientRectangle, Color.FromArgb(8, 13, 32), ocean ? Color.FromArgb(8, 61, 79) : Color.FromArgb(26, 20, 61), 90);
        g.FillRectangle(bg, ClientRectangle);
        var rng = new Random(42);
        for (int i = 0; i < 120; i++)
        {
            float x = (float)rng.NextDouble() * Width, y = (float)rng.NextDouble() * Height * .8f;
            int alpha = (int)(100 + 100 * Math.Sin(time * .7 + i));
            using var star = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 25, 200), 205, 228, 255));
            g.FillEllipse(star, x, y, i % 7 == 0 ? 3 : 1.5f, i % 7 == 0 ? 3 : 1.5f);
        }
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
            using var path = new GraphicsPath();
            var points = new List<PointF>();
            for (int i = 0; i <= 80; i++)
            {
                float x = Width * i / 80f;
                float y = Height * (ocean ? .6f : .34f) + (float)Math.Sin(i * .075 + time * .22 + layer * .12) * Height * .12f + layer * Height * .013f;
                points.Add(new(x, y));
            }
            path.AddCurve(points.ToArray());
            using var pen = new Pen(ocean ? Color.FromArgb(55, 66, 201, 214) : Color.FromArgb(32, 85 + layer * 6, 232, 183), Height * .035f);
            g.DrawPath(pen, path);
        }
        using var ground = new SolidBrush(Color.FromArgb(9, 15, 27));
        g.FillPolygon(ground, new PointF[] {new(0, Height), new(0, Height * .87f), new(Width * .18f, Height * .75f), new(Width * .34f, Height * .91f), new(Width * .62f, Height * .81f), new(Width, Height * .94f), new(Width, Height)});
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { timer.Dispose(); ReleaseVideo(); if (media != null) { ImageAnimator.StopAnimate(media, FrameChanged); media.Dispose(); } }
        base.Dispose(disposing);
    }
}

static class Desktop
{
    delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? c, string? t);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? c, string? t);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, IntPtr p);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint f, uint t, out IntPtr r);
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder name, int max);
    [DllImport("user32.dll", SetLastError = true)] static extern bool SetLayeredWindowAttributes(IntPtr h, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out NativeRect rect);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
    [DllImport("user32.dll")] static extern int MapWindowPoints(IntPtr from, IntPtr to, ref Point point, uint count);
    [StructLayout(LayoutKind.Sequential)] struct NativeRect { public int Left, Top, Right, Bottom; }
    public static string LastAttachment { get; private set; } = "";
    internal static int SurfaceInitializations { get; private set; }
    static bool BelowIcons(IntPtr handle, IntPtr icons)
    {
        for (var above = GetWindow(handle, 3); above != IntPtr.Zero; above = GetWindow(above, 3))
            if (above == icons) return true;
        return false;
    }
    public static string Describe()
    {
        var p = FindWindow("Progman", null);
        var output = new StringBuilder();
        for (var h = FindWindowEx(p, IntPtr.Zero, null, null); h != IntPtr.Zero; h = FindWindowEx(p, h, null, null))
        {
            var name = new StringBuilder(256); GetClassName(h, name, 256); GetWindowRect(h, out var r);
            output.AppendLine($"0x{h:X} {name}; Visible={IsWindowVisible(h)}; Style=0x{GetWindowLong(h,-16):X}; ExStyle=0x{GetWindowLong(h,-20):X}; Rect={r.Left},{r.Top},{r.Right},{r.Bottom}");
        }
        return output.ToString();
    }
    public static void Attach(Form window, Rectangle bounds)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) throw new InvalidOperationException("未找到 Windows 桌面，请确认资源管理器正在运行。");
        // New Explorer compositing puts the original background in a Progman child.
        // Our opaque layered child belongs between that background and the icon view.
        bool raisedDesktop = (GetWindowLong(progman, -20) & 0x00200000) != 0;
        IntPtr worker = IntPtr.Zero, icons = IntPtr.Zero;
        void FindLayers()
        {
            raisedDesktop = (GetWindowLong(progman, -20) & 0x00200000) != 0;
            worker = icons = IntPtr.Zero;
            if (raisedDesktop)
            {
                worker = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
                icons = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            }
            else
                EnumWindows((h, _) =>
                {
                    var view = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (view != IntPtr.Zero) { worker = FindWindowEx(IntPtr.Zero, h, "WorkerW", null); icons = view; return false; }
                    return true;
                }, IntPtr.Zero);
        }
        FindLayers();
        // Explorer's creation message can itself redraw the shell. Only request a
        // wallpaper layer when one is missing, not on every desktop or media switch.
        if (worker == IntPtr.Zero || icons == IntPtr.Zero)
        { SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1), 2, 1000, out _); FindLayers(); }
        if (worker == IntPtr.Zero || icons == IntPtr.Zero) throw new InvalidOperationException("未找到可用的桌面壁纸层，请开启桌面图标后重试。");
        var parent = raisedDesktop ? progman : worker;
        var handle = window.Handle;
        var origin = bounds.Location;
        MapWindowPoints(IntPtr.Zero, parent, ref origin, 1);
        bool attached = GetParent(handle) == parent && (GetWindowLong(handle, -16) & 0x40000000) != 0
            && (!raisedDesktop || (GetWindowLong(handle, -20) & 0x00080000) != 0);
        if (attached)
        {
            bool positioned = GetWindowRect(handle, out var existing) && existing.Left == bounds.Left && existing.Top == bounds.Top
                && existing.Right - existing.Left == bounds.Width && existing.Bottom - existing.Top == bounds.Height;
            // IsWindowVisible includes ancestor visibility. A temporary hidden shell
            // during a desktop animation does not mean our own surface needs rebuilding.
            bool ownVisible = (GetWindowLong(handle, -16) & 0x10000000) != 0;
            bool correctOrder = !raisedDesktop || BelowIcons(handle, icons) && !BelowIcons(handle, worker);
            if (!positioned || !ownVisible || !correctOrder)
            {
                if (raisedDesktop && BelowIcons(handle, worker) && !SetWindowPos(worker, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010))
                    throw new InvalidOperationException("无法恢复原壁纸所在的桌面层。");
                if (!SetWindowPos(handle, raisedDesktop ? icons : IntPtr.Zero, origin.X, origin.Y, bounds.Width, bounds.Height, 0x0010 | 0x0040))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法恢复桌面壁纸的位置。");
                window.PerformLayout(); window.Invalidate(true); window.Update();
            }
            if ((GetWindowLong(handle, -16) & 0x10000000) == 0 || !GetWindowRect(handle, out var restored) || restored.Left != bounds.Left || restored.Top != bounds.Top
                || restored.Right - restored.Left != bounds.Width || restored.Bottom - restored.Top != bounds.Height
                || raisedDesktop && (!BelowIcons(handle, icons) || BelowIcons(handle, worker))) throw new InvalidOperationException("恢复后的桌面壁纸层级或位置异常。");
            LastAttachment = $"ReusedSurface; Parent=0x{parent:X}; Wallpaper=0x{handle:X}; Bounds={bounds}";
            return;
        }
        if (!IsWindowVisible(parent)) throw new InvalidOperationException("桌面壁纸层当前不可见，请返回桌面后重试。");
        if (raisedDesktop)
        {
            SetWindowLong(handle, -20, GetWindowLong(handle, -20) | 0x00080000);
            if (!SetLayeredWindowAttributes(handle, 0, 255, 2)) throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法启用 Windows 11 桌面合成。Style=0x{GetWindowLong(handle, -16):X}; ExStyle=0x{GetWindowLong(handle, -20):X}");
        }
        SetWindowLong(handle, -16, (GetWindowLong(handle, -16) & ~unchecked((int)0x80000000)) | 0x40000000);
        SetParent(handle, parent);
        if (GetParent(handle) != parent) throw new InvalidOperationException("无法将壁纸附加到桌面。");
        window.Show();
        if (raisedDesktop)
        {
            // Recreate the redirection surface after the window becomes a real shell child.
            int extended = GetWindowLong(handle, -20);
            SetWindowLong(handle, -20, extended & ~0x00080000);
            SetWindowLong(handle, -20, extended | 0x00080000);
            if (!SetLayeredWindowAttributes(handle, 0, 255, 2)) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法初始化桌面子窗口合成表面。");
        }
        if (raisedDesktop && !SetWindowPos(worker, new IntPtr(1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010))
            throw new InvalidOperationException("无法调整原壁纸所在的桌面层。");
        if (!SetWindowPos(handle, raisedDesktop ? icons : IntPtr.Zero, origin.X, origin.Y, bounds.Width, bounds.Height, 0x0010 | 0x0040 | 0x0020))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置桌面壁纸的位置。");
        window.PerformLayout(); window.Invalidate(true); window.Update();
        if (!IsWindowVisible(handle) || !GetWindowRect(handle, out var actual) || actual.Right - actual.Left != bounds.Width || actual.Bottom - actual.Top != bounds.Height || actual.Left != bounds.Left || actual.Top != bounds.Top)
            throw new InvalidOperationException("壁纸窗口的可见状态或显示器位置异常，应用未完成。");
        if (raisedDesktop && (!BelowIcons(handle, icons) || GetParent(handle) != progman))
            throw new InvalidOperationException("壁纸窗口未进入桌面图标下方的正确层级。");
        LastAttachment = $"Mode={(raisedDesktop ? "Windows11Layered" : "LegacyWorkerW")}; Parent=0x{parent:X}; Wallpaper=0x{handle:X}; Icons=0x{icons:X}; Background=0x{worker:X}; Bounds={bounds}; Visible={IsWindowVisible(handle)}; Style=0x{GetWindowLong(handle, -16):X}; ExStyle=0x{GetWindowLong(handle, -20):X}";
        SurfaceInitializations++;
    }
}

static class SelfTest
{
    public static void Run(bool desktopTest)
    {
        using var scene = new Scene { Size = new(640, 360) };
        foreach (var kind in new[] { "aurora", "ocean", "space" }) { scene.LoadScene(new(kind, kind)); using var bitmap = new Bitmap(640, 360); scene.DrawToBitmap(bitmap, new(0, 0, 640, 360)); if (bitmap.GetPixel(320, 180).A != 255) throw new Exception("Render failed"); }
        using var main = new MainWindow(System.IO.Path.Combine(AppContext.BaseDirectory, "ui-test-library"), false); main.Show(); Application.DoEvents(); using var capture = new Bitmap(main.Width, main.Height); main.DrawToBitmap(capture, new(0, 0, main.Width, main.Height)); capture.Save(System.IO.Path.Combine(AppContext.BaseDirectory, "smoke-test.png")); main.Close();
        string desktopResult = "Desktop attachment not requested.";
        if (desktopTest)
        {
            using var wall = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, Bounds = Screen.PrimaryScreen!.Bounds };
            wall.Controls.Add(new Scene());
            Desktop.Attach(wall, wall.Bounds); Application.DoEvents(); wall.Close();
            desktopResult = "PASS: parent, visibility, screen bounds and desktop z-order verified; window removed. " + Desktop.LastAttachment;
        }
        File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "self-test.txt"), "PASS: three scenes rendered; library UI opened and captured. " + desktopResult);
    }
}

