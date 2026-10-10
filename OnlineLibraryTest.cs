using System.Reflection;
using System.Text;
using System.Text.Json;

namespace LumaWall;

static class OnlineLibraryTest
{
    internal sealed class OffscreenGallery(AlphaProvider provider, string path, Func<OnlineAsset[]> existing,
        Action<string> saveRoot, Action<OnlineDownloadResult> commit) : OnlineWallpaperDialog(provider, path, existing, saveRoot, commit)
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
    }
    internal sealed class FixtureTransport : IOnlineTransport
    {
        public Dictionary<string, byte[]> Bodies { get; } = [];
        public string? SlowId;
        public Action? StartedSlowRead;
        public Task<OnlineResponse> OpenAsync(Uri address, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (address.AbsolutePath.Contains("/" + SlowId + ".png") && SlowId != null)
                return Task.FromResult(new OnlineResponse(new SlowStream(StartedSlowRead), "image/png"));
            if (!Bodies.TryGetValue(address.AbsolutePath, out var data)) throw new IOException("Fixture response unavailable.");
            return Task.FromResult(new OnlineResponse(new MemoryStream(data), address.AbsolutePath.EndsWith(".png") ? "image/png" : "text/html"));
        }
    }
    sealed class SlowStream(Action? started) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { started?.Invoke(); await Task.Delay(5000, cancellationToken); return 0; }
    }
    static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static string Card(string id, string originalHost = "images3.alphacoders.com") => $$"""
        <div class='thumb-container-wallpaper-desktop' id='content_{{id}}'>
        <meta content='https://{{originalHost}}/100/{{id}}.png' itemprop='contentUrl'>
        <meta itemprop="thumbnailUrl" content="https://images3.alphacoders.com/100/thumb-350-{{id}}.webp">
        <meta itemprop="url" content="https://wall.alphacoders.com/big.php?i={{id}}">
        <meta itemprop="name" content="原图 {{id}} &amp; Wallpaper">
        <picture><source srcset='https://images3.alphacoders.com/100/thumb-350-{{id}}.png'><img src='https://images3.alphacoders.com/100/thumbbig-{{id}}.webp'></picture>
        <span>HD Wallpaper (4096x2160)</span></div>
        """;
    internal static byte[] ImageBytes()
    {
        using var image = new Bitmap(64, 36); using var graphics = Graphics.FromImage(image); graphics.Clear(Color.MediumSeaGreen);
        using var stream = new MemoryStream(); image.Save(stream, System.Drawing.Imaging.ImageFormat.Png); return stream.ToArray();
    }
    static T Wait<T>(Task<T> task) { AudioTest.Wait(task); return task.GetAwaiter().GetResult(); }
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "online-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var report = new List<string>();
        try
        {
            var topic = AlphaProvider.TopicAddress("genshin-impact", 2);
            Check(topic.AbsolutePath == "/genshin-impact-wallpapers" && topic.Query.Contains("page=2"), "Keyword must locate a public paged topic.");
            Check(AlphaProvider.TopicAddress("原神").AbsolutePath == topic.AbsolutePath, "Chinese alias must reach the public Genshin topic.");
            foreach (string bad in new[] { "https://evil.example/pictures", "https://alphacoders.com:1234/test", "https://alphacoders.com/search/view?q=test", "../private" })
            {
                bool rejected = false; try { AlphaProvider.TopicAddress(bad); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Untrusted URL or internal search must be rejected.");
            }
            string robots = "User-agent: Other\nUser-agent: *\nAllow: /\nDisallow: /search/view\nDisallow: /private\nAllow: /private/public\nUser-agent: unrelated\nDisallow: /\n";
            Check(!AlphaTransport.RobotsAllows(robots, "/search/view?q=genshin-impact")
                && AlphaTransport.RobotsAllows(robots, "/genshin-impact-wallpapers")
                && AlphaTransport.RobotsAllows(robots, "/private/public/test"), "Robots groups and longest matching rule must apply.");
            foreach (string bad in new[] { "..", @"foo\bar", "CON", "bad?", "" })
            {
                bool rejected = false; try { OnlineStorage.CategoryName(bad); } catch (ArgumentException) { rejected = true; }
                Check(rejected, "Unsafe category path must be rejected.");
            }
            var fixture = new FixtureTransport(); byte[] bytes = ImageBytes();
            fixture.Bodies["/genshin-impact-wallpapers"] = Encoding.UTF8.GetBytes("<h1>Genshin Impact</h1>" + Card("1001") + Card("1001") + Card("999", "localhost") + Card("1002"));
            foreach (string id in new[] { "1001", "1002" })
            {
                fixture.Bodies[$"/100/{id}.png"] = bytes; fixture.Bodies[$"/100/thumb-350-{id}.png"] = bytes;
            }
            var provider = new AlphaProvider(fixture); var page = Wait(provider.SearchAsync("genshin-impact", 1, default));
            Check(page.Images.Count == 2 && page.Images[0].Name.Contains("&") && page.Images[0].Original.AbsolutePath == "/100/1001.png"
                && page.Images[0].Thumbnail.AbsolutePath.EndsWith("thumb-350-1001.png") && page.Images[0].Width == 4096,
                "HTML parser must associate original/thumbnail/name/size and discard duplicate/untrusted entries.");
            using (var thumbnail = Wait(provider.ThumbnailAsync(page.Images[0], default)))
                Check(thumbnail.Size == new Size(480, 270), "Online result must display a bounded real thumbnail.");
            report.Add("Public topic keyword/URL/paging; robots groups; trusted original URLs; safe categories; card parsing and real thumbnail pixels.");
            string downloadRoot = Path.Combine(root, "chosen-downloads");
            var result = Wait(OnlineStorage.DownloadAsync(provider, page.Images, downloadRoot, "原神", [], null, default));
            Check(result.Items.Count == 2 && result.Errors.Count == 0 && result.Items.All(x => File.ReadAllBytes(x.Item.Path!).SequenceEqual(bytes)),
                "Only original image bytes must be stored, without resampling or another media copy.");
            Check(result.Items.All(x => Path.GetDirectoryName(x.Item.Path) == Path.Combine(downloadRoot, "原神") && OnlineStorage.Owns(x.Asset, x.Item.Path!)),
                "Downloads must belong to the selected root/category and exact ownership records.");
            var duplicate = Wait(OnlineStorage.DownloadAsync(provider, page.Images, downloadRoot, "其他分类", result.Items.Select(x => x.Asset), null, default));
            Check(duplicate.Items.Count == 0 && duplicate.Skipped == 2, "Existing downloaded originals must not be downloaded twice.");
            string appData = Path.Combine(root, "app"); Wallpaper retained;
            using (var window = new MainWindow(appData, false))
            {
                window.SetOnlineRoot(downloadRoot); window.CommitOnlineDownloads(result);
                var settings = Field<Settings>(window, "settings");
                Check(result.Committed && settings.OnlineAssets.Count == 2 && settings.Library.Count == 2, "Commit must persist ownership, source URLs and direct file paths.");
                var categories = Field<ComboBox>(window, "libraryCategory"); categories.SelectedItem = "Alpha Coders / 原神";
                var library = Field<ThumbnailLibrary>(window, "library");
                Check(library.Items.Count == 2, "Category filter must show only website images.");
                library.SelectedIndex = 0; typeof(MainWindow).GetMethod("RenameSelectedWallpaper", Private)!.Invoke(window, ["原神改名"]);
                var renamed = Field<Settings>(window, "settings").Library.Single(x => x.Name == "原神改名");
                Check(File.ReadAllBytes(renamed.Path!).SequenceEqual(bytes) && settings.OnlineAssets.ContainsKey(renamed.Key),
                    "Display rename must retain original bytes, metadata and the same managed file.");
                typeof(MainWindow).GetMethod("DeleteSelectedWallpaper", Private)!.Invoke(window, null);
                Check(!File.Exists(renamed.Path) && Field<Settings>(window, "settings").OnlineAssets.Count == 1,
                    "Delete must clean only the exact downloaded file and its metadata.");
                retained = Field<Settings>(window, "settings").Library.Single();
                using var online = new OffscreenGallery(provider, downloadRoot, () => [], _ => { }, _ => { });
                using var manager = new OnlineFilesDialog(window);
                online.StartPosition = FormStartPosition.Manual; online.Location = new(-32000, -32000); online.Show(); manager.CreateControl();
                using var screenshot = new Bitmap(online.Width, online.Height); online.DrawToBitmap(screenshot, new(Point.Empty, screenshot.Size));
                screenshot.Save(Path.Combine(AppContext.BaseDirectory, "online-empty-ui.png"));
            }
            using (var reopened = new MainWindow(appData, false))
            {
                Check(reopened.OnlineRoot == downloadRoot && reopened.OnlineEntries().Single().Item.Key == retained.Key, "Restart must restore the download root and direct path.");
                var settings = Field<Settings>(reopened, "settings");
                settings.Favorites.Add(retained.Key); settings.Transforms[retained.Key] = new(.8f, .1f, 0); settings.Playlist.Keys.Add(retained.Key);
                string virtualId = Guid.NewGuid().ToString(), monitorId = Screen.PrimaryScreen!.DeviceName;
                settings.VirtualDesktops[virtualId] = new() { Name = "其他桌面", Monitors = new() { [monitorId] = new() { WallpaperKey = retained.Key,
                    Playlist = new() { Keys = [retained.Key] }, Transforms = new() { [retained.Key] = new(.7f) } } } };
                string moved = Path.Combine(root, "moved-original.png"); File.Move(retained.Path!, moved);
                reopened.RelocateOnlineFile(retained, moved); var updated = Field<Settings>(reopened, "settings").Library.Single();
                Check(settings.Library.Single().Path == moved && settings.Favorites.Contains(updated.Key)
                    && settings.Transforms.ContainsKey(updated.Key) && settings.Playlist.Keys.Contains(updated.Key)
                    && !settings.OnlineAssets[updated.Key].Owned, "Relocation must remap path keys, favorites, profiles and playlist while protecting external originals.");
                Check(settings.VirtualDesktops[virtualId].Monitors[monitorId].WallpaperKey == updated.Key && settings.VirtualDesktops[virtualId].Monitors[monitorId].Transforms.ContainsKey(updated.Key)
                    && settings.VirtualDesktops[virtualId].Monitors[monitorId].Playlist.Keys.Contains(updated.Key), "Relocation must also remap inactive virtual desktop profiles.");
                var library = Field<ThumbnailLibrary>(reopened, "library"); library.SelectedIndex = library.Items.ToList().FindIndex(x => x.Key == updated.Key);
                typeof(MainWindow).GetMethod("DeleteSelectedWallpaper", Private)!.Invoke(reopened, null);
                Check(File.Exists(moved) && File.ReadAllBytes(moved).SequenceEqual(bytes), "Relocated external original must survive library deletion.");
                Check(settings.VirtualDesktops[virtualId].Monitors[monitorId].WallpaperKey == null, "Deleting a relocated image must clear inactive desktop references.");
            }
            report.Add("Exact-byte download/category paths; deduplication; persisted commit; category filtering; rename/delete; restart; relocated path references and external original protection.");

            using (var uiOwner = new MainWindow(Path.Combine(root, "ui-app"), false))
            using (var online = new OffscreenGallery(provider, downloadRoot, () => Field<Settings>(uiOwner, "settings").OnlineAssets.Values.ToArray(),
                uiOwner.SetOnlineRoot, uiOwner.CommitOnlineDownloads))
            {
                online.StartPosition = FormStartPosition.Manual; online.Location = new(-32000, -32000); online.Show();
                AudioTest.Wait(online.SearchTopicAsync("genshin-impact"));
                var cards = (List<(OnlineImage Image, CheckBox Check, PictureBox Picture)>)typeof(OnlineWallpaperDialog).GetField("cards", Private)!.GetValue(online)!;
                var downloadButton = (Button)typeof(OnlineWallpaperDialog).GetField("download", Private)!.GetValue(online)!;
                Check(cards.Count == 2 && cards[0].Picture.Width > 100 && cards[0].Picture.Parent!.Visible, "Result cards must be laid out and visible.");
                cards[0].Check.Checked = true; Check(downloadButton.Enabled, "Checking one result must enable download.");
                var downloadTask = (Task)typeof(OnlineWallpaperDialog).GetMethod("DownloadAsync", Private)!.Invoke(online, null)!; AudioTest.Wait(downloadTask);
                Check(Field<Settings>(uiOwner, "settings").OnlineAssets.Count == 1 && !cards[0].Check.Enabled
                    && Field<ThumbnailLibrary>(uiOwner, "library").SelectedIndex >= 0, "Native checkbox/download flow must commit only the selected original and show it in the library.");
                online.Close();
            }
            report.Add("Rendered native cards; checkbox enables download; real dialog callback imports only the checked original and marks it already imported.");

            string failureApp = Path.Combine(root, "blocked-app");
            var failed = Wait(OnlineStorage.DownloadAsync(provider, [page.Images[0]], downloadRoot, "失败回滚", [], null, default));
            string failedPath = failed.Items.Single().Item.Path!;
            using (var blocked = new MainWindow(failureApp, false))
            {
                Directory.CreateDirectory(Path.Combine(failureApp, "settings.json.tmp"));
                bool rejected = false; try { blocked.CommitOnlineDownloads(failed); } catch (IOException) { rejected = true; } catch (UnauthorizedAccessException) { rejected = true; }
                Check(rejected && !File.Exists(failedPath) && Field<Settings>(blocked, "settings").OnlineAssets.Count == 0,
                    "Settings save failure must roll back downloaded files and in-memory references.");
            }
            fixture.SlowId = "1002";
            using (var cancellation = new CancellationTokenSource())
            {
                fixture.StartedSlowRead = () => cancellation.CancelAfter(50);
                bool cancelled = false;
                try { Wait(OnlineStorage.DownloadAsync(provider, page.Images, downloadRoot, "中途取消", [], null, cancellation.Token)); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && Directory.GetFiles(Path.Combine(downloadRoot, "中途取消")).Length == 0,
                    "Cancel after one completed image must clean both that new image and the partial next image.");
            }
            fixture.SlowId = null; fixture.Bodies["/100/1001.png"] = Encoding.UTF8.GetBytes("not an image");
            var invalid = Wait(OnlineStorage.DownloadAsync(provider, [page.Images[0]], downloadRoot, "坏图片", [], null, default));
            Check(invalid.Items.Count == 0 && invalid.Errors.Count == 1 && Directory.GetFiles(Path.Combine(downloadRoot, "坏图片")).Length == 0,
                "Invalid original bytes must never enter the library or leave partial files.");
            report.Add("Atomic save failure; batch cancellation after a completed file; partial-file cleanup; damaged-image rejection.");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "online-library-test.txt"), "PASS:\n" + string.Join("\n", report));
        }
        finally { TestFiles.RemoveFixture(root); }
    }
}

