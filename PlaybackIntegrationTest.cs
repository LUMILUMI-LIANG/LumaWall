using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;

namespace LumaWall;

static class PlaybackIntegrationTest
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    static object? Call(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    static Task CallTask(MainWindow window, string name, params object[] args) => (Task)Call(window, name, args)!;
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
    }
    static void Until(Func<bool> condition, string message, int timeout = 20000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException(message);
            Application.DoEvents(); Thread.Sleep(5);
        }
    }
    static void Wait(Task task)
    {
        Until(() => task.IsCompleted, "Playback integration operation timed out.");
        task.GetAwaiter().GetResult();
    }
    static Bitmap Capture(Scene scene)
    {
        var bitmap = new Bitmap(scene.Width, scene.Height);
        scene.DrawToBitmap(bitmap, new(Point.Empty, bitmap.Size)); return bitmap;
    }
    static void ImageFile(string path, Color color, ImageFormat format)
    {
        using var image = new Bitmap(320, 180); using var graphics = Graphics.FromImage(image);
        graphics.Clear(color); image.Save(path, format);
    }
    static void CheckSavedOptions(MainWindow window, string stage, bool expectedShuffle = false)
    {
        var current = Field<Settings>(window, "settings").Playlist;
        var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Field<string>(window, "folder"), "settings.json")))!.Playlist;
        Check(current.IntervalSeconds == 17 && current.FadeSeconds == .3 && current.Shuffle == expectedShuffle
            && saved.IntervalSeconds == 17 && saved.FadeSeconds == .3 && saved.Shuffle == expectedShuffle,
            $"Playback options changed at {stage}; current={JsonSerializer.Serialize(current)}; saved={JsonSerializer.Serialize(saved)}; nativeMode={Field<ComboBox>(window, "rotationMode").SelectedIndex}.");
    }
    static void CommitMode(ComboBox mode, int index)
    {
        mode.SelectedIndex = index;
        typeof(ComboBox).GetMethod("OnSelectionChangeCommitted", Private)!.Invoke(mode, [EventArgs.Empty]);
    }

    public static void Run()
    {
        void Trace(string stage) => File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "playback-trace.txt"), stage + "\n");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "playback-trace.txt"), "Start\n");
        // Every write and deletion belongs to this fresh, isolated fixture directory.
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "playback-fixtures-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string red = Path.Combine(root, "original-red.png"), blue = Path.Combine(root, "original-blue.gif"), video = Path.Combine(root, "original-video.mp4");
        ImageFile(red, Color.Red, ImageFormat.Png); ImageFile(blue, Color.Blue, ImageFormat.Gif);
        Wait(Task.Run(() => VideoTestFixture.Create(video)));
        Trace("Fixture ready");
        TestTransition(red, blue);
        Trace("Transition passed");
        using (var window = new MainWindow(root, false))
        {
            window.Show(); Pump(100);
            TestQueueAndOptions(window);
            CheckSavedOptions(window, "queue editing");
            Trace("Queue passed");
            TestDropAndPersistence(window, root, red, blue, video);
            CheckSavedOptions(window, "mixed-media drop");
            Trace("Drop passed");
            TestDesktopRotation(window);
            CheckSavedOptions(window, "desktop rotation");
            Trace("Rotation passed");
            TestPendingDeletion(window, video);
            CheckSavedOptions(window, "pending deletion");
            Trace("Pending deletion passed");
            Call(window, "Stop");
            Check(Field<Form?>(window, "desktopWindow") == null && !Field<PlaylistController>(window, "playlist").Running, "Stop must release the desktop wallpaper and stop rotation.");
            window.Close();
        }
        Pump(100);
        var persisted = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json")))!;
        using (var reopened = new MainWindow(root, false))
        {
            var settings = Field<Settings>(reopened, "settings");
            Check(settings.Library.Count == 2 && settings.Library.All(x => x.Kind == "image" && File.Exists(x.Path)), "Valid imported images must survive restart while deleted video stays removed.");
            Check(settings.Playlist.Keys.SequenceEqual(persisted.Playlist.Keys), "Queue order must survive restart.");
            Check(Field<ListBox>(reopened, "queue").Items.Count == 3, "Restart must rebuild the persisted queue.");
            CheckSavedOptions(reopened, "restart");
        }
        Check(File.Exists(red) && File.Exists(blue) && File.Exists(video), "Imported original files must never be removed.");
        string expectedParent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        Check(string.Equals(Path.GetDirectoryName(root), expectedParent, StringComparison.OrdinalIgnoreCase), "Fixture cleanup must stay inside the executable directory.");
        Directory.Delete(root, true);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "playback-test.txt"),
            "PASS: queue add/deduplicate/remove/reorder; option persistence/restart; real file-drop event and mixed PNG/GIF/MP4 batch import; video preview/poster; crossfade pixel blending, timing and pause/resume; real desktop attach, timer rotation and manual next; pause freezes playlist and scene; deleting a pending video cancels loading and preserves originals; stop releases desktop playback.");
    }

    static void TestTransition(string red, string blue)
    {
        using var scene = new TransitionScene { Size = new(320, 180), Paused = true };
        scene.LoadScene(new("Blue", "image", blue));
        using var old = new Scene { Size = new(320, 180), Paused = true }; old.LoadScene(new("Red", "image", red));
        scene.BeginTransition(Capture(old), .8);
        using (var first = Capture(scene)) Check(first.GetPixel(160, 90).R > 245 && first.GetPixel(160, 90).B < 10, "Fade must begin with the previous wallpaper pixels.");
        Pump(140); Check(scene.TransitionProgress == 0, "Paused fade must not advance.");
        scene.Paused = false;
        Until(() => scene.TransitionProgress >= .3, "Fade did not advance after resume.");
        scene.Paused = true; double frozen = scene.TransitionProgress;
        using (var blend = Capture(scene))
        {
            var pixel = blend.GetPixel(160, 90);
            Check(pixel.R > 25 && pixel.B > 25 && pixel.G < 10, "Mid-fade must blend previous and incoming pixels.");
        }
        Pump(140); Check(scene.TransitionProgress == frozen, "Pausing an active fade must freeze its progress.");
        scene.Paused = false; Until(() => scene.TransitionProgress >= 1, "Fade did not finish.");
        using var finished = Capture(scene);
        Check(finished.GetPixel(160, 90).B > 245 && finished.GetPixel(160, 90).R < 10, "Completed fade must expose the incoming wallpaper.");
    }

    static void TestQueueAndOptions(MainWindow window)
    {
        var library = Field<ThumbnailLibrary>(window, "library"); var queue = Field<ListBox>(window, "queue");
        var mode = Field<ComboBox>(window, "rotationMode"); _ = mode.Handle;
        for (int i = 0; i < 3; i++) { library.SelectedIndex = i; Call(window, "AddToPlaylist"); }
        library.SelectedIndex = 0; Call(window, "AddToPlaylist"); Check(queue.Items.Count == 3, "Adding the same wallpaper twice must not duplicate queue membership.");
        queue.SelectedIndex = 2; Call(window, "MoveInPlaylist", -1);
        Check(((Wallpaper)queue.Items[1]).Kind == "space", "Move up must change queue order.");
        queue.SelectedIndex = 2; Call(window, "RemoveFromPlaylist");
        Check(queue.Items.Count == 2 && ((Wallpaper)queue.Items[1]).Kind == "space", "Removing a queue member must preserve other members and order.");
        library.SelectedIndex = 1; Call(window, "AddToPlaylist");
        Field<NumericUpDown>(window, "intervalSeconds").Value = 17;
        Field<NumericUpDown>(window, "fadeSeconds").Value = .3m;
        CommitMode(mode, 1);
        var settings = Field<Settings>(window, "settings");
        Check(settings.Playlist.Keys.SequenceEqual(["builtin:aurora", "builtin:space", "builtin:ocean"]) && settings.Playlist.IntervalSeconds == 17 && settings.Playlist.Shuffle && settings.Playlist.FadeSeconds == .3, "Queue and controls must update saved playback settings.");
        CheckSavedOptions(window, "random mode commit", true);
        CommitMode(mode, 0);
        CheckSavedOptions(window, "sequential mode commit");
        // A programmatic/native selection change is not a user mode choice.
        mode.SelectedIndex = 1;
        Field<NumericUpDown>(window, "intervalSeconds").Value = 18;
        Check(!Field<Settings>(window, "settings").Playlist.Shuffle, "Editing an interval must preserve the committed mode despite a transient native selection.");
        Field<NumericUpDown>(window, "intervalSeconds").Value = 17;
        mode.SelectedIndex = 0;
        CheckSavedOptions(window, "programmatic mode refresh");
    }

    static void TestDropAndPersistence(MainWindow window, string root, string red, string blue, string video)
    {
        var library = Field<ThumbnailLibrary>(window, "library");
        // Hidden process startup can suppress the first Form.Show and defer child
        // handles. A real drop targets a native control, so create that handle here.
        _ = library.Handle;
        Check(library.IsHandleCreated, "File-drop target must have a native control handle.");
        var data = new DataObject(); data.SetData(DataFormats.FileDrop, new[] { red, blue, video });
        var drag = new DragEventArgs(data, 0, 0, 0, DragDropEffects.Copy, DragDropEffects.None);
        typeof(Control).GetMethod("OnDragEnter", Private)!.Invoke(library, [drag]);
        Check(drag.Effect == DragDropEffects.Copy, "The wallpaper library must accept real file-drop data.");
        typeof(Control).GetMethod("OnDragDrop", Private)!.Invoke(library, [drag]);
        Until(() => !Field<bool>(window, "importing") && library.Items.Count == 6, "Dropped mixed-media batch was not imported.");
        var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json")))!;
        Check(settings.Library.Count == 3 && settings.Library.Count(x => x.Kind == "video") == 1 && settings.Library.All(x => File.Exists(x.Path)), "A dropped mixed-media batch must save validated managed copies.");
        Check(library.SelectedIndex == 5, "Batch import must select the last imported wallpaper.");
        Wait(Field<Scene>(window, "preview").WaitUntilReadyAsync());
        var posters = Field<Dictionary<string, Bitmap>>(library, "thumbnails"); string key = library.Items[5].Key;
        try
        {
            Until(() => library.PosterReady(key) || library.PosterState(key).StartsWith("failed:", StringComparison.Ordinal), "Video poster load timed out.");
            Check(library.PosterReady(key), "Video thumbnail load failed: " + library.PosterState(key));
            var pixel = posters[key].GetPixel(420, 180);
            Check(pixel.R > 160, $"Decoded video thumbnail has unexpected pixel {pixel}; state={library.PosterState(key)}.");
        }
        catch (Exception ex)
        {
            if (posters.TryGetValue(key, out var failed)) failed.Save(Path.Combine(AppContext.BaseDirectory, "playback-poster-failed.png"));
            throw new InvalidOperationException($"Video thumbnail failure; state={library.PosterState(key)}; key={key}", ex);
        }
        Call(window, "AddToPlaylist");
        Check(Field<ListBox>(window, "queue").Items.Count == 4, "Imported video must be usable as a playlist member.");
    }

    static void TestDesktopRotation(MainWindow window)
    {
        var settings = Field<Settings>(window, "settings");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var controller = new PlaylistController(settings.Playlist, () => now, new Random(1));
        typeof(MainWindow).GetField("playlist", Private)!.SetValue(window, controller);
        string first = controller.Start("builtin:aurora")!; Wait(CallTask(window, "RotateAsync", first));
        Check(controller.CurrentKey == first && Field<Wallpaper>(window, "activeWallpaper").Key == first && Field<Form>(window, "desktopWindow").Visible, "Playlist start must commit a visible desktop wallpaper.");
        now = now.AddSeconds(settings.Playlist.IntervalSeconds);
        Until(() => controller.CurrentKey == "builtin:space", "The actual UI timer did not perform a due playlist rotation.");
        string next = controller.Next()!; Wait(CallTask(window, "RotateAsync", next));
        Check(next == "builtin:ocean" && Field<Wallpaper>(window, "activeWallpaper").Key == next, "Manual next must apply queue order on the real desktop.");
        Call(window, "TogglePause"); double remaining = controller.RemainingSeconds;
        now = now.AddSeconds(200); Pump(140);
        Check(controller.Paused && controller.RemainingSeconds == remaining && Field<Scene>(window, "desktopScene").Paused && controller.CurrentKey == next, "Pause must freeze desktop playback and the rotation countdown.");
        Call(window, "TogglePause");
        Check(!controller.Paused && !Field<Scene>(window, "desktopScene").Paused, "Resume must unfreeze both playback and countdown.");
    }

    static void TestPendingDeletion(MainWindow window, string originalVideo)
    {
        var controller = Field<PlaylistController>(window, "playlist"); var library = Field<ThumbnailLibrary>(window, "library");
        string key = controller.Next()!; Check(key == library.Items[5].Key, "Video must be the next queued member for cancellation regression.");
        string managed = library.Items[5].Path!;
        var pending = CallTask(window, "RotateAsync", key);
        library.SelectedIndex = 5; Call(window, "DeleteSelectedWallpaper");
        try { Wait(pending); } catch (OperationCanceledException) { }
        Pump(100);
        Check(!controller.Loading && controller.PendingKey == null && Field<Wallpaper?>(window, "activeWallpaper")?.Key != key, "A deleted pending wallpaper must not commit or resurrect after asynchronous loading.");
        Check(!File.Exists(managed) && File.Exists(originalVideo), "Deleting pending imported media must remove only its managed copy.");
        Check(Field<ListBox>(window, "queue").Items.Count == 3 && !Field<Settings>(window, "settings").Playlist.Keys.Contains(key), "Deleted pending media must leave the queue and persisted settings.");
    }
}
