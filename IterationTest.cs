using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;

namespace LumaWall;

static class IterationTest
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    static object? Call(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void Wait(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed.TotalSeconds > 35) throw new TimeoutException("Iteration operation did not complete.");
            Application.DoEvents(); Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
    }
    static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
    static void Image(string path, Color color, ImageFormat format)
    {
        using var image = new Bitmap(320, 180); using (var graphics = Graphics.FromImage(image)) graphics.Clear(color); image.Save(path, format);
    }

    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "iteration-fixtures-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string red = Path.Combine(root, "original-red.png"), blue = Path.Combine(root, "original-blue.gif"), video = Path.Combine(root, "original-video.mp4"), bad = Path.Combine(root, "broken.mp4");
        Image(red, Color.Red, ImageFormat.Png); Image(blue, Color.Blue, ImageFormat.Gif); Wait(Task.Run(() => VideoTestFixture.Create(video))); File.WriteAllText(bad, "invalid video");
        var media = new List<Wallpaper> { new("红色图片", "image", red), new("蓝色动图", "image", blue), new("原声视频", "video", video) };
        string settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Settings { Library = media, RestoreOnLaunch = true }));
        string primaryId = Screen.PrimaryScreen!.DeviceName;
        using (var window = new MainWindow(root, false))
        {
            window.StartPosition = FormStartPosition.Manual; window.Location = new(-32000, -32000); window.Show(); Pump(120);
            var library = Field<ThumbnailLibrary>(window, "library"); var settings = Field<Settings>(window, "settings");
            var search = Field<TextBox>(window, "librarySearch"); var filter = Field<ComboBox>(window, "libraryType");
            search.Text = "蓝色";
            Check(library.Items.Count == 1 && library.Items[0].Path == blue, "Search must display matching media with correct row identity.");
            Call(window, "RenameSelectedWallpaper", "海浪 GIF");
            Check(settings.Library.Single(x => x.Path == blue).Name == "海浪 GIF" && library.Items.Count == 0, "Filtered rename must update the master row and remove a nonmatching result.");
            search.Text = ""; filter.SelectedIndex = 2;
            Check(library.Items.Count == 1 && library.Items[0].Path == blue, "GIF filter must retain the correct master entry.");
            Call(window, "ToggleFavorite"); Field<CheckBox>(window, "favoritesOnly").Checked = true;
            Check(library.Items.Count == 1 && settings.Favorites.Contains(media[1].Key), "Favorites must persist and combine with type filters.");
            Field<CheckBox>(window, "favoritesOnly").Checked = false;
            Call(window, "DeleteSelectedWallpaper");
            Check(settings.Library.Count == 2 && settings.Library.All(x => x.Path != blue) && File.Exists(blue) && File.Exists(red), "Deleting a filtered GIF row must preserve unrelated and original files.");
            filter.SelectedIndex = 0;
            var sorted = Field<ComboBox>(window, "librarySort"); sorted.SelectedIndex = 1;
            Check(library.Items.Select(x => x.Name).SequenceEqual(library.Items.Select(x => x.Name).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)), "Name sorting must preserve displayed identity.");
            sorted.SelectedIndex = 0; library.SelectedIndex = library.Items.ToList().FindIndex(x => x.Path == video); Wait(Field<Scene>(window, "preview").WaitUntilReadyAsync());
            Wait((Task)Call(window, "SwitchDesktopAsync", library.Items[library.SelectedIndex], 0d)!);
            var runtimes = Field<Dictionary<string, MonitorRuntime>>(window, "monitorRuntimes"); var primary = runtimes[primaryId];
            var owner = primary.Scene!.Video;
            Field<ComboBox>(window, "layoutMode").SelectedIndex = (int)WallpaperLayoutMode.Fit; Call(window, "ChangeLayoutMode");
            Check(primary.Scene.LayoutMode == WallpaperLayoutMode.Fit && ReferenceEquals(owner, primary.Scene.Video), "Changing fit mode must synchronize desktop without restarting the video decoder.");

            // Synthetic display identity exercises independent slots without requiring a second physical display.
            string secondId = "isolated-secondary";
            var secondaryProfile = new MonitorProfile { Muted = true };
            var secondary = new MonitorRuntime(secondId, new(primary.Bounds.X + 360, primary.Bounds.Y + 240, 320, 180), secondaryProfile);
            runtimes.Add(secondId, secondary); settings.Monitors.Add(secondId, secondaryProfile);
            Wait((Task)Call(window, "SwitchDesktopForMonitorAsync", secondary, settings.Library.First(x => x.Path == red), 0d)!);
            Check(primary.Window != null && secondary.Window != null && !primary.Window.IsDisposed && !secondary.Window.IsDisposed && ReferenceEquals(owner, primary.Scene.Video), "Applying a second display must preserve the first display and decoder.");
            Check(secondary.Scene!.Volume == 0 && primary.Scene.Volume > 0, "New secondary display must default to silent independent playback.");
            Field<NumericUpDown>(window, "volumeValue").Value = 31;
            Check(Math.Abs(primary.Scene.Volume - .31f) < .001 && secondary.Scene.Volume == 0, "Volume changes must affect only the selected monitor.");

            Call(window, "OnLifecycleSnapshot", new LifecycleSnapshot(false, false, [primary.Bounds], false));
            Check(primary.Scene.Paused && !secondary.Scene.Paused && primary.AutoPaused, "Fullscreen pause must affect only the covered display.");
            Call(window, "TogglePause");
            Call(window, "OnLifecycleSnapshot", new LifecycleSnapshot(false, false, [], false));
            Check(primary.Scene.Paused && primary.Profile.Paused, "Leaving fullscreen must preserve manual pause.");
            Call(window, "TogglePause"); Check(!primary.Scene.Paused, "Manual resume must continue after automatic pause ends.");
            Call(window, "OnLifecycleSnapshot", new LifecycleSnapshot(true, false, [], false));
            Check(primary.Scene.Paused && secondary.Scene.Paused, "Locking must pause every wallpaper and its audio clock.");
            Call(window, "OnLifecycleSnapshot", new LifecycleSnapshot(false, false, [], false));
            Check(!primary.Scene.Paused && !secondary.Scene.Paused, "Unlocking must resume independently unpaused displays.");

            var broken = new Wallpaper("坏视频", "video", bad); Field<List<Wallpaper>>(window, "items").Add(broken); settings.Library.Add(broken);
            settings.Playlist = new() { Keys = [broken.Key, media[0].Key], IntervalSeconds = 60, FadeSeconds = 0 };
            var controller = new PlaylistController(settings.Playlist); Set(window, "playlist", controller); Call(window, "CaptureCurrentMonitor");
            string first = controller.Start(broken.Key)!; Wait((Task)Call(window, "RotateAsync", first)!);
            Check(controller.Running && controller.CurrentKey == media[0].Key && primary.Active?.Path == red && primary.FailedKeys.Contains(broken.Key), "A broken video must be logged and skipped to a working entry.");
            var lastGood = primary.Window;
            settings.Playlist = new() { Keys = [broken.Key], IntervalSeconds = 60, FadeSeconds = 0 };
            controller = new(settings.Playlist); Set(window, "playlist", controller); Call(window, "CaptureCurrentMonitor"); primary.FailedKeys.Clear();
            Wait((Task)Call(window, "RotateAsync", controller.Start(broken.Key)!)!);
            Check(!controller.Running && ReferenceEquals(lastGood, primary.Window) && !lastGood!.IsDisposed, "All-failed playlist must stop retrying and preserve the last working wallpaper.");
            Check(File.Exists(Path.Combine(root, "playback-errors.log")), "Playback errors must provide local diagnostic details.");
            Check(window.DiagnosticText().Contains("CPU") && window.DiagnosticText().Contains("坏视频"), "Diagnostics should include resource usage and readable playback errors.");
            Call(window, "Save");
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath))!;
            Check(saved.Monitors[primaryId].WallpaperKey == media[0].Key && saved.Monitors[secondId].WallpaperKey == media[0].Key
                && saved.Monitors[primaryId].Volume == 31 && saved.Monitors[secondId].Muted, "Independent monitor session and audio settings must persist.");
            using (var screenshot = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(screenshot, new(Point.Empty, screenshot.Size)); screenshot.Save(Path.Combine(AppContext.BaseDirectory, "iteration-ui.png")); }
            using (var dialog = new EngineSettingsDialog(settings, false))
            {
                dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new(-32000, -32000); dialog.Show(); Pump(30);
                using var screenshot = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(screenshot, new(Point.Empty, screenshot.Size)); screenshot.Save(Path.Combine(AppContext.BaseDirectory, "iteration-settings.png"));
            }
            window.Close();
        }
        using (var reopened = new MainWindow(root, false))
        {
            Wait((Task)Call(reopened, "RestoreLastSessionAsync")!);
            Check(Field<Wallpaper>(reopened, "activeWallpaper").Path == red, "Explicit session restoration must restore the remembered wallpaper.");
            Check(Field<Settings>(reopened, "settings").Monitors[primaryId].Volume == 31, "Session restoration must retain per-monitor volume.");
        }
        Check(File.Exists(red) && File.Exists(blue) && File.Exists(video), "All iteration operations must preserve original media.");
        TestFiles.RemoveFixture(root);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "iteration-test.txt"), "PASS: search/type/favorites/sort with exact master-row rename/delete; layout changes retain active decoder; independent synthetic monitor windows and audio; fullscreen/lock pause and manual-pause preservation; invalid-video fallback and all-failed retry bound; diagnostic log; monitor/session persistence and restoration; original media protection; offscreen UI/settings rendering. Physical second-monitor hotplug and actual lock/sleep remain manual environment checks.");
    }
}
