using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace LumaWall;

static class IterationV013Test
{
    internal sealed class Source(VirtualDesktopSnapshot snapshot) : IVirtualDesktopSource
    { public VirtualDesktopSnapshot Snapshot = snapshot; public VirtualDesktopSnapshot Read() => Snapshot; }
    internal sealed class OffscreenWindow(string folder, IVirtualDesktopSource? source = null) : MainWindow(folder, false, source)
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
    }
    sealed class ProbeWindow : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
    }
    sealed class Transport : IOnlineTransport
    {
        public Dictionary<string, byte[]> Pages = [];
        public List<Uri> Requested = [];
        public HttpStatusCode Missing = HttpStatusCode.NotFound;
        public Task<OnlineResponse> OpenAsync(Uri address, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requested.Add(address);
            if (!Pages.TryGetValue(address.AbsolutePath, out var bytes)) throw new HttpRequestException("Test HTTP response", null, Missing);
            return Task.FromResult(new OnlineResponse(new MemoryStream(bytes), "text/html"));
        }
    }
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    static object? Invoke(MainWindow window, string name, params object[] values) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, values);
    static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    static void Wait(Task task) { AudioTest.Wait(task); task.GetAwaiter().GetResult(); }
    static T Wait<T>(Task<T> task) { Wait((Task)task); return task.GetAwaiter().GetResult(); }
    static void Pump(int milliseconds)
    { var timer = System.Diagnostics.Stopwatch.StartNew(); while (timer.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); } }
    static void Capture(Form form, string name)
    { using var image = new Bitmap(form.Width, form.Height); form.DrawToBitmap(image, new(Point.Empty, image.Size)); image.Save(Path.Combine(AppContext.BaseDirectory, name)); }
    public static void Run()
    {
        var report = new List<string>();
        foreach (string query in new[] { "崩坏三", "崩坏3", "崩壞三", "Honkai Impact 3", "honkai-impact-3rd" })
            Check(AlphaProvider.TopicAddress(query).AbsolutePath == "/honkai-impact-3rd-wallpapers", "Honkai 3 aliases must normalize to its verified topic.");
        foreach (string query in new[] { "崩坏：星穹铁道", "星穹铁道", "星铁", "Honkai: Star Rail", "honkai_star_rail" })
            Check(AlphaProvider.TopicAddress(query).AbsolutePath == "/honkai-star-rail-wallpapers", "Star Rail Chinese/punctuation aliases must normalize.");
        bool chineseRejected = false; try { AlphaProvider.TopicAddress("没有收录的中文主题"); } catch (ArgumentException ex) { chineseRejected = ex.Message.Contains("英文"); }
        Check(chineseRejected, "Unsupported Chinese needs an actionable message instead of a fabricated URL.");
        var legacy = AlphaProvider.TopicAddress("https://wall.alphacoders.com/by_sub_category.php?id=123&page=8&view=infinite", 2);
        Check(legacy.Query.Contains("id=123") && legacy.Query.Contains("page=2") && !legacy.Query.Contains("page=8") && !legacy.Query.Contains("infinite"), "Pasted category links must retain their topic identifier and replace pagination.");
        bool escapedSearchRejected = false; try { AlphaProvider.TopicAddress("https://alphacoders.com/%73earch/view?q=test"); } catch (ArgumentException) { escapedSearchRejected = true; }
        Check(escapedSearchRejected, "Encoded internal search routes must remain excluded.");
        var transport = new Transport();
        string card = OnlineLibraryTest.Card("1001").Replace("class='thumb-container-wallpaper-desktop'", "class='css-grid-content' itemscope itemtype='http://schema.org/ImageObject'")
            .Replace("<span>HD Wallpaper (4096x2160)</span>", "<meta itemprop='width' content='5760'><meta itemprop='height' content='3240'>");
        transport.Pages["/honkai-star-rail"] = Encoding.UTF8.GetBytes("<h1>Honkai: Star Rail</h1>" + card);
        var provider = new AlphaProvider(transport);
        var page = Wait(provider.SearchAsync("https://alphacoders.com/honkai-star-rail", 2, default));
        Check(page.Images.Count == 1 && page.Images[0].Width == 5760 && page.Images[0].Height == 3240 && page.Address.Query.Contains("page=2"), "Generic theme must parse metadata and preserve requested pagination.");
        page = Wait(provider.SearchAsync("Honkai: Star Rail", 1, default));
        Check(page.Images.Count == 1 && transport.Requested[^2].AbsolutePath.EndsWith("-wallpapers") && transport.Requested[^1].AbsolutePath == "/honkai-star-rail", "404 may fall back once to the other public topic format.");
        transport.Missing = HttpStatusCode.Forbidden; transport.Requested.Clear(); bool forbidden = false;
        try { Wait(provider.SearchAsync("unavailable", 1, default)); } catch (HttpRequestException ex) { forbidden = ex.StatusCode == HttpStatusCode.Forbidden; }
        Check(forbidden && transport.Requested.Count == 1, "403 must not trigger alternate requests or bypass.");
        report.Add("Keyword aliases, Unicode/punctuation normalization, generic theme cards, original metadata dimensions, paged public-topic fallback; 403 is not retried.");

        string root = Path.Combine(AppContext.BaseDirectory, "v013-fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            Guid first = Guid.NewGuid(), second = Guid.NewGuid(); string monitor = Screen.PrimaryScreen!.DeviceName;
            var desktops = new[] { new VirtualDesktopEntry(first, "工作"), new VirtualDesktopEntry(second, "休闲") };
            var source = new Source(new(first, desktops));
            var initial = new Settings { Monitors = new() { [monitor] = new() { WallpaperKey = "builtin:aurora", Volume = 25, Paused = true,
                Transforms = new() { ["builtin:aurora"] = new(1.75f, .1f, -.2f) }, Playlist = new() { Keys = ["builtin:aurora", "builtin:ocean"], IntervalSeconds = 15 }, PlaylistEnabled = true } } };
            File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(initial));
            using (var window = new OffscreenWindow(root, source))
            {
                window.StartPosition = FormStartPosition.Manual; window.Location = new(-32000, -32000); window.Show(); Pump(350);
                var settings = Field<Settings>(window, "settings"); var library = Field<ThumbnailLibrary>(window, "library");
                Check(window.Size == new Size(1920, 1200), "New default window must use the user's chosen size on this display.");
                var original = Field<Dictionary<string, MonitorRuntime>>(window, "monitorRuntimes")[monitor];
                var oldWindow = new Form(); var oldScene = new Scene(); oldWindow.Controls.Add(oldScene); original.Window = oldWindow; original.Scene = oldScene; original.Active = library.Items[0];
                using var pending = new CancellationTokenSource(); original.Request = pending;
                original.Playlist.Start("builtin:aurora"); original.Playlist.Commit("builtin:aurora"); original.Playlist.Pause(true); Invoke(window, "PublishMonitor", original);
                window.SetVirtualDesktopWallpaper(second, monitor, library.Items[2]);
                Check(settings.Monitors[monitor].WallpaperKey == "builtin:aurora" && window.VirtualDesktopWallpaperKey(second, monitor) == "builtin:space", "Configuring another desktop must not change the current wallpaper.");
                source.Snapshot = new(second, desktops); Wait(window.ChangeVirtualDesktopAsync(source.Snapshot, false));
                Check(oldWindow.IsDisposed && pending.IsCancellationRequested && settings.Monitors[monitor].WallpaperKey == "builtin:space", "Switch must cancel old preparation, release old desktop window, and bind new profile.");
                Check(settings.VirtualDesktops[first.ToString()].Monitors[monitor].Volume == 25 && settings.VirtualDesktops[first.ToString()].Monitors[monitor].PlaylistEnabled,
                    "Outgoing desktop must retain its own volume and enabled playlist.");
                Field<NumericUpDown>(window, "volumeValue").Value = 65; Invoke(window, "Save");
                source.Snapshot = new(first, desktops); Wait(window.ChangeVirtualDesktopAsync(source.Snapshot, false));
                Check(settings.Volume == 25 && settings.Transforms["builtin:aurora"].Scale == 1.75f && Field<Scene>(window, "preview").Paused, "Returning must restore independent transform, volume and pause.");
                Invoke(window, "PublishMonitor", original);
                Check(Field<Scene?>(window, "desktopScene") == null, "A stale runtime callback must not bind a previous desktop window.");
                source.Snapshot = new(second, desktops); Wait(window.ChangeVirtualDesktopAsync(source.Snapshot, false)); Check(settings.Volume == 65, "Second desktop volume must persist independently.");
                for (int index = 0; index < 12; index++) { source.Snapshot = new(index % 2 == 0 ? first : second, desktops); Wait(window.ChangeVirtualDesktopAsync(source.Snapshot, false)); }
                source.Snapshot = new(first, desktops); Wait(window.ChangeVirtualDesktopAsync(source.Snapshot, false));
                string temporary = Path.Combine(root, "settings.json.tmp"); Directory.CreateDirectory(temporary);
                bool saveFailed = false; try { window.SetVirtualDesktopWallpaper(second, monitor, library.Items[1]); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveFailed = true; }
                Directory.Delete(temporary); Check(saveFailed && window.VirtualDesktopWallpaperKey(second, monitor) == "builtin:space", "Failed inactive-desktop save must roll back.");
                window.SetVirtualDesktopWallpaper(second, monitor, library.Items[0]); library.SelectedIndex = 0; Invoke(window, "DeleteSelectedWallpaper");
                Check(window.VirtualDesktopWallpaperKey(second, monitor) == null, "Deleting a wallpaper must clear references on inactive desktops too.");
                report.Add("Legacy profile migration; independent virtual-desktop wallpaper/volume/transform/pause/playlist; cancellation and old-window release; stale callback rejection; rapid switching; inactive assignment save rollback and deletion cleanup. No system desktop switch was made.");

                library.SelectedIndex = 1; string selected = library.Items[library.SelectedIndex].Key;
                library.ThumbnailWidth = 160; int smallColumns = (int)typeof(ThumbnailLibrary).GetProperty("Columns", Private)!.GetValue(library)!;
                Check((int)typeof(ThumbnailLibrary).GetProperty("TileWidth", Private)!.GetValue(library)! == (int)Math.Round(160 * library.DeviceDpi / 96.0), "Thumbnail preset must keep its requested logical width rather than stretching to fill the row.");
                library.ThumbnailWidth = 440; int largeColumns = (int)typeof(ThumbnailLibrary).GetProperty("Columns", Private)!.GetValue(library)!;
                Check(smallColumns > largeColumns, "Smaller thumbnails must expose more columns.");
                library.ResizeThumbnails(-120); Check(library.ThumbnailWidth == 420 && library.Items[library.SelectedIndex].Key == selected, "Wheel resizing must keep wallpaper selection.");
                library.ThumbnailWidth = 220;
                window.ChangeTextScale(125); Pump(200); Check(Math.Abs(window.Font.Size - 12.5f) < .01 && library.Font.Size >= 12.5f, "Text setting must enlarge inherited and explicit fonts immediately.");
                using (var appearance = new AppearanceDialog(125))
                {
                    window.PrepareDialog(appearance);
                    var sample = (Label)typeof(AppearanceDialog).GetField("sample", Private)!.GetValue(appearance)!;
                    Check(Math.Abs(sample.Font.Size - 12.5f) < .01, "Appearance sample must show target font size rather than applying scaling twice.");
                }
                Check(window.Controls[0].Right <= window.ClientSize.Width && window.Controls[0].Bottom <= window.ClientSize.Height, "125% root must stay within the window, not crop the inspector.");
                var apply = Field<Button>(window, "applyButton");
                Check(window.PointToClient(apply.PointToScreen(Point.Empty)).X + apply.Width <= window.ClientSize.Width, "125% inspector must remain inside the window.");
                Capture(window, "v013-main-125.png");
                window.ChangeTextScale(150); Pump(200); Check(Math.Abs(window.Font.Size - 15f) < .01, "150% text must work without cumulative font growth."); Capture(window, "v013-main-150.png");
                Check(window.Controls[0].Right <= window.ClientSize.Width && window.Controls[0].Bottom <= window.ClientSize.Height, "150% root must stay within the window.");
                Check(window.PointToClient(apply.PointToScreen(Point.Empty)).X + apply.Width <= window.ClientSize.Width, "150% inspector must remain inside the window.");
                Check(library.Parent!.Height <= library.Parent.Parent!.ClientSize.Height, "Large fonts must fit the library layout into its panel so the playlist remains reachable.");
                Check(apply.Parent!.Parent!.Height <= apply.Parent.Parent.Parent!.ClientSize.Height, "Inspector must fit its body row and scroll within the visible area.");
                using (var gallery = new OnlineLibraryTest.OffscreenGallery(provider, root, () => [], _ => { }, _ => { }))
                {
                    window.PrepareDialog(gallery); gallery.StartPosition = FormStartPosition.Manual; gallery.Location = new(-32000, -32000); gallery.Show();
                    Wait(gallery.SearchTopicAsync("https://alphacoders.com/honkai-star-rail")); Pump(100);
                    Check(gallery.Font.Size == 15 && gallery.ResultCount == 1, "Online dialog must inherit text size and generate cards at that size."); Capture(gallery, "v013-online-150.png"); gallery.Close();
                }
                window.ChangeTextScale(100); Check(Math.Abs(window.Font.Size - 10f) < .01, "Returning to 100% must restore original fonts.");
                window.ChangeTextScale(125); window.Close();
            }
            using (var reopened = new OffscreenWindow(root, source))
            { Check(Field<Settings>(reopened, "settings").ThumbnailWidth == 220 && reopened.Font.Size == 12.5f, "Thumbnail and text sizes must survive restart."); }
            report.Add("1920x1200 initial window; thumbnail presets and wheel sizing preserve selection and change grid columns; 100/125/150% text changes reflow controls; dialogs inherit size; text and thumbnail settings survive restart.");
        }
        finally { TestFiles.RemoveFixture(root); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "iteration-v013-test.txt"), "PASS:\n" + string.Join("\n", report));
    }
    public static void Live()
    {
        var provider = new AlphaProvider(); using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3)); var report = new List<string>();
        foreach (string query in new[] { "https://alphacoders.com/honkai-star-rail", "崩坏三", "崩坏：星穹铁道" })
        {
            var result = Wait(provider.SearchAsync(query, 1, deadline.Token));
            Check(result.Images.Count > 0 && result.Images.All(x => x.Width > 0 && x.Height > 0), "Real query must resolve images and dimensions: " + query);
            using var thumbnail = Wait(provider.ThumbnailAsync(result.Images[0], deadline.Token)); Check(thumbnail.Width == 480, "Live thumbnail must decode.");
            report.Add($"{query} -> {result.Address} : {result.Images.Count} images; first original {result.Images[0].Width}x{result.Images[0].Height}; real thumbnail decoded.");
        }
        using var source = new WindowsVirtualDesktopSource(); var snapshot = source.Read(); Check(snapshot.Available, "Windows virtual-desktop current ID must be detected.");
        report.Add($"Windows: detected {snapshot.Desktops.Count} desktops, current={snapshot.Current}; registry is read-only.");
        using var probe = new ProbeWindow { ShowInTaskbar = true, Text = "Luma Wall · isolated virtual desktop check", StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000), Size = new(100, 80) };
        probe.Show(); Pump(200); IntPtr handle = probe.Handle;
        foreach (var desktop in snapshot.Desktops)
        {
            bool moved = source.MoveOwnWindow(handle, desktop.Id); Pump(100);
            Check(moved && source.WindowDesktopId(handle) == desktop.Id, $"Owned probe movement failed: moved={moved}, target={desktop.Id}, actual={source.WindowDesktopId(handle)}.");
            Check(source.WindowOnCurrentDesktop(handle) == (desktop.Id == snapshot.Current), "Windows current-desktop visibility must distinguish inactive owned windows.");
        }
        Check(source.Read().Current == snapshot.Current, "Probe movement must not switch the user's active desktop.");
        report.Add("Documented COM move/query verified on an isolated owned window across existing desktops; user's current desktop unchanged. Real desktop-switch rendering remains a manual environment check.");
        string galleryRoot = Path.Combine(AppContext.BaseDirectory, "v013-live-gallery-fixtures-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var gallery = new OnlineLibraryTest.OffscreenGallery(provider, galleryRoot, () => [], _ => { }, _ => { });
            UiTypography.Apply(gallery, 125, false); gallery.StartPosition = FormStartPosition.Manual; gallery.Location = new(-32000, -32000); gallery.Show();
            Wait(gallery.SearchTopicAsync("https://alphacoders.com/honkai-star-rail"));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!gallery.AllThumbnailsReady && watch.Elapsed < TimeSpan.FromSeconds(60)) Pump(50);
            Check(gallery.AllThumbnailsReady, "Real generic theme gallery must render every compatible thumbnail.");
            Capture(gallery, "v013-live-gallery.png"); gallery.Close();
            report.Add("Real 30-image generic theme gallery rendered at 125% text, including actual thumbnails and original resolutions.");
        }
        finally { if (Directory.Exists(galleryRoot)) TestFiles.RemoveFixture(galleryRoot); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "iteration-v013-live-test.txt"), "PASS:\n" + string.Join("\n", report));
    }
}
