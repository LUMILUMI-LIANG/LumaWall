using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LumaWall;

static class AudioTest
{
    static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    static void Until(Func<bool> condition, string message, AudioPlayback? audio = null)
    {
        var wait = Stopwatch.StartNew();
        while (!condition())
        {
            if (audio?.Error is { } failure) throw failure;
            if (wait.Elapsed.TotalSeconds > 8) throw new TimeoutException(message);
            Thread.Sleep(10);
        }
    }
    static void Ready(AudioPlayback audio)
    {
        Until(() => audio.Ready.IsCompleted, "Audio reader initialization timed out.", audio);
        audio.Ready.GetAwaiter().GetResult();
    }
    internal static void Wait(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (watch.Elapsed.TotalSeconds > 30) throw new TimeoutException("Native media preparation timed out.");
            Application.DoEvents(); Thread.Sleep(5);
        }
        task.GetAwaiter().GetResult();
    }
    public static void Run()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "audio-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string tone = Path.Combine(root, "native-av-tone.mp4");
        string silent = Path.Combine(root, "native-silent.mp4");
        string invalid = Path.Combine(root, "invalid.mp4");
        try
        {
            AudioTestFixture.Create(tone, true);
            AudioTestFixture.Create(silent, false);
            var durationTask = Task.Run(() =>
            {
                using var decoder = new AudioDecoder(tone);
                Check(decoder.HasAudio && decoder.DurationTicks > 0, "Native AAC track must expose a duration.");
                AudioChunk first = decoder.Read(default) ?? throw new InvalidDataException("AAC fixture decoded no samples.");
                Check(first.Data.Any(value => value != 0), "AAC fixture must decode real nonzero PCM samples.");
                return decoder.DurationTicks;
            });
            Wait(durationTask); long duration = durationTask.GetAwaiter().GetResult();
            var clock = new PlaybackClock(true);
            using (var audio = new AudioPlayback(tone, clock, duration) { Volume = .12f })
            {
                Ready(audio);
                Check(audio.HasAudio && !audio.Enabled && audio.Error is null, "Valid audio must initialize silently.");
                clock.Start(); Thread.Sleep(100);
                Check(audio.SubmittedFrames == 0 && audio.DevicePlayedFrames == 0,
                    "Preview must not submit any audio to a sound device.");
                audio.Enabled = true; Thread.Sleep(60);
                Check(audio.SubmittedFrames == 0, "A paused candidate must remain silent after enabling.");
                clock.SetPaused(false);
                Until(() => audio.DevicePlayedFrames > AudioDecoder.SampleRate / 4, "Native sound device position did not advance.", audio);
                Check(audio.MaximumQueuedTicks > 0 && audio.MaximumQueuedTicks <= 1500000,
                    $"Queued sound must remain below 150ms; received {audio.MaximumQueuedTicks / 10000.0:F1}ms.");
                Check(Math.Abs(clock.PositionTicks - audio.CurrentPositionTicks) < 1500000,
                    "Native audio position must follow the shared media clock.");
                clock.SetPaused(true);
                Until(() => audio.OutputPaused, "Sound device did not pause.", audio);
                Thread.Sleep(80); // A driver may finish its current 20ms block.
                long frozen = audio.DevicePlayedFrames, frozenClock = clock.PositionTicks;
                Thread.Sleep(160);
                Check(audio.DevicePlayedFrames - frozen <= AudioDecoder.SampleRate / 50 && clock.PositionTicks == frozenClock,
                    "Pause must freeze both native device position and the shared clock.");
                clock.SetPaused(false);
                Until(() => audio.DevicePlayedFrames > frozen + AudioDecoder.SampleRate / 5, "Sound device did not resume.", audio);
                Until(() => audio.LoopCount >= 2, "Audio did not repeat according to the shared clock.", audio);
                Check(Math.Abs(clock.PositionTicks - audio.CurrentPositionTicks) < 1500000 && audio.Error is null,
                    "Looping audio must stay synchronized without accumulating old sound.");
                audio.Enabled = false;
                Until(() => audio.QueuedTicks == 0 && audio.OutputPaused, "Disabled output did not empty its queue.", audio);
                long disabledSubmitted = audio.SubmittedFrames;
                Thread.Sleep(220);
                Check(audio.SubmittedFrames == disabledSubmitted, "Disabled preview must not decode and queue sound continuously.");
                audio.Enabled = true;
                Until(() => audio.SubmittedFrames > disabledSubmitted + AudioDecoder.SampleRate / 10,
                    "Enabled audio did not seek to the advancing video timeline.", audio);
                Thread.Sleep(80);
                Check(Math.Abs(clock.PositionTicks - audio.CurrentPositionTicks) < 1500000,
                    "Re-enabled audio must catch up to the current cycle instead of restarting the soundtrack.");
                audio.Volume = 0;
                Check(audio.Volume == 0, "Mute must use local PCM gain.");
                audio.Volume = 4;
                Check(audio.Volume == 1, "Volume must clamp to the supported range.");
                audio.Enabled = false;
            }
            using (var exclusive = new FileStream(tone, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(exclusive.Length > 0, "Disposed audio must release its source reader and file.");

            using (var video = new VideoPlayback(tone, true) { AudioEnabled = true, Volume = .08f })
            {
                Wait(video.Ready);
                Until(() => video.AudioReady, "Combined video/audio initialization did not complete.");
                Check(video.HasAudio && video.AudioError == null && video.Clock.PositionTicks == 0,
                    "Prepared wallpaper must retain its original audio without advancing the paused timeline.");
                video.Paused = false;
                Until(() => video.Audio?.DevicePlayedFrames > AudioDecoder.SampleRate / 3,
                    "Combined video/audio playback did not emit original sound.", video.Audio);
                Check(Math.Abs(video.Clock.PositionTicks - video.PresentedPositionTicks) < 1500000
                    && Math.Abs(video.Clock.PositionTicks - video.Audio!.CurrentPositionTicks) < 1500000,
                    "Displayed video and native audio must follow the same timeline.");
                Until(() => video.LoopCount >= 2 && video.Audio!.LoopCount >= 2,
                    "Combined wallpaper did not loop both picture and original sound.", video.Audio);
                video.Paused = true;
                Until(() => video.Audio!.OutputPaused, "Combined wallpaper pause did not stop original sound.", video.Audio);
                long frozenFrame = video.FrameCount, frozenTime = video.Clock.PositionTicks;
                Thread.Sleep(120);
                Check(video.FrameCount == frozenFrame && video.Clock.PositionTicks == frozenTime && video.AudioError == null,
                    "Pausing the wallpaper must freeze video and the shared audio timeline.");
            }
            using (var exclusive = new FileStream(tone, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

            using (var noTrack = new AudioPlayback(silent, new PlaybackClock(), duration) { Enabled = true })
            {
                Ready(noTrack);
                Check(!noTrack.HasAudio && noTrack.Error is null && noTrack.SubmittedFrames == 0,
                    "A video without audio is a normal silent success.");
            }
            File.WriteAllText(invalid, "invalid media fixture");
            using (var damaged = new AudioPlayback(invalid, new PlaybackClock(), duration))
            {
                Until(() => damaged.Ready.IsCompleted, "Invalid audio initialization did not complete.");
                bool failed = false;
                try { damaged.Ready.GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { failed = true; }
                Check(failed && damaged.Error is not null, "Decode failure must be reported as an error, not silently treated as no audio.");
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "audio-test.txt"),
                "PASS: native H.264/AAC fixture and PCM decoding; silent preview; paused preparation; real waveOut device position advances; queue <150ms; shared-clock synchronization; pause/resume native position; two loops; disabled queue cleanup and seek catch-up; local volume/mute; source release; no-audio silent success; damaged media errors.");
        }
        finally
        {
            string target = Path.GetFullPath(root);
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
            if (Path.GetDirectoryName(target) != parent || !Path.GetFileName(target).StartsWith("audio-fixtures-", StringComparison.Ordinal) ||
                (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Refusing to delete an unexpected audio fixture directory.");
            foreach (string file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root);
        }
    }
}

// A quiet one-second tone and small real H.264 picture, encoded with built-in
// Windows encoders. No downloaded clips, additional codecs, or user media.
static class AudioTestFixture
{
    static readonly Guid Aac = new("00001610-0000-0010-8000-00aa00389b71"), H264 = new("34363248-0000-0010-8000-00aa00389b71"),
        Bitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
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
    static void VideoFormat(IntPtr type, Guid subtype)
    {
        MfNative.SetGuid(type, MfNative.MajorType, MfNative.VideoType); MfNative.SetGuid(type, MfNative.Subtype, subtype);
        MfNative.SetUInt32(type, Interlace, 2); MfNative.SetUInt64(type, MfNative.FrameSize, 128UL << 32 | 80);
        MfNative.SetUInt64(type, MfNative.FrameRate, 20UL << 32 | 1); MfNative.SetUInt64(type, PixelAspect, 1UL << 32 | 1);
    }
    public static void Create(string path, bool sound = true)
    {
        // MTA confinement also makes calling this helper from the UI test's STA safe.
        AudioTest.Wait(Task.Run(() => CreateOnMta(path, sound)));
    }
    static void CreateOnMta(string path, bool sound)
    {
        bool initialized = false, started = false;
        IntPtr writer = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero;
        try
        {
            MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, 0)); initialized = true;
            MfNative.Check(MfNative.MFStartup(0x00020070, 0)); started = true;
            MfNative.Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out writer));
            MfNative.Check(MfNative.MFCreateMediaType(out output)); VideoFormat(output, H264); MfNative.SetUInt32(output, Bitrate, 300000);
            MfNative.Check(MfNative.Method<AddStream>(writer, 3)(writer, output, out uint video));
            MfNative.Check(MfNative.MFCreateMediaType(out input)); VideoFormat(input, MfNative.Rgb32);
            MfNative.Check(MfNative.Method<SetInputType>(writer, 4)(writer, video, input, IntPtr.Zero));
            MfNative.Release(ref input); MfNative.Release(ref output);
            uint audio = 0;
            if (sound)
            {
                MfNative.Check(MfNative.MFCreateMediaType(out output));
                MfNative.SetGuid(output, MfNative.MajorType, AudioNative.AudioType); MfNative.SetGuid(output, MfNative.Subtype, Aac);
                MfNative.SetUInt32(output, AudioNative.AudioChannels, 2); MfNative.SetUInt32(output, AudioNative.AudioRate, 48000);
                MfNative.SetUInt32(output, AudioNative.AudioBits, 16); MfNative.SetUInt32(output, AudioNative.AudioAverageBytes, 16000);
                MfNative.Check(MfNative.Method<AddStream>(writer, 3)(writer, output, out audio));
                MfNative.Check(MfNative.MFCreateMediaType(out input)); AudioNative.PcmFormat(input);
                MfNative.Check(MfNative.Method<SetInputType>(writer, 4)(writer, audio, input, IntPtr.Zero));
            }
            MfNative.Check(MfNative.Method<MfNative.SimpleCall>(writer, 5)(writer));
            byte[] pixels = new byte[128 * 80 * 4], pcm = new byte[2400 * 4];
            for (int frame = 0; frame < 20; frame++)
            {
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    pixels[i] = (byte)(frame * 10); pixels[i + 1] = (byte)(frame < 10 ? 20 : 180);
                    pixels[i + 2] = (byte)(frame < 10 ? 200 : 20);
                }
                Write(writer, video, pixels, frame * 500000L, 500000);
                if (!sound) continue;
                for (int index = 0; index < 2400; index++)
                {
                    short sample = (short)(1600 * Math.Sin((frame * 2400 + index) * 2 * Math.PI * 220 / 48000));
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 4, 2), sample);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index * 4 + 2, 2), sample);
                }
                Write(writer, audio, pcm, frame * 500000L, 500000);
            }
            MfNative.Check(MfNative.Method<MfNative.SimpleCall>(writer, 11)(writer));
        }
        finally
        {
            MfNative.Release(ref input); MfNative.Release(ref output); MfNative.Release(ref writer);
            if (started) MfNative.MFShutdown(); if (initialized) MfNative.CoUninitialize();
        }
    }
    static void Write(IntPtr writer, uint stream, byte[] bytes, long timestamp, long duration)
    {
        IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
        bool locked = false;
        try
        {
            MfNative.Check(MFCreateSample(out sample)); MfNative.Check(MFCreateMemoryBuffer(bytes.Length, out buffer));
            MfNative.Check(MfNative.Method<MfNative.BufferLock>(buffer, 3)(buffer, out IntPtr destination, out _, out _)); locked = true;
            Marshal.Copy(bytes, 0, destination, bytes.Length);
            MfNative.Check(MfNative.Method<MfNative.SimpleCall>(buffer, 4)(buffer)); locked = false;
            MfNative.Check(MfNative.Method<SetLength>(buffer, 6)(buffer, (uint)bytes.Length));
            MfNative.Check(MfNative.Method<AddBuffer>(sample, 42)(sample, buffer));
            MfNative.Check(MfNative.Method<SetTime>(sample, 36)(sample, timestamp)); MfNative.Check(MfNative.Method<SetTime>(sample, 38)(sample, duration));
            MfNative.Check(MfNative.Method<WriteSample>(writer, 6)(writer, stream, sample));
        }
        finally
        {
            if (locked) MfNative.Method<MfNative.SimpleCall>(buffer, 4)(buffer);
            MfNative.Release(ref buffer); MfNative.Release(ref sample);
        }
    }
}