static class OnlineLiveTest
{
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "online-live-fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var provider = new AlphaProvider(); var pageTask = provider.SearchAsync("genshin-impact", 1, cancellation.Token);
            AudioTest.Wait(pageTask); var page = pageTask.GetAwaiter().GetResult();
            if (page.Images.Count == 0) throw new IOException("The live public Genshin topic returned no parsed wallpapers.");
            var secondPage = provider.SearchAsync("genshin-impact", 2, cancellation.Token); AudioTest.Wait(secondPage);
            var next = secondPage.GetAwaiter().GetResult();
            if (next.Images.Count == 0 || next.Images.Select(x => x.Id).SequenceEqual(page.Images.Select(x => x.Id)))
                throw new IOException("Live pagination did not return a distinct second page.");
            var thumbnailTask = provider.ThumbnailAsync(page.Images[0], cancellation.Token); AudioTest.Wait(thumbnailTask);
            using var thumbnail = thumbnailTask.GetAwaiter().GetResult(); thumbnail.Save(Path.Combine(AppContext.BaseDirectory, "online-live-thumbnail.png"));
            var downloaded = OnlineStorage.DownloadAsync(provider, [page.Images[0]], root, "genshin-impact", [], null, cancellation.Token);
            AudioTest.Wait(downloaded); var result = downloaded.GetAwaiter().GetResult();
            if (result.Items.Count != 1) throw new IOException(string.Join("\n", result.Errors));
            using var original = Image.FromFile(result.Items[0].Item.Path!);
            if (original.Width <= thumbnail.Width || original.Height <= thumbnail.Height) throw new IOException("Live download must contain the full-size original.");
            using var online = new OnlineLibraryTest.OffscreenGallery(provider, root, () => [], _ => { }, _ => { });
            online.StartPosition = FormStartPosition.Manual; online.Location = new(-32000, -32000); online.Show();
            var searchUi = online.SearchTopicAsync("genshin-impact"); AudioTest.Wait(searchUi);
            var uiWait = System.Diagnostics.Stopwatch.StartNew();
            while (!online.AllThumbnailsReady && uiWait.Elapsed.TotalSeconds < 60) { Application.DoEvents(); Thread.Sleep(5); }
            if (online.ResultCount != page.Images.Count || !online.AllThumbnailsReady) throw new IOException("Live gallery failed to display all real thumbnails.");
            using var screenshot = new Bitmap(online.Width, online.Height); online.DrawToBitmap(screenshot, new(Point.Empty, screenshot.Size));
            screenshot.Save(Path.Combine(AppContext.BaseDirectory, "online-live-ui.png"));
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "online-live-test.txt"),
                $"PASS: live public page, robots rules, distinct second page, real native gallery thumbnails and one selected original, never modifying the user library.\nPage: {page.Address}\nParsed {page.Images.Count} images; second page={next.Images.Count} distinct results.\nOriginal: {page.Images[0].Original}\nDecoded original: {original.Width}x{original.Height}; file={new FileInfo(result.Items[0].Item.Path!).Length} bytes; category/path recorded.\nFixture original removed after validation.");
        }
        finally { TestFiles.RemoveFixture(root); }
    }
}
