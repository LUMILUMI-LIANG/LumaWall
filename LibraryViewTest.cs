using System.Reflection;
using System.Text.Json;
namespace LumaWall;
static class LibraryViewTest
{
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(MainWindow w, string name) => (T)typeof(MainWindow).GetField(name, Flags)!.GetValue(w)!;
    static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "library-view-fixtures"); Directory.CreateDirectory(root);
        string image = Path.Combine(root, "wallpaper.png");
        using (var bitmap = new Bitmap(1600, 900)) { using var g = Graphics.FromImage(bitmap); g.Clear(Color.Teal); g.FillEllipse(Brushes.Coral, 300, 100, 800, 700); bitmap.Save(image); }
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new Settings { Library = [new("大图测试", "image", image)] }));
        using (var w = new MainWindow(root, false))
        {
            w.Show(); Application.DoEvents();
            Check(w.Text == "" && !w.ShowIcon, "Caption text and icon must be hidden.");
            var library = Field<ThumbnailLibrary>(w, "library"); var sidebar = Field<Control>(w, "playlistSidebar");
            int width = library.Width, height = library.Height;
            Check(!sidebar.Visible && height > 300, "Playlist should initially be collapsed and free library height.");
            Field<Button>(w, "playlistToggle").PerformClick(); Application.DoEvents();
            Check(sidebar.Visible && library.Width < width && sidebar.Width > 200, "Sidebar must consume width, not library height.");
            Check(library.Height == height, "Opening sidebar must preserve library height.");
            var body = Field<TableLayoutPanel>(w, "body");
            float inspectorWidth = body.ColumnStyles[2].Width;
            w.ResizeInspector(100); Application.DoEvents();
            Check(body.ColumnStyles[2].Width < inspectorWidth - 80, "Dragging main divider should resize inspector.");
            int playlistWidth = sidebar.Width;
            w.ResizePlaylist(-70); Application.DoEvents();
            Check(sidebar.Width > playlistWidth + 50, "Dragging playlist divider should resize sidebar.");
            w.ResizeInspector(100000); w.ResizePlaylist(-100000); Application.DoEvents();
            Check(body.ColumnStyles[2].Width > 200 && sidebar.Width < Field<TableLayoutPanel>(w, "libraryWorkspace").Width * .7, "Extreme drags must respect limits.");
            w.ResizeInspector(-100); w.ResizePlaylist(100); Application.DoEvents();
            foreach (int percent in new[] {100, 150})
            {
                w.ApplyMainTextScale(percent); Application.DoEvents();
                Check(Field<ListBox>(w, "queue").Height > 60, "Playlist list must remain usable at scaled text size.");
                using var capture = new Bitmap(w.Width, w.Height); w.DrawToBitmap(capture, new(0, 0, w.Width, w.Height)); capture.Save(Path.Combine(root, $"sidebar-{percent}.png"));
            }
            Field<Button>(w, "playlistToggle").PerformClick(); Application.DoEvents(); Check(!sidebar.Visible, "Sidebar close failed.");
            w.ResizeInspector(100000); Application.DoEvents();
            foreach (string name in new[] { "zoomSlider", "horizontalSlider", "verticalSlider", "volumeSlider" })
                Check(Field<AdjustmentSlider>(w, name).Width >= 240, "Narrow inspector must preserve full-width slider: " + name);
            var zoom = Field<NumericUpDown>(w, "zoomValue"); zoom.Value = 125;
            Check(Field<AdjustmentSlider>(w, "zoomSlider").Value == 125, "Number and slider must stay connected.");
            using (var narrow = new Bitmap(w.Width, w.Height)) { w.DrawToBitmap(narrow, new(0, 0, w.Width, w.Height)); narrow.Save(Path.Combine(root, "narrow-dark.png")); }
            w.Close();
        }
        var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json")))!;
        Check(saved.InspectorWidthRatio > 0 && saved.PlaylistWidthRatio > 0, "Divider ratios must persist.");
        using (var restored = new MainWindow(root, false))
        {
            restored.Show(); Application.DoEvents();
            var body = Field<TableLayoutPanel>(restored, "body");
            double expected = saved.InspectorWidthRatio * (body.ClientSize.Width - body.Padding.Horizontal);
            Check(Math.Abs(body.ColumnStyles[2].Width - expected) < 3, "Main divider must restore saved proportion.");
            restored.Size = new(1180, 760); Application.DoEvents();
            Check(Field<ThumbnailLibrary>(restored, "library").Width > 100, "Narrow window must preserve library area.");
            restored.Close();
        }
        using (var viewer = new ImageViewerDialog(new("查看大图", "image", image)))
        {
            viewer.Show(); Application.DoEvents(); var viewport = viewer.Viewport;
            Check(viewport.ImageSize == new Size(1600, 900), "Viewer must load full resolution image.");
            Check(viewport.ScaleFactor > 0 && viewport.ScaleFactor < 1, "Fit must contain image.");
            viewport.Zoom(1, new(300, 200)); Check(viewport.ScaleFactor == 1, "Original size failed.");
            viewport.Fit(); using var capture = new Bitmap(viewer.Width, viewer.Height); viewer.DrawToBitmap(capture, new(0, 0, viewer.Width, viewer.Height)); capture.Save(Path.Combine(root, "viewer.png"));
            using var file = File.Open(image, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            viewer.Close();
        }
        File.WriteAllText(Path.Combine(root, "result.txt"), "PASS: sidebar open/close, 100/150% text, full-resolution viewer, fit, 1:1, no file lock.");
    }
}
