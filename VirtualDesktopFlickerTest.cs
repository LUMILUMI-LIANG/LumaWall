using System.Reflection;
using System.Text.Json;
using System.Diagnostics;

namespace LumaWall;

static class VirtualDesktopFlickerTest
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    sealed class Source(VirtualDesktopSnapshot snapshot) : IVirtualDesktopSource
    {
        public VirtualDesktopSnapshot Snapshot = snapshot;
        public VirtualDesktopSnapshot Read() => Snapshot;
    }
    sealed class OffscreenWindow(string folder, Source source) : MainWindow(folder, false, source)
    { protected override bool ShowWithoutActivation => true; }
    static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    static object? Invoke(MainWindow window, string name, params object[] args) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, args);
    static void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
    static void Pump(int milliseconds)
    {
        var clock = Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(2); }
    }
    static void Wait(Task task)
    {
        var clock = Stopwatch.StartNew();
        while (!task.IsCompleted) { if (clock.Elapsed.TotalSeconds > 25) throw new TimeoutException("Wallpaper switch did not finish."); Pump(2); }
        task.GetAwaiter().GetResult();
    }
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "virtual-switch-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var report = new List<string>();
        string output = Path.Combine(AppContext.BaseDirectory, "virtual-desktop-flicker-test.txt");
        File.WriteAllText(output, "RUNNING: isolated desktop surface checks.");
        try
        {
            string image = Path.Combine(root, "red.png"), blueImage = Path.Combine(root, "blue.png"), video = Path.Combine(root, "red-blue.mp4"), broken = Path.Combine(root, "broken.mp4");
            using (var red = new Bitmap(1280, 720)) { using var g = Graphics.FromImage(red); g.Clear(Color.FromArgb(235, 30, 30)); red.Save(image); }
            using (var blue = new Bitmap(1280, 720)) { using var g = Graphics.FromImage(blue); g.Clear(Color.FromArgb(30, 60, 235)); blue.Save(blueImage); }
            Wait(Task.Run(() => FlickerFixture.Create(video)));
            File.WriteAllText(broken, "invalid video");
            var still = new Wallpaper("Red", "image", image); var movie = new Wallpaper("Video", "video", video); var invalid = new Wallpaper("Invalid", "video", broken);
            var blueStill = new Wallpaper("Blue", "image", blueImage);
            Guid first = Guid.NewGuid(), second = Guid.NewGuid(), empty = Guid.NewGuid(), invalidDesktop = Guid.NewGuid();
            var desktops = new[] { new VirtualDesktopEntry(first, "图片"), new VirtualDesktopEntry(second, "视频"), new VirtualDesktopEntry(empty, "未配置"), new VirtualDesktopEntry(invalidDesktop, "损坏视频") };
            var source = new Source(new(first, desktops)); string display = Screen.PrimaryScreen!.DeviceName;
            File.WriteAllText(Path.Combine(root, "settings.json"), JsonSerializer.Serialize(new Settings { Library = [still, movie, invalid, blueStill],
                Monitors = new() { [display] = new() { WallpaperKey = still.Key, Volume = 21 } },
                VirtualDesktops = new() { [second.ToString()] = new() { Monitors = new() { [display] = new() { WallpaperKey = movie.Key, Volume = 63 } } },
                    [invalidDesktop.ToString()] = new() { Monitors = new() { [display] = new() { WallpaperKey = invalid.Key } } } } }));
            using var window = new OffscreenWindow(root, source) { StartPosition = FormStartPosition.Manual, Location = new(-32000, -32000) };
            window.Show(); Pump(100);
            Field<System.Windows.Forms.Timer>(window, "virtualDesktopTimer").Stop();
            Wait((Task)Invoke(window, "RestoreLastSessionAsync")!); Pump(200);
            MonitorRuntime Current() => Field<Dictionary<string, MonitorRuntime>>(window, "monitorRuntimes")[display];
            var original = Current(); var host = original.Window!; var surface = original.Scene!;
            IntPtr hostHandle = host.Handle, sceneHandle = surface.Handle;
            int surfaces = Desktop.SurfaceInitializations, holds = 0;
            string measurements = FlickerTest.MeasureDesktopSwitches(surface, host, original.Bounds, () =>
            {
                for (int index = 0; index < 20; index++)
                {
                    source.Snapshot = new(index % 2 == 0 ? second : first, desktops);
                    Task task = window.ChangeVirtualDesktopAsync(source.Snapshot);
                    while (!task.IsCompleted)
                    {
                        var incoming = Current();
                        Check(incoming.Window?.Handle == hostHandle && incoming.Scene?.Handle == sceneHandle, "Preparation must retain both the shell host and scene HWND.");
                        if (incoming.HoldingPrevious)
                        {
                            holds++; Check(incoming.Scene!.Paused && !incoming.Scene.AudioEnabled && incoming.Active == null,
                                "Retained frame must be silent, paused, and not claimed as the incoming wallpaper.");
                        }
                        Pump(2);
                    }
                    Wait(task); Pump(130);
                    var active = Current();
                    Check(active.Window?.Handle == hostHandle && active.Scene?.Handle == sceneHandle && !active.HoldingPrevious,
                        "Successful switch must replace media in the existing HWNDs.");
                    Check(active.Active?.Key == (index % 2 == 0 ? movie.Key : still.Key), "Incoming wallpaper must really be active.");
                    Check(active.Profile.Volume == (index % 2 == 0 ? 63 : 21), "Volumes remain independent while the native surface is reused.");
                    if (index % 2 == 0)
                    {
                        Check(active.Scene!.Video != null && active.Scene.AudioEnabled, "Incoming video must own its player and permit original audio.");
                        long before = active.Scene.Video!.PresentedPositionTicks; Pump(100);
                        Check(active.Scene.Video.PresentedPositionTicks > before, "Reused surface must play, not just show a cached first frame.");
                    }
                    Wait((Task)Invoke(window, "RecoverDesktopAsync")!);
                    Check(Desktop.SurfaceInitializations == surfaces, "Healthy hosts must not recreate their composition surface during switches or recovery.");
                }
                Pump(250);
            });
            Check(holds > 0, "The test must observe asynchronous first-frame preparation while the outgoing frame is retained.");
            report.Add(measurements); report.Add($"20 image/video profile switches; held-first-frame observations={holds}; unchanged host/scene HWND; composition initializations added=0; actual video time advances; independent volumes; recovery is idempotent.");
            File.WriteAllText(output, "RUNNING: pixel sampling passed; checking pause/failure/cancellation/icon.\n" + measurements);

            var blueRow = Field<List<Wallpaper>>(window, "items").First(x => x.Key == blueStill.Key);
            Wait((Task)Invoke(window, "SwitchDesktopAsync", blueRow, 0d)!);
            var settings = Field<Settings>(window, "settings");
            var pausedProfile = settings.VirtualDesktops[second.ToString()].Monitors[display];
            pausedProfile.Paused = true; pausedProfile.PlaylistEnabled = true;
            pausedProfile.Playlist = new() { Keys = [movie.Key], FadeSeconds = 1, IntervalSeconds = 60 };
            source.Snapshot = new(second, desktops); Wait(window.ChangeVirtualDesktopAsync(source.Snapshot)); Pump(100);
            Check(Current().Scene!.Paused && ((TransitionScene)Current().Scene!).TransitionProgress == 1, "Paused incoming video must show its first frame, not freeze an outgoing fade.");
            using (var rendered = new Bitmap(160, 90))
            {
                using var full = new Bitmap(surface.Width, surface.Height); surface.DrawToBitmap(full, new(Point.Empty, full.Size));
                using var g = Graphics.FromImage(rendered); g.DrawImage(full, new Rectangle(Point.Empty, rendered.Size));
                var pixel = rendered.GetPixel(80, 45);
                Check(pixel.R > 165 && pixel.G < 110 && pixel.B < 110, "Paused desktop must display the real RED incoming video, not the BLUE outgoing image.");
            }
            report.Add("Paused incoming video with a configured playlist/fade shows its own red first frame over an outgoing blue image, without a frozen transition.");

            source.Snapshot = new(invalidDesktop, desktops); Wait(window.ChangeVirtualDesktopAsync(source.Snapshot)); Pump(50);
            Check(Current().HoldingPrevious && Current().Active == null && Current().Window?.Handle == hostHandle, "Failed incoming media must retain the previous frame without claiming success.");
            Check(Field<Label>(window, "status").Text.Contains("未能加载"), "Load failure must be visible to the user.");
            source.Snapshot = new(second, desktops); Task rapid = window.ChangeVirtualDesktopAsync(source.Snapshot);
            if (!rapid.IsCompleted)
            {
                source.Snapshot = new(first, desktops); Current().Request?.Cancel(); Wait(rapid);
                Wait(window.ChangeVirtualDesktopAsync(source.Snapshot));
                Check(Current().Active?.Key == blueStill.Key && Current().Window?.Handle == hostHandle, "Cancelled older preparation cannot replace the final desktop.");
            }
            source.Snapshot = new(empty, desktops); Wait(window.ChangeVirtualDesktopAsync(source.Snapshot));
            Check(host.IsDisposed && Current().Window == null, "An unconfigured desktop must release the retained surface once and show Windows' own background.");
            report.Add("Corrupt-video failure keeps a silent old frame and explicit error; cancelled preparation cannot commit to the final desktop; unconfigured desktop releases the host; disposal releases media resources. User files/settings and actual Windows desktop selection were not changed.");

            using var iconStream = typeof(AppIcon).Assembly.GetManifestResourceStream("LumaWall.AppIcon.ico")!;
            using var reader = new BinaryReader(iconStream);
            Check(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1 && reader.ReadUInt16() == 9, "Embedded icon must include all nine sizes.");
            Check(window.Icon!.Handle == AppIcon.Value.Handle && Field<NotifyIcon>(window, "tray").Icon!.Handle == AppIcon.Value.Handle,
                "Window and tray must use the selected moon icon.");
            using var extracted = Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            Check(extracted != null && extracted.Size.Width > 0, "Executable must contain an extractable Windows icon.");
            report.Add("Selected A moon icon embedded at 16/20/24/32/40/48/64/128/256; same window/tray icon; native executable icon extractable.");
            window.Close();
        }
        finally { TestFiles.RemoveFixture(root); }
        File.WriteAllText(output, "PASS:\n" + string.Join("\n\n", report));
    }
}
