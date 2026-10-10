using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LumaWall;

static class VideoTest
{
    static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static T Wait<T>(Task<T> task)
    {
        Wait((Task)task);
        return task.GetAwaiter().GetResult();
    }

    static void Wait(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed.TotalSeconds > 20) throw new TimeoutException("Video test timed out");
            Application.DoEvents(); Thread.Sleep(10);
        }
        task.GetAwaiter().GetResult();
    }

    static void PumpUntil(Func<bool> condition, string message, double seconds = 5)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException(message);
            Application.DoEvents(); Thread.Sleep(10);
        }
    }

    static Bitmap Capture(Scene scene)
    {
        var bitmap = new Bitmap(scene.Width, scene.Height);
        scene.DrawToBitmap(bitmap, new(Point.Empty, scene.Size));
        return bitmap;
    }

    static long Fingerprint(Bitmap bitmap)
    {
        long hash = 17;
        for (int y = 10; y < bitmap.Height; y += 20)
            for (int x = 10; x < bitmap.Width; x += 20)
                hash = unchecked(hash * 31 + bitmap.GetPixel(x, y).ToArgb());
        return hash;
    }

    public static void Run()
    {
        string folder = System.IO.Path.Combine(AppContext.BaseDirectory, "video-test-library");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, "native-test.mp4");
        Wait(Task.Run(() => VideoTestFixture.Create(path)));
        using (var poster = Wait(VideoMedia.GetPosterAsync(path)))
        {
            poster.Save(System.IO.Path.Combine(folder, "poster.png"));
            Assert(poster.Width == 320 && poster.Height == 180, $"Poster must contain the decoded video frame; received {poster.Width}×{poster.Height}");
            var red = poster.GetPixel(250, 120);
            Assert(red.R > 180 && red.B < 50, "Poster did not decode the first red frame");
            var green = poster.GetPixel(10, 10);
            Assert(green.G > 140 && green.R < 80, "Decoded frames are upside down or have invalid RGB channels");
            poster.Save(System.IO.Path.Combine(folder, "poster.png"));
        }

        using var scene = new Scene { Size = new(320, 180), Paused = true };
        scene.LoadScene(new("Native video", "video", path));
        Wait(scene.WaitUntilReadyAsync());
        Assert(scene.Ready && scene.Error == null, "Scene must be ready only after the first decoded frame");
        var playback = scene.Video!;
        using (var first = Capture(scene))
        {
            var color = first.GetPixel(250, 120);
            Assert(color.R > 180 && color.B < 50, "Scene.DrawToBitmap must capture real video pixels");
            Assert(color.A == 255, "Video snapshots must be opaque for cross-fade blending");
            long initial = Fingerprint(first), count = playback.FrameCount;
            Thread.Sleep(200); Application.DoEvents();
            using var paused = Capture(scene);
            Assert(Fingerprint(paused) == initial && playback.FrameCount == count, "Paused video must freeze both decoded output and displayed pixels");
            first.Save(System.IO.Path.Combine(folder, "first-frame.png"));
        }

        scene.SetTransform(new(.5f, .25f, 0));
        using (var moved = Capture(scene))
        {
            Assert(moved.GetPixel(80, 90).ToArgb() == Color.Black.ToArgb(), "Video transform must expose black outside the moved canvas");
            Assert(moved.GetPixel(280, 90).R > 180, "Video transforms must move the actual video frame");
        }
        scene.SetTransform(new());
        long beforePlaying = playback.FrameCount;
        scene.Paused = false;
        PumpUntil(() => playback.FrameCount > beforePlaying + 8, "Video did not start advancing");
        scene.Paused = true;
        using (var moving = Capture(scene)) moving.Save(System.IO.Path.Combine(folder, "moving-frame.png"));
        long frozen = playback.FrameCount;
        Thread.Sleep(200); Application.DoEvents();
        Assert(playback.FrameCount == frozen, "Pausing a playing video must stop publishing frames");
        scene.Paused = false;
        PumpUntil(() => playback.CurrentTimestamp >= 11000000, "The second second of the video was not reached");
        using (var blue = Capture(scene))
        {
            var color = blue.GetPixel(250, 120);
            Assert(color.B > 180 && color.R < 50, "Playing frames did not change from red to blue");
            blue.Save(System.IO.Path.Combine(folder, "blue-frame.png"));
        }
        PumpUntil(() => playback.LoopCount >= 1, "The video did not rewind and loop");
        scene.Paused = true;
        using (var loop = Capture(scene)) Assert(loop.GetPixel(250, 120).R > 180, "Looping must return to the first red frames");
        scene.ClearScene();
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert(exclusive.Length > 0, "Video decoder did not release the media file");

        for (int i = 0; i < 3; i++)
        {
            using var repeated = new Scene { Size = new(320, 180), Paused = true };
            repeated.LoadScene(new("Repeat", "video", path)); Wait(repeated.WaitUntilReadyAsync());
            using var screenshot = Capture(repeated);
            Assert(screenshot.GetPixel(250, 120).R > 180, "Repeated video decoder initialization failed");
        }
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

        string invalid = System.IO.Path.Combine(folder, "invalid.mp4"); File.WriteAllText(invalid, "not a video");
        bool rejected = false;
        try { using var unexpected = Wait(VideoMedia.GetPosterAsync(invalid)); }
        catch (NotSupportedException) { rejected = true; }
        Assert(rejected, "Invalid video import must be rejected before it enters the library");
        using (var bad = new Scene { Size = new(320, 180) })
        {
            bad.LoadScene(new("Invalid", "video", invalid));
            try { Wait(bad.WaitUntilReadyAsync()); throw new Exception("Invalid video was reported as ready"); }
            catch (InvalidOperationException) { Assert(!bad.Ready && bad.Error != null, "Failed video must expose readiness and error state"); }
        }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            bool cancelled = false;
            try { Wait(VideoMedia.GetPosterAsync(path, cancellation.Token)); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled, "Cancelled poster requests must not return a frame");
        }
        File.WriteAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "video-test.txt"),
            "PASS: Windows-native H.264 MP4 generated; real-frame poster and DrawToBitmap; RGB channels and orientation; zoom/move canvas; playback changes; pause/resume; seamless EOF rewind; decoder file release; repeated initialization; invalid-video errors; cancellation. Silent fixture has no audio track.");
    }
}

