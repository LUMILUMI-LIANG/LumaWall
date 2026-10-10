using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LumaWall;

static class RenameTest
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    static object? Call(MainWindow target, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(target, args);
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void Wait(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("Rename verification timed out.");
            Application.DoEvents(); Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
    }
    static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static void Run()
    {
        // No reads or writes to the real user library; all media are isolated fixtures.
        string root = Path.Combine(AppContext.BaseDirectory, "rename-fixtures-" + Guid.NewGuid().ToString("N"));
        string media = Path.Combine(root, "media"); Directory.CreateDirectory(media);
        string image = Path.Combine(root, "original.png"), gif = Path.Combine(root, "original.gif"), video = Path.Combine(root, "original.mp4");
        using (var bitmap = new Bitmap(48, 32))
        {
            using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.CornflowerBlue);
            bitmap.Save(image, ImageFormat.Png); bitmap.Save(gif, ImageFormat.Gif);
        }
        Wait(Task.Run(() => VideoTestFixture.Create(video)));
        string[] originals = [image, gif, video];
        string[] copies = originals.Select(x => Path.Combine(media, "copy" + Path.GetExtension(x))).ToArray();
        for (int i = 0; i < copies.Length; i++) File.Copy(originals[i], copies[i]);
        var protectedFiles = originals.Concat(copies).ToDictionary(x => x, x => (Hash: Hash(x), Modified: File.GetLastWriteTimeUtc(x)));
        var imported = copies.Select((x, i) => new Wallpaper("原名称" + i, i == 2 ? "video" : "image", x)).ToList();
        var transform = new WallpaperTransform(1.3f, .12f, -.08f);
        var options = new PlaylistOptions { Keys = imported.Select(x => x.Key).ToList(), IntervalSeconds = 60, FadeSeconds = .4, Shuffle = true };
        var initial = new Settings { Library = imported, Playlist = options, Transforms = imported.ToDictionary(x => x.Key, _ => transform) };
        string settingsPath = Path.Combine(root, "settings.json");
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(initial));
        string[] names = ["星空 & 海浪 / 图片", "轻风：GIF 动图", "雨夜 / 原声视频"];

        using (var window = new MainWindow(root, false))
        {
            var library = Field<ThumbnailLibrary>(window, "library"); var preview = Field<Scene>(window, "preview");
            var settings = Field<Settings>(window, "settings"); var queue = Field<ListBox>(window, "queue");
            Check(!Field<Button>(window, "renameButton").Enabled, "Built-in scenes should not expose media rename.");
            Call(window, "RenameSelectedWallpaper", "内置不变");
            Check(library.Items[0].Name == "极光之境", "Built-in rename must be a no-op.");
            for (int i = 0; i < 3; i++)
            {
                library.SelectedIndex = i + 3;
                if (i == 2) Wait(preview.WaitUntilReadyAsync());
                var source = preview.Video;
                Check(Field<Button>(window, "renameButton").Enabled, "Imported media should enable rename.");
                Call(window, "RenameSelectedWallpaper", "  " + names[i] + "  ");
                Check(library.SelectedIndex == i + 3 && library.Items[i + 3].Name == names[i], "Rename must trim and update only the selected thumbnail label.");
                Check(Field<Label>(window, "title").Text == names[i] && ((Wallpaper)queue.Items[i]).Name == names[i], "Title and queue must show the new display name.");
                Check(preview.Transform == transform && ReferenceEquals(source, preview.Video), "Rename must retain transforms and the current preview decoder.");
                Check(library.Items[i + 3].Path == copies[i] && library.Items[i + 3].Key == imported[i].Key, "Rename must retain media identity and path.");
            }
            var controller = Field<PlaylistController>(window, "playlist");
            controller.Start(imported[2].Key); controller.Commit(imported[2].Key); controller.Pause(true);
            double remaining = controller.RemainingSeconds;
            Wait((Task)Call(window, "SwitchDesktopAsync", library.Items[5], 0d)!);
            var desktop = Field<Scene>(window, "desktopScene"); var desktopWindow = Field<Form>(window, "desktopWindow");
            Check(preview.IsSharedVideo && ReferenceEquals(preview.Video, desktop.Video), "The active video preview should share the desktop decoder.");
            var decoder = desktop.Video;
            Call(window, "RenameSelectedWallpaper", "播放中重命名");
            Check(ReferenceEquals(decoder, desktop.Video) && ReferenceEquals(decoder, preview.Video) && !desktopWindow.IsDisposed, "Active rename must not restart video or close its desktop window.");
            Check(Field<Wallpaper>(window, "activeWallpaper").Name == "播放中重命名", "Active playback metadata must follow the display name.");
            Check(controller.Running && controller.Paused && controller.RemainingSeconds == remaining && controller.CurrentKey == imported[2].Key, "Rename must preserve rotation and its countdown.");
            Check(settings.Playlist.Keys.SequenceEqual(options.Keys) && settings.Playlist.Shuffle && settings.Playlist.FadeSeconds == .4, "Rename must preserve playlist membership and options.");
            Call(window, "Stop");

            // A loading video must commit the newest name rather than its old captured record.
            var preparing = (Task)Call(window, "SwitchDesktopAsync", library.Items[5], 0d)!;
            Check(!preparing.IsCompleted, "Video fixture must exercise asynchronous preparation.");
            Call(window, "RenameSelectedWallpaper", names[2]); Wait(preparing);
            Check(Field<Wallpaper>(window, "activeWallpaper").Name == names[2], "Preparing video must commit its renamed record.");
            Call(window, "Stop");

            string previous = library.Items[5].Name;
            foreach (string invalid in new[] { "   ", "two\nlines", new string('x', WallpaperName.MaximumLength + 1) })
            {
                bool rejected = false;
                try { Call(window, "RenameSelectedWallpaper", invalid); } catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { rejected = true; }
                Check(rejected && library.Items[5].Name == previous, "Invalid names must leave metadata unchanged.");
            }
            Directory.CreateDirectory(settingsPath + ".tmp");
            bool failed = false;
            try { Call(window, "RenameSelectedWallpaper", "不能保存的名称"); } catch (TargetInvocationException ex) when (ex.InnerException is IOException or UnauthorizedAccessException) { failed = true; }
            Check(failed && library.Items[5].Name == previous && settings.Library[2].Name == previous && ((Wallpaper)queue.Items[2]).Name == previous, "Save failure must roll back settings and leave all labels unchanged.");
            Directory.Delete(settingsPath + ".tmp");
        }

        using (var reopened = new MainWindow(root, false))
        {
            var library = Field<ThumbnailLibrary>(reopened, "library");
            Check(library.Items.Skip(3).Select(x => x.Name).SequenceEqual(names), "Renamed image, GIF and video labels must survive restart.");
            var settings = Field<Settings>(reopened, "settings");
            Check(settings.Transforms.Values.All(x => x == transform), "Transforms must survive name changes and restart.");
        }
        foreach (var (path, before) in protectedFiles)
            Check(File.Exists(path) && Hash(path) == before.Hash && File.GetLastWriteTimeUtc(path) == before.Modified, "Original and copied media names, bytes and write times must remain unchanged: " + path);

        // A shared file can have two display names; changing one must retain row identity.
        var shared = new Settings { Library = [imported[2] with { Name = "第一条" }, imported[2] with { Name = "第二条" }] };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(shared));
        using (var window = new MainWindow(root, false))
        {
            var library = Field<ThumbnailLibrary>(window, "library"); library.SelectedIndex = 4;
            var pending = (Task)Call(window, "SwitchDesktopAsync", library.Items[4], 0d)!;
            Call(window, "RenameSelectedWallpaper", "共享文件第二条"); Wait(pending);
            Check(library.Items[3].Name == "第一条" && Field<Wallpaper>(window, "activeWallpaper").Name == "共享文件第二条", "Shared-path loading must keep the renamed target row.");
            library.SelectedIndex = 3; Call(window, "RenameSelectedWallpaper", "同名壁纸");
            Check(Field<Wallpaper>(window, "activeWallpaper").Name == "共享文件第二条", "Renaming another reference must not change the active row's name.");
            library.SelectedIndex = 4; Call(window, "RenameSelectedWallpaper", "同名壁纸");
            Call(window, "DeleteSelectedWallpaper");
            Check(Field<Form?>(window, "desktopWindow") == null && File.Exists(copies[2]) && library.Items.Count == 4, "Deleting the renamed active row must stop playback and preserve the remaining shared reference.");
        }

        using (var dialog = new RenameWallpaperDialog(names[0]))
        {
            dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new(-32000, -32000);
            dialog.Show(); Application.DoEvents(); dialog.PerformLayout();
            using var screenshot = new Bitmap(dialog.Width, dialog.Height);
            dialog.DrawToBitmap(screenshot, new(Point.Empty, screenshot.Size)); screenshot.Save(Path.Combine(AppContext.BaseDirectory, "rename-dialog.png"));
        }
        foreach (string path in copies.Concat(originals)) File.Delete(path);
        File.Delete(settingsPath); Directory.Delete(media); Directory.Delete(root);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "rename-test.txt"), "PASS: image/GIF/video display-name persistence; original/copy paths, bytes and write times unchanged; thumbnail/title/queue labels; preview and active decoder continuity; transforms and paused rotation countdown; rename during preparation; invalid-name rejection; save-failure rollback; shared-path row identity; duplicate-name active deletion.");
    }
}
