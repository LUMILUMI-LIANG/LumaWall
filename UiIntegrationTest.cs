using System.Reflection;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace LumaWall;

static class UiIntegrationTest
{
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    static void Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Pump(int milliseconds)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(10); }
    }
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "ui-fixtures"); Directory.CreateDirectory(root);
        List<Wallpaper> imported = [];
        try
        {
            var userSettings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LumaWall", "settings.json")));
            imported = userSettings?.Library.Where(x => x.Path != null && File.Exists(x.Path)).ToList() ?? [];
        }
        catch { }
        File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new Settings { Library = imported }));
        using (var window = new MainWindow(root, false))
        {
            window.Show(); ShowWindow(window.Handle, 4); ShowWindow(window.Handle, 4); window.Activate(); Pump(500);
            var library = Field<ThumbnailLibrary>(window, "library"); var preview = Field<Scene>(window, "preview");
            Check(window.Width >= 1400 && window.Height >= 900, "Default window should be enlarged on this display.");
            Check(library.Items.Count == imported.Count + 3 && library.Width > 800, "Thumbnail library must dominate the main content area.");
            Check(Math.Abs(preview.Width / (double)preview.Height - Screen.AllScreens[0].Bounds.Width / (double)Screen.AllScreens[0].Bounds.Height) < .03, "Preview must match target display aspect ratio.");
            var nativeCombo = Field<ComboBox>(window, "fps");
            if (GetForegroundWindow() == window.Handle) using (var crop = new Bitmap(nativeCombo.Width, nativeCombo.Height))
            {
                using var graphics = Graphics.FromImage(crop);
                graphics.CopyFromScreen(nativeCombo.PointToScreen(Point.Empty), Point.Empty, crop.Size);
                crop.Save(Path.Combine(AppContext.BaseDirectory, "ui-native-fps.png"));
            }
            using (var image = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(image, new(Point.Empty, image.Size)); image.Save(Path.Combine(AppContext.BaseDirectory, "ui-default.png")); }
            typeof(ThumbnailLibrary).GetMethod("OnKeyDown", Private)!.Invoke(library, [new KeyEventArgs(Keys.Right)]);
            Check(library.SelectedIndex == 1, "Arrow keys must select adjacent thumbnails.");
            library.SelectedIndex = 0;
            Field<NumericUpDown>(window, "zoomValue").Value = 175;
            Field<NumericUpDown>(window, "horizontalValue").Value = 20;
            Field<NumericUpDown>(window, "verticalValue").Value = -15;
            Check(preview.Transform == new WallpaperTransform(1.75f, .2f, -.15f), "Numeric controls must update the selected preview.");
            using var desktopScene = new Scene(); typeof(MainWindow).GetField("desktopScene", Private)!.SetValue(window, desktopScene);
            Check(!preview.AudioEnabled && !desktopScene.AudioEnabled, "Preview scenes must remain silent until explicitly attached to the desktop.");
            Field<NumericUpDown>(window, "volumeValue").Value = 37;
            Check(Math.Abs(desktopScene.Volume - .37f) < .001 && !preview.AudioEnabled, "Volume must affect the desktop while leaving the preview silent.");
            Field<CheckBox>(window, "muteAudio").Checked = true;
            Check(desktopScene.Volume == 0, "Mute must silence the active desktop scene.");
            Field<CheckBox>(window, "muteAudio").Checked = false;
            Check(Math.Abs(desktopScene.Volume - .37f) < .001, "Unmute must restore the selected volume.");
            typeof(MainWindow).GetField("activeWallpaper", Private)!.SetValue(window, library.Items[0]);
            Field<AdjustmentSlider>(window, "zoomSlider").Value = 150;
            Check(desktopScene.Transform == preview.Transform && preview.Transform.Scale == 1.5f, "Slider changes must synchronize the active wallpaper.");
            var retained = desktopScene.Transform; library.SelectedIndex = 1;
            Field<NumericUpDown>(window, "zoomValue").Value = 80;
            Check(desktopScene.Transform == retained, "Editing a different wallpaper must not change the active desktop scene.");
            library.SelectedIndex = 0;
            Check(preview.Transform == retained, "Switching back must restore per-wallpaper transform.");
            Field<Button>(window, "resetButton").PerformClick();
            Check(preview.Transform == new WallpaperTransform() && desktopScene.Transform == new WallpaperTransform(), "Reset must synchronize controls, preview and active desktop scene.");
            Field<NumericUpDown>(window, "zoomValue").Value = 125;
            Field<NumericUpDown>(window, "horizontalValue").Value = 12;
            Pump(600);
            var saved = JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(root, "settings.json")))!;
            Check(saved.Transforms["builtin:aurora"] == new WallpaperTransform(1.25f, .12f, 0), "Debounced transform changes must persist.");
            Check(saved.Transforms["builtin:ocean"].Scale == .8f, "Different wallpapers must persist independently.");
            Check(saved.Volume == 37 && !saved.Muted, "Audio volume and mute preferences must persist.");
            window.Size = new(1180, 760); Pump(200);
            Check(library.Width > 500 && Field<Button>(window, "applyButton").Width > 300, "Reduced window must preserve grid and usable inspector controls.");
            using (var image = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(image, new(Point.Empty, image.Size)); image.Save(Path.Combine(AppContext.BaseDirectory, "ui-small.png")); }
            window.Close();
        }
        using (var reopened = new MainWindow(root, false))
        {
            Check(Field<Scene>(reopened, "preview").Transform == new WallpaperTransform(1.25f, .12f, 0), "Reopening the app must restore saved transform.");
            Check(Field<NumericUpDown>(reopened, "volumeValue").Value == 37 && !Field<CheckBox>(reopened, "muteAudio").Checked && !Field<Scene>(reopened, "preview").AudioEnabled, "Reopening must restore audio controls without sounding the preview.");
        }
        TestFiles.RemoveFixture(root);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "ui-test.txt"), "PASS: enlarged layout; thumbnail library and arrow selection; display aspect; numeric and slider edits; active-only live sync; reset; per-wallpaper persistence; desktop volume/mute; silent previews; reopening; reduced layout.");
    }
}