// Deterministic two-second H.264 fixture generated with the built-in Windows
// encoder, requiring neither downloaded assets nor ffmpeg.
static class VideoTestFixture
{
    static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71"),
        Bitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"),
        Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSinkWriterFromURL(string path, IntPtr stream, IntPtr attributes, out IntPtr writer);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(int size, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AddStream(IntPtr self, IntPtr type, out uint index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetInputType(IntPtr self, uint index, IntPtr type, IntPtr attributes);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int WriteSample(IntPtr self, uint index, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AddBuffer(IntPtr self, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetLength(IntPtr self, uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetTime(IntPtr self, long value);

    static void Format(IntPtr type, Guid subtype, int width, int height)
    {
        MfNative.SetGuid(type, MfNative.MajorType, MfNative.VideoType);
        MfNative.SetGuid(type, MfNative.Subtype, subtype);
        MfNative.SetUInt32(type, Interlace, 2);
        MfNative.SetUInt64(type, MfNative.FrameSize, (ulong)(uint)width << 32 | (uint)height);
        MfNative.SetUInt64(type, MfNative.FrameRate, 20UL << 32 | 1);
        MfNative.SetUInt64(type, PixelAspect, 1UL << 32 | 1);
    }

    public static void Create(string path, int width = 320, int height = 180)
    {
        bool initialized = false, started = false;
        IntPtr writer = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero;
        try
        {
            MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, 0)); initialized = true;
            MfNative.Check(MfNative.MFStartup(0x00020070, 0)); started = true;
            MfNative.Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out writer));
            MfNative.Check(MfNative.MFCreateMediaType(out output)); Format(output, H264, width, height);
            MfNative.SetUInt32(output, Bitrate, 800000);
            MfNative.Check(MfNative.Method<AddStream>(writer, 3)(writer, output, out uint stream));
            MfNative.Check(MfNative.MFCreateMediaType(out input)); Format(input, MfNative.Rgb32, width, height);
            MfNative.Check(MfNative.Method<SetInputType>(writer, 4)(writer, stream, input, IntPtr.Zero));
            MfNative.Check(MfNative.Method<MfNative.SimpleCall>(writer, 5)(writer));
            byte[] pixels = new byte[width * height * 4];
            for (int frame = 0; frame < 40; frame++)
            {
                for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
                {
                    int offset = ((height - 1 - y) * width + x) * 4;
                    bool green = x < 30 && y < 30;
                    bool moving = x >= (frame * 8 % 260) && x < (frame * 8 % 260) + 30 && y > 65 && y < 100;
                    pixels[offset] = (byte)(moving || frame >= 20 && !green ? 255 : 0);
                    pixels[offset + 1] = (byte)(green || moving ? 255 : 0);
                    pixels[offset + 2] = (byte)(moving || frame < 20 && !green ? 255 : 0);
                }
                IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
                bool locked = false;
                try
                {
                    MfNative.Check(MFCreateSample(out sample)); MfNative.Check(MFCreateMemoryBuffer(pixels.Length, out buffer));
                    MfNative.Check(MfNative.Method<MfNative.BufferLock>(buffer, 3)(buffer, out IntPtr destination, out _, out _)); locked = true;
                    Marshal.Copy(pixels, 0, destination, pixels.Length);
                    MfNative.Check(MfNative.Method<MfNative.SimpleCall>(buffer, 4)(buffer)); locked = false;
                    MfNative.Check(MfNative.Method<SetLength>(buffer, 6)(buffer, (uint)pixels.Length));
                    MfNative.Check(MfNative.Method<AddBuffer>(sample, 42)(sample, buffer));
                    MfNative.Check(MfNative.Method<SetTime>(sample, 36)(sample, frame * 500000L));
                    MfNative.Check(MfNative.Method<SetTime>(sample, 38)(sample, 500000L));
                    MfNative.Check(MfNative.Method<WriteSample>(writer, 6)(writer, stream, sample));
                }
                finally
                {
                    if (locked) MfNative.Method<MfNative.SimpleCall>(buffer, 4)(buffer);
                    MfNative.Release(ref buffer); MfNative.Release(ref sample);
                }
            }
            MfNative.Check(MfNative.Method<MfNative.SimpleCall>(writer, 11)(writer));
        }
        finally
        {
            MfNative.Release(ref input); MfNative.Release(ref output); MfNative.Release(ref writer);
            if (started) MfNative.MFShutdown(); if (initialized) MfNative.CoUninitialize();
        }
    }
}
