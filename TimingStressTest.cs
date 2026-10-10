using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LumaWall;

static class TimingStressTest
{
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    static void Until(Func<bool> condition, VideoPlayback playback, string message, double seconds = 8)
    {
        var wait = Stopwatch.StartNew();
        while (!condition())
        {
            if (playback.Error is { } failure) throw failure;
            if (wait.Elapsed.TotalSeconds > seconds) throw new TimeoutException(message);
            Thread.Sleep(5);
        }
    }
    public static void Run()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "timing-fixtures-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var results = new List<string>();
        try
        {
            foreach (int fps in new[] { 30, 60 })
            {
                string path = Path.Combine(root, $"long-gop-{fps}.mp4");
                Task.Run(() => LongGopFixture.Create(path, fps)).GetAwaiter().GetResult();
                long seekTimestamp = Task.Run(() =>
                {
                    using var decoder = new VideoDecoder(path);
                    using var first = decoder.ReadFrame(default) ?? throw new InvalidDataException("Long-GOP fixture has no frames.");
                    Check(Math.Abs(decoder.DurationTicks - 100000000) < 1000000 && Math.Abs(decoder.FrameDuration - 10000000L / fps) < 1000,
                        "Fixture must contain a real ten-second video at the requested frame rate.");
                    decoder.Seek(30000000);
                    using var afterSeek = decoder.ReadFrame(default) ?? throw new InvalidDataException("Long-GOP seek returned no frame.");
                    return afterSeek.Timestamp;
                }).GetAwaiter().GetResult();
                // Setting an encoder property is not enough: prove that this
                // encoded stream really lands far before a requested seek.
                Check(seekTimestamp < 10000000,
                    $"Fixture did not create a long GOP: seek to3s returned {seekTimestamp / 10000000.0:F3}s.");
                results.Add($"{fps}fps native long GOP verified: seek request=3.000s; actual frame={seekTimestamp / 10000000.0:F3}s.");
                using var playback = new VideoPlayback(path, true);
                Until(() => playback.Ready.IsCompleted, playback, "Long-GOP initial decode timed out.");
                playback.Ready.GetAwaiter().GetResult();
                // Force sustained decode capacity below the source frame rate.
                // This tests native timestamps/seek behavior through the real
                // playback scheduler, not a mock or a slow UI renderer.
                playback.DecodeDelayMilliseconds = fps == 30 ? 70 : 40;
                long startFrames = playback.FrameCount, lastFrames = startFrames;
                double lastChange = 0, maximumGap = 0;
                playback.Paused = false;
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed.TotalSeconds < 4.2)
                {
                    if (playback.Error is { } failure) throw failure;
                    double now = watch.Elapsed.TotalSeconds;
                    long count = playback.FrameCount;
                    if (count != lastFrames)
                    {
                        maximumGap = Math.Max(maximumGap, now - lastChange);
                        lastFrames = count; lastChange = now;
                    }
                    Thread.Sleep(5);
                }
                maximumGap = Math.Max(maximumGap, watch.Elapsed.TotalSeconds - lastChange);
                Check(Math.Abs(playback.Clock.PositionTicks / 10000000.0 - watch.Elapsed.TotalSeconds) < .15,
                    "Overloaded decoding must not stretch the 1x master clock.");
                Check(playback.FrameCount - startFrames >= 12 && maximumGap < .75,
                    $"Slow decoding must keep the wallpaper moving; fps={fps}, updates={playback.FrameCount - startFrames}, longest freeze={maximumGap:F3}s.");
                Check(playback.SeekCount == 0,
                    $"A steadily advancing slow decoder must not be sent backwards by seeking; fps={fps}, seeks={playback.SeekCount}.");
                double overloadLag = (playback.Clock.PositionTicks - playback.PresentedPositionTicks) / 10000000.0;
                results.Add($"{fps}fps overloaded: updates={playback.FrameCount - startFrames}; longest gap={maximumGap:F3}s; lag={overloadLag:F3}s; seeks={playback.SeekCount}; dropped={playback.DroppedFrameCount}.");
                // Separately model a native call blocking for a full second.
                // This warrants one seek, followed by uninterrupted GOP decode.
                playback.DecodeDelayMilliseconds = 1000;
                Until(() => playback.SeekCount >= 1, playback, "A major native decoder stall did not enter recovery.", 5);
                long recoverySeekCount = playback.SeekCount;
                playback.DecodeDelayMilliseconds = 0;
                var recovery = Stopwatch.StartNew();
                Until(() => playback.Clock.PositionTicks - playback.PresentedPositionTicks < 2500000,
                    playback, "A recovered decoder did not catch up through the long GOP.", 6);
                Check(playback.SeekCount == recoverySeekCount,
                    "Recovering a long GOP must not restart seeking before the sequential decoder catches up.");
                results.Add($"{fps}fps catch-up: {recovery.Elapsed.TotalSeconds:F3}s; seeks unchanged={playback.SeekCount}; gap={(playback.Clock.PositionTicks - playback.PresentedPositionTicks) / 10000000.0:F3}s.");
                playback.Paused = true;
                long frozen = playback.FrameCount, frozenClock = playback.Clock.PositionTicks;
                Thread.Sleep(180);
                Check(playback.FrameCount == frozen && playback.Clock.PositionTicks == frozenClock,
                    "Late-frame rescue must still respect pause.");
                playback.Paused = false;
                Until(() => playback.LoopCount >= 1 && playback.CurrentTimestamp < 10000000,
                    playback, "A long-GOP clip did not restart at its container duration.", 10);
                Check(playback.Clock.PositionTicks >= 100000000 && playback.Error is null,
                    "Looping must preserve the continuous clock and recover to first-cycle frames.");
                results.Add($"{fps}fps loop: clock={playback.Clock.PositionTicks / 10000000.0:F3}s; timestamp={playback.CurrentTimestamp / 10000000.0:F3}s; loops={playback.LoopCount}.");
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "timing-stress-test.txt"),
                "PASS: verified native long-GOP seeks; real 30/60fps files; injected native decoder overload; bounded frozen interval; no repeated seek rollback; real-time clock; sequential recovery; pause; long-GOP loop.\n" + string.Join("\n", results));
        }
        finally
        {
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            if (Path.GetDirectoryName(root) != parent || !Path.GetFileName(root).StartsWith("timing-fixtures-", StringComparison.Ordinal) ||
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to delete an unexpected timing fixture directory.");
            foreach (string file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }

    // Optional diagnostics for a real user's clip. The decoder only reads the
    // file; it does not modify, copy, or delete the supplied media.
    public static string InspectFile(string path) => Task.Run(() =>
    {
        using var decoder = new VideoDecoder(path);
        using var first = decoder.ReadFrame(default) ?? throw new InvalidDataException("No video frame.");
        long seek = Math.Min(30000000, Math.Max(0, decoder.DurationTicks / 2));
        var elapsed = Stopwatch.StartNew(); decoder.Seek(seek);
        using var afterSeek = decoder.ReadFrame(default) ?? throw new InvalidDataException("Seek returned no video frame.");
        return $"{first.Bitmap.Width}x{first.Bitmap.Height}; fps={10000000.0 / decoder.FrameDuration:F3}; duration={decoder.DurationTicks / 10000000.0:F3}s; first={first.Timestamp / 10000000.0:F3}s; seek request={seek / 10000000.0:F3}s, actual={afterSeek.Timestamp / 10000000.0:F3}s, seek+decode={elapsed.Elapsed.TotalMilliseconds:F1}ms.";
    }).GetAwaiter().GetResult();
}

static class LongGopFixture
{
    static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71"),
        Bitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSinkWriterFromURL(string path, IntPtr stream, IntPtr attributes, out IntPtr writer);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(int size, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AddStream(IntPtr self, IntPtr type, out uint index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetInputType(IntPtr self, uint index, IntPtr type, IntPtr attributes);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int WriteSample(IntPtr self, uint index, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int AddBuffer(IntPtr self, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetLength(IntPtr self, uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int SetTime(IntPtr self, long value);
    static void Format(IntPtr type, Guid subtype, int fps)
    {
        MfNative.SetGuid(type, MfNative.MajorType, MfNative.VideoType); MfNative.SetGuid(type, MfNative.Subtype, subtype);
        MfNative.SetUInt32(type, Interlace, 2); MfNative.SetUInt64(type, MfNative.FrameSize, 640UL << 32 | 360);
        MfNative.SetUInt64(type, MfNative.FrameRate, (ulong)(uint)fps << 32 | 1); MfNative.SetUInt64(type, PixelAspect, 1UL << 32 | 1);
    }
    public static void Create(string path, int fps)
    {
        bool initialized = false, started = false;
        IntPtr writer = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero, parameters = IntPtr.Zero;
        try
        {
            MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, 0)); initialized = true;
            MfNative.Check(MfNative.MFStartup(0x00020070, 0)); started = true;
            MfNative.Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out writer));
            MfNative.Check(MfNative.MFCreateMediaType(out output)); Format(output, H264, fps); MfNative.SetUInt32(output, Bitrate, 5000000);
            MfNative.Check(MfNative.Method<AddStream>(writer, 3)(writer, output, out uint stream));
            MfNative.Check(MfNative.MFCreateMediaType(out input)); Format(input, MfNative.Rgb32, fps);
            MfNative.Check(MfNative.MFCreateAttributes(out parameters, 1)); MfNative.SetUInt32(parameters, GopSize, (uint)(fps * 8));
            MfNative.Check(MfNative.Method<SetInputType>(writer, 4)(writer, stream, input, parameters));
            MfNative.Check(MfNative.Method<MfNative.SimpleCall>(writer, 5)(writer));
            byte[] pixels = new byte[640 * 360 * 4];
            // A textured moving landscape rather than a flat solid-color frame.
            for (int frame = 0; frame < fps * 10; frame++)
            {
                for (int y = 0; y < 360; y++) for (int x = 0; x < 640; x++)
                {
                    int offset = ((359 - y) * 640 + x) * 4;
                    int motion = x + frame * 2;
                    pixels[offset] = (byte)(40 + (motion + y) % 100);
                    pixels[offset + 1] = (byte)(60 + (motion / 7 + y / 3) % 130);
                    pixels[offset + 2] = (byte)(40 + (motion / 11 + y / 5) % 150);
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
                    long timestamp = frame * 10000000L / fps, duration = (frame + 1) * 10000000L / fps - timestamp;
                    MfNative.Check(MfNative.Method<SetTime>(sample, 36)(sample, timestamp)); MfNative.Check(MfNative.Method<SetTime>(sample, 38)(sample, duration));
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
            MfNative.Release(ref parameters); MfNative.Release(ref input); MfNative.Release(ref output); MfNative.Release(ref writer);
            if (started) MfNative.MFShutdown(); if (initialized) MfNative.CoUninitialize();
        }
    }
}
