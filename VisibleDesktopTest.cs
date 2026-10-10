using System.Diagnostics;

namespace LumaWall;

static class VisibleDesktopTest
{
    public static void Run(bool video = false)
    {
        var bounds = Screen.PrimaryScreen!.Bounds;
        using var wall = new Form { FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Bounds = bounds };
        var scene = new Scene { Paused = true }; wall.Controls.Add(scene);
        string prefix = video ? "video-" : "";
        if (video)
        {
            string fixtureFolder = Path.Combine(AppContext.BaseDirectory, "video-visible-fixture"); Directory.CreateDirectory(fixtureFolder);
            string path = Path.Combine(fixtureFolder, "visible.mp4");
            Wait(Task.Run(() => VideoTestFixture.Create(path)));
            scene.LoadScene(new("Video desktop test", "video", path));
            Wait(scene.WaitUntilReadyAsync());
        }
        Desktop.Attach(wall, bounds);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.ElapsedMilliseconds < 1500) { Application.DoEvents(); Thread.Sleep(15); }
        using var expected = new Bitmap(scene.Width, scene.Height);
        int painted = scene.PaintedFrames;
        scene.DrawToBitmap(expected, new Rectangle(Point.Empty, expected.Size));
        using var actual = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(actual)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        actual.Save(Path.Combine(AppContext.BaseDirectory, prefix + "visible-desktop.png"));
        expected.Save(Path.Combine(AppContext.BaseDirectory, prefix + "expected-desktop.png"));
        int matching = 0, total = 0;
        // Probe the upper desktop strip, away from taskbar and typical central app windows.
        for (int x = 40; x < bounds.Width - 40; x += 80)
            for (int y = 25; y < Math.Min(140, bounds.Height); y += 25)
            {
                var a = actual.GetPixel(x, y); var b = expected.GetPixel(x, y);
                if (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B) < 15) matching++;
                total++;
            }
        scene.Paused = false; elapsed.Restart();
        while (elapsed.ElapsedMilliseconds < (video ? 1300 : 900)) { Application.DoEvents(); Thread.Sleep(15); }
        scene.Paused = true; Application.DoEvents();
        using var secondExpected = new Bitmap(scene.Width, scene.Height);
        scene.DrawToBitmap(secondExpected, new Rectangle(Point.Empty, secondExpected.Size));
        using var secondActual = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(secondActual)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        int moving = 0;
        for (int x = 40; x < bounds.Width - 40; x += 80)
            for (int y = 25; y < bounds.Height - 80; y += 60)
            {
                Color a = actual.GetPixel(x, y), b = expected.GetPixel(x, y);
                Color c = secondActual.GetPixel(x, y), d = secondExpected.GetPixel(x, y);
                if (Distance(a, b) < 15 && Distance(c, d) < 15 && Distance(b, d) > 10 && Distance(a, c) > 10) moving++;
            }
        var tree = Desktop.Describe();
        string currentHandle = $"CurrentHandle=0x{wall.Handle:X}";
        wall.Close();
        Application.DoEvents(); elapsed.Restart();
        while (elapsed.ElapsedMilliseconds < 300) { Application.DoEvents(); Thread.Sleep(15); }
        using var restored = new Bitmap(bounds.Width, bounds.Height);
        using (var g = Graphics.FromImage(restored)) g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        int removed = 0;
        for (int x = 40; x < bounds.Width - 40; x += 80)
            for (int y = 25; y < Math.Min(140, bounds.Height); y += 25)
                if (Distance(actual.GetPixel(x, y), expected.GetPixel(x, y)) < 15 && Distance(restored.GetPixel(x, y), expected.GetPixel(x, y)) > 30) removed++;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, prefix + "visible-test.txt"),
            $"Actual screen pixels matching rendered wallpaper: {matching}/{total}; Animated pixels verified={moving}; Original background restored at {removed} samples; PaintedFrames={painted}; SceneBounds={scene.Bounds}; {currentHandle}; {Desktop.LastAttachment}\n{tree}");
        if (matching < 20) throw new Exception($"Wallpaper is not visible on actual screen: only {matching}/{total} sampled pixels match; PaintedFrames={painted}; {currentHandle}.\n{tree}");
        if (moving < 5) throw new Exception($"Wallpaper animation is not visible: only {moving} samples updated on the screen.");
        if (removed < 20) throw new Exception($"Stopping the wallpaper did not restore the background: only {removed} samples changed.");
    }
    static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    static void Wait(Task task)
    {
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timeout.Elapsed.TotalSeconds > 20) throw new TimeoutException("视频桌面测试准备超时。");
            Application.DoEvents(); Thread.Sleep(10);
        }
        task.GetAwaiter().GetResult();
    }
}
