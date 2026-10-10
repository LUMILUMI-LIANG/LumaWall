using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace LumaWall;

static class PreviewPerformanceTest
{
    static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    static T Field<T>(MainWindow window, string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
    static void Set(MainWindow window, string name, object value) => typeof(MainWindow).GetField(name, Private)!.SetValue(window, value);
    static void Call(MainWindow window, string name) => typeof(MainWindow).GetMethod(name, Private)!.Invoke(window, null);
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void Until(Func<bool> condition, string message, double timeoutSeconds = 20)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed.TotalSeconds > timeoutSeconds) throw new TimeoutException(message);
            Thread.Sleep(5);
        }
    }
    static void Ready(Scene scene)
    {
        Task ready = scene.WaitUntilReadyAsync();
        Until(() => ready.IsCompleted, "Preview readiness timed out.");
        ready.GetAwaiter().GetResult();
    }
    public static void Run()
    {
        // All scenes remain unshown. This test verifies decode size and clock
        // suspension rather than creating another desktop rendering workload.
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "preview-fixtures-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "native-720p.mp4");
        try
        {
            Task.Run(() => PerformanceFixture.Create(path, 1280, 720, 30, 3)).GetAwaiter().GetResult();
            var wallpaper = new Wallpaper("Preview fixture", "video", path);
            using (var preview = new Scene { VideoMaximumWidth = 640, RenderingSuspended = true, Size = new(400, 225) })
            using (var desktop = new Scene { Paused = true, Size = new(1280, 720) })
            {
                preview.LoadScene(wallpaper); desktop.LoadScene(wallpaper);
                Ready(preview); Ready(desktop);
                var previewVideo = preview.Video!; var desktopVideo = desktop.Video!;
                using (var small = previewVideo.CopyFrame()!)
                using (var full = desktopVideo.CopyFrame()!)
                {
                    Check(small.Size == new Size(640, 360), $"Preview must decode actual scaled 640x360 pixels, received {small.Size}.");
                    Check(full.Size == new Size(1280, 720), $"Desktop decode must retain source resolution, received {full.Size}.");
                }
                Check(previewVideo.DurationTicks == desktopVideo.DurationTicks && previewVideo.FrameDuration == desktopVideo.FrameDuration,
                    "XVP resizing must preserve timestamps, frame duration and the clip's playback period.");
                Check(!preview.AudioEnabled && !previewVideo.AudioEnabled && !preview.Paused && previewVideo.Clock.IsPaused,
                    "Hidden preview must stay silent and suspend its own clock without changing the user's pause preference.");
                long frozenFrames = previewVideo.FrameCount, frozenClock = previewVideo.Clock.PositionTicks;
                desktop.Paused = false;
                Until(() => desktopVideo.FrameCount > 10, "Suspended preview blocked desktop playback.");
                Check(previewVideo.FrameCount == frozenFrames && previewVideo.Clock.PositionTicks == frozenClock,
                    "Suspended preview must stop decoding/publishing while desktop continues.");
                Check(!desktopVideo.Clock.IsPaused && desktopVideo.Clock.PositionTicks > 1000000,
                    "Preview suspension must not pause or reset the desktop's independent clock.");

                preview.RenderingSuspended = false;
                Until(() => previewVideo.FrameCount > frozenFrames + 8, "Preview did not resume after becoming visible.");
                Check(!previewVideo.Clock.IsPaused && previewVideo.Error is null && desktopVideo.Error is null,
                    "Scaled preview must resume without decoder or shared-clock errors.");
                preview.Paused = true;
                preview.RenderingSuspended = true;
                preview.RenderingSuspended = false;
                frozenFrames = previewVideo.FrameCount; frozenClock = previewVideo.Clock.PositionTicks;
                long desktopBefore = desktopVideo.FrameCount;
                Thread.Sleep(180);
                Check(preview.Paused && previewVideo.Clock.IsPaused && previewVideo.FrameCount == frozenFrames &&
                    previewVideo.Clock.PositionTicks == frozenClock && desktopVideo.FrameCount > desktopBefore,
                    "Restoring visibility must preserve explicit pause and keep desktop playback running.");
                preview.Paused = false;
                Until(() => previewVideo.FrameCount > frozenFrames + 3, "Explicit preview resume failed.");
                preview.ClearScene();
                Check(preview.Video is null && !preview.Ready, "Clearing a scaled video must unload its reader and readiness state.");

                preview.LoadVideoPreview(wallpaper, desktopVideo);
                Check(preview.IsSharedVideo && ReferenceEquals(preview.Video, desktopVideo),
                    "Borrowed preview must share the desktop's actual decoder instead of creating another playback instance.");
                using (var sharedPixels = preview.Video!.CopyFrame()!)
                    Check(sharedPixels.Size == new Size(1280, 720), "A shared preview must draw the owner's full decoded frame.");
                float ownerVolume = desktopVideo.Volume;
                bool ownerSound = desktopVideo.AudioEnabled;
                preview.AudioEnabled = true; preview.Volume = 0; preview.Paused = true; preview.RenderingSuspended = true;
                long ownerFrames = desktopVideo.FrameCount, ownerClock = desktopVideo.Clock.PositionTicks;
                Thread.Sleep(180);
                Check(!preview.AudioEnabled && desktopVideo.AudioEnabled == ownerSound && desktopVideo.Volume == ownerVolume &&
                    !desktopVideo.Paused && desktopVideo.FrameCount > ownerFrames && desktopVideo.Clock.PositionTicks > ownerClock,
                    "Borrowed preview pause, hide, volume and audio controls must not change the owner or enable a second soundtrack.");
                preview.RenderingSuspended = false; preview.Paused = false;
                Check(ReferenceEquals(preview.Video, desktopVideo) && !desktopVideo.Paused,
                    "Restoring a shared preview must reuse the owner without changing its clock.");
                preview.ClearScene(); ownerFrames = desktopVideo.FrameCount;
                Thread.Sleep(120);
                Check(!preview.IsSharedVideo && preview.Video is null && desktopVideo.FrameCount > ownerFrames && desktopVideo.Error is null,
                    "Detaching a borrowed preview must not dispose its owner.");
                preview.LoadScene(wallpaper); Ready(preview);
                Check(!preview.IsSharedVideo && !ReferenceEquals(preview.Video, desktopVideo),
                    "After detaching, preview must be able to prepare an independent owned decoder.");
                using (var reloaded = preview.Video!.CopyFrame()!)
                    Check(reloaded.Size == new Size(640, 360), "Independent preview reload must restore its decode-size limit.");
                preview.ClearScene();

                // Exercise repeated Control.Dispose after its native scheduler
                // exists, without showing/painting an actual window.
                Type delayType = typeof(NativeVideoSurface).GetNestedType("HighResolutionDelay", BindingFlags.NonPublic)!;
                object scheduler = Activator.CreateInstance(delayType, new object[] { (Action)(() => { }) })!;
                typeof(NativeVideoSurface).GetField("deferred", Private)!.SetValue(preview, scheduler);
                preview.Dispose(); preview.Dispose();
            }
            using (var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(file.Length > 0, "Suspended/scaled scenes must release their media on unload/dispose.");

            // A borrowed preview has no sound device of its own. The real native
            // owner's device must keep advancing through preview hide/dispose.
            string tone = Path.Combine(root, "quiet-owner-audio.mp4");
            AudioTestFixture.Create(tone, true);
            using (var owner = new Scene { AudioEnabled = true, Volume = .06f, Size = new(128, 80) })
            using (var borrowed = new Scene { VideoMaximumWidth = 640, Size = new(400, 225) })
            {
                var item = new Wallpaper("Quiet native sound owner", "video", tone);
                owner.LoadScene(item); Ready(owner);
                var source = owner.Video!;
                Until(() => source.Audio?.Ready.IsCompleted == true, "Owner's original soundtrack did not initialize.");
                var audio = source.Audio!;
                audio.Ready.GetAwaiter().GetResult();
                Until(() => audio.DevicePlayedFrames > 1200, "Real owner sound device did not advance.");
                Check(source.HasAudio && source.AudioEnabled && audio.Error is null,
                    "The native owner must have an enabled, decoded soundtrack.");
                borrowed.LoadVideoPreview(item, source);
                borrowed.Paused = true; borrowed.RenderingSuspended = true; borrowed.AudioEnabled = true; borrowed.Volume = 0;
                long beforeSound = audio.DevicePlayedFrames, beforeFrame = source.FrameCount;
                Thread.Sleep(180);
                Check(audio.DevicePlayedFrames > beforeSound && source.FrameCount > beforeFrame &&
                    source.AudioEnabled && Math.Abs(source.Volume - .06f) < .0001f && !borrowed.AudioEnabled,
                    "Hiding or muting a borrowed preview must preserve the owner's actual original-sound output.");
                borrowed.Dispose(); beforeSound = audio.DevicePlayedFrames; beforeFrame = source.FrameCount;
                Thread.Sleep(180);
                Check(audio.DevicePlayedFrames > beforeSound && source.FrameCount > beforeFrame && source.Error is null,
                    "Disposing a borrowed preview must leave both owner frames and native sound playing.");
            }
            using (var file = new FileStream(tone, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(file.Length > 0, "Disposing the audio owner must finally release its original media.");

            // A newly constructed but unshown library has no VisibleChanged
            // event yet; its first selected video must already be suspended.
            string settingsFolder = Path.Combine(root, "library"); Directory.CreateDirectory(settingsFolder);
            File.WriteAllText(Path.Combine(settingsFolder, "settings.json"), JsonSerializer.Serialize(new Settings { Library = [wallpaper] }));
            using (var hidden = new MainWindow(settingsFolder, false))
            {
                var scene = (Scene)typeof(MainWindow).GetField("preview", Private)!.GetValue(hidden)!;
                Check(!hidden.Visible && scene.RenderingSuspended,
                    "An initially hidden library must suspend its preview before the first Show event.");
                var library = (ThumbnailLibrary)typeof(MainWindow).GetField("library", Private)!.GetValue(hidden)!;
                library.SelectedIndex = 3;
                Ready(scene);
                long frameCount = scene.Video!.FrameCount, position = scene.Video.Clock.PositionTicks;
                Thread.Sleep(150);
                Check(scene.Video.FrameCount == frameCount && scene.Video.Clock.PositionTicks == position && !scene.AudioEnabled,
                    "An unshown library must not run an imported preview in the background or enable sound.");

                // Exercise the application's real sharing/selection/Stop wiring
                // without showing or attaching a Form to Explorer's desktop.
                using var ownerWindow = new Form { ShowInTaskbar = false };
                using var owner = new Scene { Size = new(1280, 720) };
                ownerWindow.Controls.Add(owner); owner.LoadScene(wallpaper); Ready(owner);
                var source = owner.Video!;
                Set(hidden, "desktopWindow", ownerWindow); Set(hidden, "desktopScene", owner); Set(hidden, "activeWallpaper", wallpaper);
                Call(hidden, "UpdatePreviewPlaybackSource");
                Check(scene.IsSharedVideo && ReferenceEquals(scene.Video, source) && scene.RenderingSuspended && !source.Paused,
                    "MainWindow must rebind a selected video's hidden preview to the active owner without pausing that owner.");
                library.SelectedIndex = 0;
                Check(!scene.IsSharedVideo && scene.Video is null, "Selecting a built-in must detach the previous shared video.");
                using (var alive = source.CopyFrame())
                    Check(alive is not null && !source.Paused, "Selecting another wallpaper must leave the desktop owner alive.");
                library.SelectedIndex = 3; Ready(scene);
                Check(scene.IsSharedVideo && ReferenceEquals(scene.Video, source) && !scene.AudioEnabled,
                    "Selecting the active video again must share the same decoder and remain silent.");
                Call(hidden, "Stop"); Ready(scene);
                Check(!scene.IsSharedVideo && !ReferenceEquals(scene.Video, source) && scene.RenderingSuspended &&
                    scene.Video!.Clock.IsPaused && !scene.AudioEnabled && !scene.Video.AudioEnabled,
                    "Stop must detach and prepare an independent suspended silent preview before releasing the old owner.");
                using (var reloaded = scene.Video!.CopyFrame()!)
                    Check(reloaded.Size == new Size(640, 360), "MainWindow Stop must restore the independent preview's decode limit.");
                using (var released = source.CopyFrame())
                    Check(released is null && ownerWindow.IsDisposed && owner.IsDisposed,
                        "Stop must finally dispose the old desktop owner and release its frame storage.");
                Check(Field<Scene?>(hidden, "desktopScene") is null && Field<Form?>(hidden, "desktopWindow") is null &&
                    Field<Wallpaper?>(hidden, "activeWallpaper") is null, "Stop must clear all active owner references.");
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "preview-performance-test.txt"),
                "PASS: real 720p fixture; decoded preview 640x360 and desktop 1280x720; preserved media timestamps; silent hidden owned preview; independent desktop clock; visibility resume and explicit pause; borrowed single-decoder reference; preview controls preserve owner; detach/owned reload; actual original-sound output survives borrowed hide/dispose; final media release; native scheduler disposal idempotence; initially hidden library suspension; actual MainWindow active-video sharing, selection detach/rebind, and Stop ordering with independent640 reload and old-owner disposal.");
        }
        finally
        {
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            if (Path.GetDirectoryName(root) != parent || !Path.GetFileName(root).StartsWith("preview-fixtures-", StringComparison.Ordinal) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to delete an unexpected preview fixture directory.");
            string settingsFolder = Path.Combine(root, "library");
            if (Directory.Exists(settingsFolder))
            {
                foreach (string file in Directory.GetFiles(settingsFolder)) File.Delete(file);
                Directory.Delete(settingsFolder);
            }
            foreach (string file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }
}
