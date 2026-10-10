using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LumaWall;

// Decode into ordinary bitmaps so videos work with the existing layered desktop
// window, wallpaper transforms, DrawToBitmap, and cross-fades.
static class VideoMedia
{
    public static async Task<Bitmap> GetPosterAsync(string path, CancellationToken token = default)
    {
        var work = Task.Run(() =>
        {
            token.ThrowIfCancellationRequested();
            using var decoder = new VideoDecoder(path);
            var frame = decoder.ReadFrame(token) ?? throw new InvalidDataException("视频中没有可显示的画面。");
            if (token.IsCancellationRequested) { frame.Bitmap.Dispose(); token.ThrowIfCancellationRequested(); }
            return frame.Bitmap;
        }, token);
        try { return await work.WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false); }
        catch
        {
            // A system codec may ignore cancellation inside its native read. Its
            // late frame remains our responsibility after the caller has left.
            _ = work.ContinueWith(completed =>
            {
                if (completed.Status == TaskStatus.RanToCompletion) completed.Result.Dispose();
                else _ = completed.Exception;
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
}

sealed class VideoPlayback : IDisposable
{
    readonly object gate = new();
    readonly CancellationTokenSource stop = new();
    readonly AutoResetEvent changed = new(false);
    readonly VideoFramePool frames = new();
    readonly PlaybackClock clock;
    readonly Task worker;
    readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    VideoFrame? current;
    AudioPlayback? audio;
    bool paused, disposed, audioEnabled;
    int decodeDelayMilliseconds;
    float volume = 1;
    long frameCount, loopCount, timestamp, presentedPosition, droppedFrames, seekCount, durationTicks, frameDuration = 333333;
    int sourceWidth, sourceHeight;
    int accelerated;
    public Size SourceSize => new(Volatile.Read(ref sourceWidth), Volatile.Read(ref sourceHeight));
    public bool Accelerated => Volatile.Read(ref accelerated) != 0;
    public Task Ready => ready.Task;
    Exception? error, audioError;
    public Exception? Error => Volatile.Read(ref error);
    public Exception? AudioError => Volatile.Read(ref audioError) ?? Volatile.Read(ref audio)?.Error;
    public bool HasAudio => Volatile.Read(ref audio)?.HasAudio == true;
    public bool AudioReady => Volatile.Read(ref audio)?.Ready.IsCompleted == true;
    public event Action<Exception>? Failed;
    public event Action<Exception>? AudioFailed;
    public event Action? FramePublished;
    internal PlaybackClock Clock => clock;
    internal AudioPlayback? Audio => Volatile.Read(ref audio);
    internal long FrameCount => Interlocked.Read(ref frameCount);
    internal long LoopCount => Interlocked.Read(ref loopCount);
    internal long CurrentTimestamp => Interlocked.Read(ref timestamp);
    internal long PresentedPositionTicks => Interlocked.Read(ref presentedPosition);
    internal long DroppedFrameCount => Interlocked.Read(ref droppedFrames);
    internal long SeekCount => Interlocked.Read(ref seekCount);
    internal long DurationTicks => Interlocked.Read(ref durationTicks);
    internal long FrameDuration => Interlocked.Read(ref frameDuration);

    public VideoPlayback(string path, bool paused = false, int maximumWidth = 0)
    {
        this.paused = paused;
        clock = new PlaybackClock(paused);
        worker = Task.Factory.StartNew(() => Run(path, Math.Max(0, maximumWidth)), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    internal int DecodeDelayMilliseconds
    {
        get => Volatile.Read(ref decodeDelayMilliseconds);
        set => Volatile.Write(ref decodeDelayMilliseconds, Math.Clamp(value, 0, 1000));
    }

    public bool Paused
    {
        get { lock (gate) return paused; }
        set
        {
            lock (gate)
            {
                if (disposed) return;
                paused = value; clock.SetPaused(value);
            }
            changed.Set();
        }
    }

    public bool AudioEnabled
    {
        get { lock (gate) return audioEnabled; }
        set
        {
            lock (gate)
            {
                if (disposed) return;
                audioEnabled = value;
                if (audio != null) audio.Enabled = value;
            }
        }
    }
    public float Volume
    {
        get { lock (gate) return volume; }
        set
        {
            lock (gate)
            {
                volume = float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 1;
                if (!disposed && audio != null) audio.Volume = volume;
            }
        }
    }

    VideoFrame? AcquireFrame()
    {
        lock (gate) return disposed ? null : current?.Acquire();
    }

    public bool Draw(Graphics graphics, Size viewport)
        => Draw(graphics, viewport, WallpaperLayoutMode.Fill);

    public bool Draw(Graphics graphics, Size viewport, WallpaperLayoutMode mode)
        => Draw(graphics, viewport, mode, Size.Empty);

    public bool Draw(Graphics graphics, Size viewport, WallpaperLayoutMode mode, Size referenceSize)
    {
        using var frame = AcquireFrame();
        if (frame == null) return false;
        if (frame.Drawing?.Draw(graphics, viewport, mode, referenceSize, SourceSize) == true) return true;
        var destination = WallpaperLayout.Destination(frame.Bitmap.Size, viewport, mode, referenceSize, SourceSize);
        float scale = (float)Math.Max(destination.Width / frame.Bitmap.Width, destination.Height / frame.Bitmap.Height);
        graphics.InterpolationMode = scale > 3 ? System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor : System.Drawing.Drawing2D.InterpolationMode.Bilinear;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
        WallpaperLayout.DrawImage(graphics, frame.Bitmap, viewport, mode, referenceSize, SourceSize);
        return true;
    }

    public bool DrawHdc(IntPtr target, Size viewport, WallpaperTransform transform, Rectangle paintClip)
        => DrawHdc(target, viewport, transform, paintClip, out _);

    public bool DrawHdc(IntPtr target, Size viewport, WallpaperTransform transform, Rectangle paintClip, out long frameId)
        => DrawHdc(target, viewport, transform, paintClip, WallpaperLayoutMode.Fill, out frameId);

    public bool DrawHdc(IntPtr target, Size viewport, WallpaperTransform transform, Rectangle paintClip, WallpaperLayoutMode mode, out long frameId)
        => DrawHdc(target, viewport, transform, paintClip, mode, Size.Empty, out frameId);

    public bool DrawHdc(IntPtr target, Size viewport, WallpaperTransform transform, Rectangle paintClip, WallpaperLayoutMode mode, Size referenceSize, out long frameId)
    {
        using var frame = AcquireFrame();
        frameId = frame?.PresentationTime ?? -1;
        return frame?.Drawing?.DrawHdc(target, viewport, transform, paintClip, mode, referenceSize, SourceSize) == true;
    }

    internal Bitmap? CopyFrame()
    {
        using var frame = AcquireFrame();
        return frame == null ? null : (Bitmap)frame.Bitmap.Clone();
    }

    bool Publish(VideoFrame frame, long presentationTime, bool firstFrame = false, bool allowExpired = false)
    {
        while (true)
        {
            stop.Token.ThrowIfCancellationRequested();
            VideoFrame? previous = null;
            lock (gate)
            {
                if (disposed) return false;
                // Pause and publication share this short lock. Painting holds a
                // frame lease elsewhere and cannot make the timeline run slow.
                if (!paused || firstFrame)
                {
                    if (!firstFrame && !allowExpired && presentationTime + frame.Duration <= clock.PositionTicks)
                    { Interlocked.Increment(ref droppedFrames); return false; }
                    frame.PresentationTime = presentationTime;
                    previous = current; current = frame;
                    Interlocked.Exchange(ref timestamp, frame.Timestamp);
                    Interlocked.Exchange(ref presentedPosition, presentationTime);
                    if (DurationTicks > 0) Interlocked.Exchange(ref loopCount, presentationTime / DurationTicks);
                    Interlocked.Increment(ref frameCount);
                }
            }
            if (previous != null || firstFrame)
            {
                previous?.Dispose();
                try { FramePublished?.Invoke(); } catch { }
                return true;
            }
            WaitChanged(100);
        }
    }

    void WaitChanged(int delay)
    {
        if (WaitHandle.WaitAny([stop.Token.WaitHandle, changed], delay) == 0) stop.Token.ThrowIfCancellationRequested();
    }

    void WaitFor(long presentationTime, FrameDeadlineWait deadlines)
    {
        while (true)
        {
            stop.Token.ThrowIfCancellationRequested();
            bool frozen = clock.IsPaused;
            long remaining = presentationTime - clock.PositionTicks;
            if (!frozen && remaining <= 0) return;
            if (frozen) WaitChanged(100); else deadlines.Wait(remaining);
        }
    }

    void ReportAudioFailure(Exception failure)
    {
        Volatile.Write(ref audioError, failure);
        try { AudioFailed?.Invoke(failure); } catch { }
    }

    void CreateAudio(string path)
    {
        var output = new AudioPlayback(path, clock, DurationTicks);
        output.Failed += ReportAudioFailure;
        bool accepted;
        lock (gate)
        {
            accepted = !disposed;
            if (accepted)
            {
                audio = output; output.Volume = volume; output.Enabled = audioEnabled;
            }
        }
        if (!accepted) { output.Dispose(); return; }
        if (output.Error is { } failure) ReportAudioFailure(failure);
        _ = output.Ready.ContinueWith(completed => { _ = completed.Exception; }, TaskScheduler.Default);
    }

    void Run(string path, int maximumWidth)
    {
        VideoFrame? pending = null;
        try
        {
            using var deadlines = new FrameDeadlineWait(stop.Token, changed);
            using var decoder = VideoDecoder.OpenForPlayback(path, maximumWidth, stop.Token);
            Volatile.Write(ref sourceWidth, decoder.OriginalSize.Width); Volatile.Write(ref sourceHeight, decoder.OriginalSize.Height);
            Interlocked.Exchange(ref durationTicks, decoder.DurationTicks);
            Interlocked.Exchange(ref frameDuration, decoder.FrameDuration);
            pending = decoder.ReadFrame(stop.Token, frames) ?? throw new InvalidDataException("视频中没有可显示的画面。");
            Volatile.Write(ref accelerated, decoder.Accelerated ? 1 : 0);
            long decodedEnd = pending.Timestamp + pending.Duration;
            if (!Publish(pending, Math.Max(0, pending.Timestamp), true)) pending.Dispose(); pending = null;
            clock.Start(); ready.TrySetResult();
            // Original sound is optional. An audio device/codec failure must
            // leave the already prepared video frame and playback available.
            try { CreateAudio(path); }
            catch (Exception ex) { ReportAudioFailure(new InvalidOperationException("视频原声无法播放：" + ex.Message, ex)); }
            long cycle = 0, lastSeekTime = -20000000;
            long lastPublishedAt = clock.PositionTicks, lastPresentationTime = Math.Max(0, decoder.DecodedTimestamp);
            long previousDecodeCost = 0;
            bool recovering = false;
            while (true)
            {
                WaitFor(clock.PositionTicks, deadlines);
                long period = DurationTicks, elapsed = clock.PositionTicks;
                if (period > 0)
                {
                    long wantedCycle = elapsed / period;
                    if (wantedCycle > cycle)
                    {
                        cycle = wantedCycle; decoder.Seek(elapsed % period);
                        lastSeekTime = elapsed; recovering = true;
                    }
                    else if (!recovering && previousDecodeCost > 7500000
                        && elapsed - (cycle * period + decoder.DecodedTimestamp) > Math.Max(10000000, FrameDuration * 30)
                        && elapsed - lastSeekTime > 20000000)
                    {
                        // Only a major native stall warrants a seek. A decoder
                        // making steady forward progress under overload must
                        // never be sent backwards just because it is slow.
                        // A seek can land seconds before the requested time.
                        // Continue decoding that GOP until caught up: another
                        // timer-based seek could rewind to the same key frame.
                        decoder.Seek(elapsed % period); lastSeekTime = elapsed;
                        recovering = true;
                        Interlocked.Increment(ref seekCount);
                    }
                }
                long decodeStarted = clock.PositionTicks;
                pending = decoder.ReadFrame(stop.Token, frames, (sampleTime, sampleDuration) =>
                {
                    int delay = DecodeDelayMilliseconds;
                    if (delay > 0 && stop.Token.WaitHandle.WaitOne(delay)) stop.Token.ThrowIfCancellationRequested();
                    decodedEnd = Math.Max(decodedEnd, sampleTime + sampleDuration);
                    long samplePosition = cycle * period + Math.Max(0, sampleTime);
                    long now = clock.PositionTicks;
                    bool expired = samplePosition + sampleDuration <= now;
                    long tolerance = Math.Clamp(FrameDuration * 2, 400000, 1000000);
                    bool slightlyLate = now - samplePosition <= sampleDuration + tolerance;
                    // Under sustained overload there might never be a sample
                    // newer than the clock. Publish an advancing decoded frame
                    // at least every 150ms instead of dropping everything and
                    // leaving the wallpaper frozen for the whole GOP.
                    // RGB transfer can finish a few milliseconds past a very
                    // short frame's deadline. Rejecting every such advancing
                    // frame reduced high-fps playback to the 150ms rescue rate.
                    // Present small delays continuously; drop genuinely old
                    // samples before copying them and never queue past frames.
                    bool discard = expired && (samplePosition <= lastPresentationTime || !slightlyLate && now - lastPublishedAt < 1500000);
                    if (discard) Interlocked.Increment(ref droppedFrames);
                    return discard;
                });
                Volatile.Write(ref accelerated, decoder.Accelerated ? 1 : 0);
                previousDecodeCost = Math.Max(0, clock.PositionTicks - decodeStarted);
                Interlocked.Exchange(ref frameDuration, decoder.FrameDuration);
                if (pending == null)
                {
                    // A bounded batch yields to pause/loop/cancellation, but
                    // preserves the one-way recovery state across that yield.
                    if (!decoder.AtEnd) continue;
                    if (period <= 0)
                    {
                        period = Math.Max(1, decodedEnd);
                        Interlocked.Exchange(ref durationTicks, period);
                    }
                    WaitFor((cycle + 1) * period, deadlines);
                    elapsed = clock.PositionTicks; cycle = elapsed / period;
                    decoder.Seek(elapsed % period); lastSeekTime = elapsed; recovering = true;
                    continue;
                }
                decodedEnd = Math.Max(decodedEnd, pending.Timestamp + pending.Duration);
                long presentationTime = cycle * period + Math.Max(0, pending.Timestamp);
                WaitFor(presentationTime, deadlines);
                long publicationTime = clock.PositionTicks;
                bool expired = presentationTime + pending.Duration <= publicationTime;
                if (!expired) recovering = false;
                long lateWindow = Math.Clamp(FrameDuration * 2, 400000, 1000000);
                bool rescue = expired && presentationTime > lastPresentationTime
                    && (publicationTime - presentationTime <= pending.Duration + lateWindow || publicationTime - lastPublishedAt >= 1500000);
                if (Publish(pending, presentationTime, allowExpired: rescue))
                { lastPublishedAt = clock.PositionTicks; lastPresentationTime = presentationTime; }
                else pending.Dispose();
                pending = null;
            }
        }
        catch (OperationCanceledException) { ready.TrySetCanceled(stop.Token); }
        catch (Exception ex)
        {
            var failure = new InvalidOperationException("视频播放失败：" + ex.Message, ex);
            Volatile.Write(ref error, failure);
            lock (gate) { if (audio != null) audio.Enabled = false; clock.SetPaused(true); }
            ready.TrySetException(failure);
            try { Failed?.Invoke(failure); } catch { }
        }
        finally { pending?.Dispose(); }
    }

    public void Dispose()
    {
        AudioPlayback? output;
        VideoFrame? previous;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; output = audio; audio = null; previous = current; current = null;
        }
        output?.Dispose(); clock.SetPaused(true); stop.Cancel(); changed.Set();
        previous?.Dispose(); ready.TrySetCanceled();
        // Outstanding paints own their frame leases. Closing the pool disposes
        // their storage safely when those paints return it, even after a timeout.
        if (worker.Wait(TimeSpan.FromSeconds(3))) { frames.Dispose(); changed.Dispose(); stop.Dispose(); }
        else _ = worker.ContinueWith(_ => { frames.Dispose(); changed.Dispose(); stop.Dispose(); }, TaskScheduler.Default);
    }
}
// The publisher and each active paint hold a lease. Old storage is recycled only
// after the last paint finishes, so the decoder never waits on a desktop blit.
sealed class VideoFrame : IDisposable
{
    int leases = 1;
    readonly VideoFramePool? pool;
    public Bitmap Bitmap { get; }
    public VideoDrawing? Drawing { get; }
    public long Timestamp { get; }
    public long Duration { get; }
    internal long PresentationTime { get; set; }
    public VideoFrame(Bitmap bitmap, long timestamp, long duration, VideoDrawing? drawing = null, VideoFramePool? pool = null)
    { Bitmap = bitmap; Timestamp = timestamp; Duration = duration; Drawing = drawing; this.pool = pool; }
    public VideoFrame Acquire() { Interlocked.Increment(ref leases); return this; }
    public void Dispose()
    {
        if (Interlocked.Decrement(ref leases) != 0) return;
        if (Drawing != null && pool != null) pool.Return(Drawing);
        else { Drawing?.Dispose(); Bitmap.Dispose(); }
    }
}

sealed class VideoFramePool : IDisposable
{
    readonly object gate = new();
    readonly Stack<VideoDrawing> available = new();
    bool disposed;
    public VideoDrawing Rent(Size size)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            while (available.TryPop(out var drawing))
            {
                if (drawing.Size == size) return drawing;
                drawing.Dispose();
            }
        }
        return new VideoDrawing(size);
    }
    public void Return(VideoDrawing drawing)
    {
        lock (gate)
        {
            if (!disposed && available.Count < 3) { available.Push(drawing); return; }
        }
        drawing.Dispose();
    }
    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            while (available.TryPop(out var drawing)) drawing.Dispose();
        }
    }
}

sealed class VideoDecoder : IDisposable
{
    IntPtr reader;
    VideoAcceleration? acceleration;
    readonly string sourcePath;
    readonly int maximumWidth;
    bool decodedFrame;
    bool nv12, fullRange, forceRgb;
    int yuvMatrix;
    public bool Accelerated => acceleration != null;
    bool comStarted, mediaStarted;
    int width, height, stride;
    Rectangle visibleArea;
    long frameDuration = 333333;
    public long FrameDuration => frameDuration;
    public long DurationTicks { get; private set; }
    public Size OriginalSize { get; private set; }
    public long DecodedTimestamp { get; private set; }
    public bool AtEnd { get; private set; }
    public static VideoDecoder OpenForPlayback(string path, int maximumWidth, CancellationToken token)
    {
        var selected = new VideoDecoder(path, maximumWidth);
        if (!selected.Accelerated || selected.FrameDuration > 111111) return selected;
        VideoDecoder? compatible = null;
        try
        {
            // GPU readback can exceed a high-fps frame's entire budget on some
            // drivers. Compare actual decode+copy costs before starting the
            // clock instead of forcing hardware on every codec/resolution.
            double gpu = ProbeCost(selected, token);
            if (selected.Accelerated)
            {
                try
                {
                    compatible = new VideoDecoder(path, maximumWidth, false);
                    double cpu = ProbeCost(compatible, token);
                    if (cpu < gpu * .85)
                    { selected.Dispose(); selected = compatible; compatible = null; }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A working hardware-only codec must remain usable if no
                    // corresponding software implementation is installed.
                    compatible?.Dispose(); compatible = null;
                }
            }
            selected.Rewind(); return selected;
        }
        catch { selected.Dispose(); throw; }
        finally { compatible?.Dispose(); }
    }

    static double ProbeCost(VideoDecoder decoder, CancellationToken token)
    {
        using var pool = new VideoFramePool();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        double total = 0; int count = 0;
        for (int i = 0; i < 10; i++)
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            using var frame = decoder.ReadFrame(token, pool);
            if (frame == null) break;
            if (i > 0) { total += System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds; count++; }
            if (watch.Elapsed.TotalMilliseconds > 800) break;
        }
        return count > 0 ? total / count : watch.Elapsed.TotalMilliseconds;
    }

    public VideoDecoder(string path, int maximumWidth = 0, bool preferHardware = true)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到视频文件。", path);
        sourcePath = System.IO.Path.GetFullPath(path); this.maximumWidth = maximumWidth;
        try
        {
            MfNative.Check(MfNative.CoInitializeEx(IntPtr.Zero, 0)); comStarted = true;
            MfNative.Check(MfNative.MFStartup(0x00020070, 0)); mediaStarted = true;
            if (preferHardware) acceleration = VideoAcceleration.TryCreate();
            try { InitializeReader(); }
            catch when (acceleration != null) { UseSoftwareReader(); }
        }
        catch (Exception ex)
        {
            Dispose();
            throw new NotSupportedException("无法解码此视频。请使用 Windows 支持的 MP4（H.264）、WMV 或 AVI 文件。" + ex.Message, ex);
        }
    }

    void UseSoftwareReader()
    {
        MfNative.Release(ref reader); acceleration?.Dispose(); acceleration = null;
        InitializeReader();
    }

    void InitializeReader()
    {
        IntPtr attributes = IntPtr.Zero, mediaType = IntPtr.Zero;
        try
        {
            MfNative.Check(MfNative.MFCreateAttributes(out attributes, 3));
            MfNative.SetUInt32(attributes, MfNative.AdvancedVideoProcessing, 1);
            MfNative.SetUInt32(attributes, MfNative.EnableHardwareTransforms, 1);
            if (acceleration != null)
            {
                var key = VideoAcceleration.SourceReaderManager;
                MfNative.Check(MfNative.Method<MfNative.SetUnknownValue>(attributes, 27)(attributes, ref key, acceleration.Manager));
            }
            MfNative.Check(MfNative.MFCreateSourceReaderFromURL(sourcePath, attributes, out reader));
            MfNative.Check(MfNative.Method<MfNative.SelectStream>(reader, 4)(reader, 0xfffffffe, 0));
            MfNative.Check(MfNative.Method<MfNative.SelectStream>(reader, 4)(reader, MfNative.VideoStream, 1));
            MfNative.Check(MfNative.MFCreateMediaType(out mediaType));
            MfNative.SetGuid(mediaType, MfNative.MajorType, MfNative.VideoType);
            nv12 = !forceRgb && maximumWidth == 0;
            MfNative.SetGuid(mediaType, MfNative.Subtype, nv12 ? MfNative.Nv12 : MfNative.Rgb32);
            {
                IntPtr originalType = IntPtr.Zero;
                try
                {
                    MfNative.Check(MfNative.Method<MfNative.GetMediaType>(reader, 6)(reader, MfNative.VideoStream, out originalType));
                    ulong sourceSize = MfNative.GetUInt64(originalType, MfNative.FrameSize);
                    int sourceWidth = checked((int)(sourceSize >> 32)), sourceHeight = checked((int)(sourceSize & uint.MaxValue));
                    OriginalSize = new(sourceWidth, sourceHeight);
                    if (maximumWidth > 0 && sourceWidth > maximumWidth && sourceHeight > 0)
                    {
                        int targetWidth = Math.Max(16, maximumWidth / 2 * 2);
                        int targetHeight = Math.Max(2, (int)Math.Round(sourceHeight * (double)targetWidth / sourceWidth / 2) * 2);
                        MfNative.SetUInt64(mediaType, MfNative.FrameSize, (ulong)(uint)targetWidth << 32 | (uint)targetHeight);
                    }
                }
                finally { MfNative.Release(ref originalType); }
            }
            int negotiated = MfNative.Method<MfNative.SetMediaType>(reader, 7)(reader, MfNative.VideoStream, IntPtr.Zero, mediaType);
            if (negotiated < 0 && nv12)
            {
                nv12 = false; MfNative.SetGuid(mediaType, MfNative.Subtype, MfNative.Rgb32);
                negotiated = MfNative.Method<MfNative.SetMediaType>(reader, 7)(reader, MfNative.VideoStream, IntPtr.Zero, mediaType);
            }
            MfNative.Check(negotiated);
            ReadFormat();
            ReadDuration();
        }
        finally { MfNative.Release(ref mediaType); MfNative.Release(ref attributes); }
    }

    void ReadFormat()
    {
        IntPtr type = IntPtr.Zero;
        try
        {
            MfNative.Check(MfNative.Method<MfNative.GetMediaType>(reader, 6)(reader, MfNative.VideoStream, out type));
            ulong size = MfNative.GetUInt64(type, MfNative.FrameSize);
            width = checked((int)(size >> 32)); height = checked((int)(size & uint.MaxValue));
            if (width <= 0 || height <= 0 || (long)width * height > 67108864) throw new InvalidDataException("视频画面尺寸无效或过大。");
            visibleArea = new(0, 0, width, height);
            // Codecs may expose macroblock-padded storage (for example 180
            // visible rows in a 192-row H.264 frame). Preserve the actual
            // display aperture rather than showing padded black rows.
            IntPtr aperture = Marshal.AllocHGlobal(16);
            try
            {
                foreach (var apertureKey in new[] { MfNative.MinimumDisplayAperture, MfNative.GeometricAperture })
                {
                    var candidate = apertureKey;
                    if (MfNative.Method<MfNative.GetBlobValue>(type, 15)(type, ref candidate, aperture, 16, out uint blobLength) < 0 || blobLength != 16) continue;
                    int x = Marshal.ReadInt16(aperture, 2), y = Marshal.ReadInt16(aperture, 6);
                    int visibleWidth = Marshal.ReadInt32(aperture, 8), visibleHeight = Marshal.ReadInt32(aperture, 12);
                    if (x >= 0 && y >= 0 && visibleWidth > 0 && visibleHeight > 0 && (long)x + visibleWidth <= width && (long)y + visibleHeight <= height)
                    { visibleArea = new(x, y, visibleWidth, visibleHeight); break; }
                }
            }
            finally { Marshal.FreeHGlobal(aperture); }
            var key = MfNative.DefaultStride;
            int result = MfNative.Method<MfNative.GetUInt32Value>(type, 7)(type, ref key, out uint rawStride);
            if (result >= 0) stride = unchecked((int)rawStride);
            else if (nv12) stride = width;
            else MfNative.Check(MfNative.MFGetStrideForBitmapInfoHeader(22, width, out stride));
            if (Math.Abs((long)stride) < width * (nv12 ? 1L : 4L) || nv12 && (stride < 0 || (width & 1) != 0 || (height & 1) != 0))
                throw new InvalidDataException("视频帧数据行宽无效。");
            key = MfNative.YuvMatrix;
            yuvMatrix = MfNative.Method<MfNative.GetUInt32Value>(type, 7)(type, ref key, out uint matrix) >= 0 && matrix != 0 ? (int)matrix : height >= 720 ? 1 : 2;
            key = MfNative.NominalRange;
            fullRange = MfNative.Method<MfNative.GetUInt32Value>(type, 7)(type, ref key, out uint range) >= 0 && range == 1;
            key = MfNative.FrameRate;
            if (MfNative.Method<MfNative.GetUInt64Value>(type, 8)(type, ref key, out ulong rate) >= 0)
            {
                uint numerator = (uint)(rate >> 32), denominator = (uint)rate;
                if (numerator != 0 && denominator != 0) frameDuration = Math.Max(1, 10000000L * denominator / numerator);
            }
        }
        finally { MfNative.Release(ref type); }
    }

    void ReadDuration()
    {
        var key = MfNative.PresentationDuration;
        var value = new MfNative.PropVariant();
        try
        {
            if (MfNative.Method<MfNative.GetPresentationAttribute>(reader, 12)(reader, 0xffffffff, ref key, ref value) >= 0 &&
                value.Type is 20 or 21 && value.Long > 0) DurationTicks = value.Long;
        }
        finally { MfNative.PropVariantClear(ref value); }
    }

    public VideoFrame? ReadFrame(CancellationToken token, VideoFramePool? pool = null, Func<long, long, bool>? skipFrame = null)
    {
        try
        {
            var result = ReadFrameCore(token, pool, skipFrame);
            decodedFrame |= result != null;
            return result;
        }
        catch (Exception ex) when (acceleration != null && ex is COMException or InvalidDataException)
        {
            // Unsupported GPU buffers or a lost device get one software retry.
            // Continue from the existing media time; do not restart the shared
            // video/audio clock. Cancellation is never retried.
            token.ThrowIfCancellationRequested();
            long resume = DecodedTimestamp;
            UseSoftwareReader();
            if (decodedFrame) Seek(resume);
            var result = ReadFrameCore(token, pool, skipFrame); decodedFrame |= result != null;
            return result;
        }
    }

    VideoFrame? ReadFrameCore(CancellationToken token, VideoFramePool? pool, Func<long, long, bool>? skipFrame)
    {
        AtEnd = false;
        int skipped = 0;
        for (int attempt = 0; attempt < 100; attempt++)
        {
            token.ThrowIfCancellationRequested();
            IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero, buffer2D = IntPtr.Zero;
            Bitmap? bitmap = null;
            VideoDrawing? drawing = null;
            bool locked = false;
            try
            {
                MfNative.Check(MfNative.Method<MfNative.ReadSample>(reader, 9)(reader, MfNative.VideoStream, 0, out _, out uint flags, out long timestamp, out sample));
                if ((flags & 1) != 0) throw new InvalidDataException("视频解码器报告了错误。");
                if ((flags & 0x20) != 0) ReadFormat();
                if (sample == IntPtr.Zero)
                {
                    if ((flags & 2) != 0) { AtEnd = true; return null; }
                    continue;
                }
                DecodedTimestamp = timestamp;
                var interlacedKey = MfNative.SampleInterlaced;
                if (nv12 && MfNative.Method<MfNative.GetUInt32Value>(sample, 7)(sample, ref interlacedKey, out uint interlaced) >= 0 && interlaced != 0)
                {
                    // Containers often mark progressive H.264 as mixed mode.
                    // Only an actually interlaced sample needs the system RGB
                    // processor's deinterlacing path.
                    SwitchToRgb(); continue;
                }
                long duration = frameDuration;
                if (MfNative.Method<MfNative.GetSampleTime>(sample, 37)(sample, out long sampleDuration) >= 0 && sampleDuration > 0) duration = sampleDuration;
                // Decode reference frames as required by the codec, but avoid
                // copying expired samples into another full-size pixel buffer.
                if (skipFrame?.Invoke(timestamp, duration) == true)
                {
                    if (++skipped >= 16) return null;
                    attempt = -1; continue;
                }
                MfNative.Check(MfNative.Method<MfNative.SampleBuffer>(sample, 41)(sample, out buffer));
                IntPtr top;
                int actualStride;
                var iid = MfNative.Buffer2D;
                if (Marshal.QueryInterface(buffer, ref iid, out buffer2D) >= 0)
                {
                    MfNative.Check(MfNative.Method<MfNative.Lock2DSize>(buffer2D, 10)(buffer2D, 1,
                        out top, out actualStride, out IntPtr start, out uint length));
                    locked = true;
                    long rowBytes = width * (nv12 ? 1L : 4L);
                    long rows = nv12 ? height * 3L / 2 : height;
                    long first = top.ToInt64(), last = first + (rows - 1L) * actualStride;
                    long low = Math.Min(first, last), high = Math.Max(first, last) + rowBytes;
                    if (Math.Abs((long)actualStride) < rowBytes || nv12 && actualStride < 0 || low < start.ToInt64() || high > start.ToInt64() + length)
                        throw new InvalidDataException("视频二维帧缓冲区范围无效。");
                }
                else
                {
                    MfNative.Check(MfNative.Method<MfNative.BufferLock>(buffer, 3)(buffer, out IntPtr data, out _, out uint length));
                    locked = true; actualStride = stride;
                    long expectedLength = ((nv12 ? height * 3L / 2 : height) - 1) * Math.Abs((long)actualStride) + width * (nv12 ? 1L : 4L);
                    if (length < expectedLength) throw new InvalidDataException("视频帧缓冲区长度不足。");
                    top = actualStride < 0 ? IntPtr.Add(data, checked((height - 1) * -actualStride)) : data;
                }
                if (!nv12) top = IntPtr.Add(top, checked(visibleArea.Y * actualStride + visibleArea.X * 4));
                if (pool != null)
                {
                    drawing = pool.Rent(visibleArea.Size);
                    if (nv12) drawing.CopyNv12From(top, actualStride, height, visibleArea, yuvMatrix, fullRange);
                    else drawing.CopyFrom(top, actualStride);
                    var frame = new VideoFrame(drawing.Bitmap, timestamp, duration, drawing, pool); drawing = null;
                    return frame;
                }
                bitmap = new Bitmap(visibleArea.Width, visibleArea.Height, PixelFormat.Format32bppRgb);
                var bits = bitmap.LockBits(new(Point.Empty, bitmap.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    if (nv12) Nv12Converter.Copy(bits.Scan0, bits.Stride, top, actualStride, height, visibleArea, yuvMatrix, fullRange);
                    else MfNative.Check(MfNative.MFCopyImage(bits.Scan0, bits.Stride, top, actualStride, visibleArea.Width * 4, visibleArea.Height));
                }
                finally { bitmap.UnlockBits(bits); }
                var managedFrame = new VideoFrame(bitmap, timestamp, duration); bitmap = null;
                return managedFrame;
            }
            finally
            {
                bitmap?.Dispose();
                if (drawing != null) pool!.Return(drawing);
                if (locked) MfNative.Method<MfNative.SimpleCall>(buffer2D != IntPtr.Zero ? buffer2D : buffer, 4)(buffer2D != IntPtr.Zero ? buffer2D : buffer);
                MfNative.Release(ref buffer2D);
                MfNative.Release(ref buffer); MfNative.Release(ref sample);
            }
        }
        throw new InvalidDataException("视频解码器没有返回有效画面。");
    }

    public void Rewind() => Seek(0);

    void SwitchToRgb()
    {
        IntPtr type = IntPtr.Zero;
        try
        {
            forceRgb = true;
            MfNative.Check(MfNative.MFCreateMediaType(out type));
            MfNative.SetGuid(type, MfNative.MajorType, MfNative.VideoType);
            MfNative.SetGuid(type, MfNative.Subtype, MfNative.Rgb32);
            MfNative.Check(MfNative.Method<MfNative.SetMediaType>(reader, 7)(reader, MfNative.VideoStream, IntPtr.Zero, type));
            nv12 = false; ReadFormat();
        }
        finally { MfNative.Release(ref type); }
    }

    public void Seek(long timestamp)
    {
        var format = Guid.Empty;
        var position = new MfNative.PropVariant { Type = 20, Long = Math.Max(0, timestamp) };
        MfNative.Check(MfNative.Method<MfNative.SetPosition>(reader, 8)(reader, ref format, ref position));
    }

    public void Dispose()
    {
        MfNative.Release(ref reader);
        acceleration?.Dispose(); acceleration = null;
        if (mediaStarted) { MfNative.MFShutdown(); mediaStarted = false; }
        if (comStarted) { MfNative.CoUninitialize(); comStarted = false; }
    }
}

// The method slots below follow the Windows SDK's IMFSourceReader,
// IMFAttributes, IMFSample and IMFMediaBuffer COM vtables.
static class MfNative
{
    internal const uint VideoStream = 0xfffffffc;
    internal static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"),
        AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3"),
        Buffer2D = new("33ae5ea6-4316-436f-8ddd-d73d22f829ec"),
        EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0"), PresentationDuration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a"),
        MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        VideoType = new("73646976-0000-0010-8000-00aa00389b71"), Rgb32 = new("00000016-0000-0010-8000-00aa00389b71"),
        SampleInterlaced = new("b1d5830a-deb8-40e3-90fa-389943716461"),
        Nv12 = new("3231564e-0000-0010-8000-00aa00389b71"), YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327"), NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11"),
        FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d"), FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),
        DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"),
        MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4"), GeometricAperture = new("66758743-7e5f-400d-980a-aa8596c85696");
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Long;
    }
    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint options);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int Lock2DSize(IntPtr self, uint flags,
        out IntPtr scanline0, out int pitch, out IntPtr start, out uint length);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("ole32.dll")] internal static extern int PropVariantClear(ref PropVariant value);
    [DllImport("mfplat.dll")] internal static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] internal static extern int MFShutdown();
    [DllImport("mfplat.dll")] internal static extern int MFCreateAttributes(out IntPtr attributes, uint capacity);
    [DllImport("mfplat.dll")] internal static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll")] internal static extern int MFGetStrideForBitmapInfoHeader(uint format, int width, out int stride);
    [DllImport("mfplat.dll")] internal static extern int MFCopyImage(IntPtr destination, int destinationStride, IntPtr source, int sourceStride, int widthBytes, int height);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] internal static extern int MFCreateSourceReaderFromURL(string path, IntPtr attributes, out IntPtr reader);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SelectStream(IntPtr self, uint stream, int selected);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetMediaType(IntPtr self, uint stream, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetMediaType(IntPtr self, uint stream, IntPtr reserved, IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ReadSample(IntPtr self, uint stream, uint controls, out uint actualStream, out uint flags, out long timestamp, out IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetPosition(IntPtr self, ref Guid format, ref PropVariant position);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetPresentationAttribute(IntPtr self, uint stream, ref Guid key, ref PropVariant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetUInt32Value(IntPtr self, ref Guid key, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetUInt64Value(IntPtr self, ref Guid key, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetUInt32Value(IntPtr self, ref Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetUInt64Value(IntPtr self, ref Guid key, ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetGuidValue(IntPtr self, ref Guid key, ref Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetUnknownValue(IntPtr self, ref Guid key, IntPtr value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetBlobValue(IntPtr self, ref Guid key, IntPtr destination, uint capacity, out uint length);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SampleBuffer(IntPtr self, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int BufferLock(IntPtr self, out IntPtr data, out uint maximumLength, out uint currentLength);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetSampleTime(IntPtr self, out long duration);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SimpleCall(IntPtr self);

    internal static T Method<T>(IntPtr instance, int index) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), index * IntPtr.Size));
    internal static void Check(int result) { if (result < 0) Marshal.ThrowExceptionForHR(result); }
    internal static void Release(ref IntPtr instance) { if (instance != IntPtr.Zero) { Marshal.Release(instance); instance = IntPtr.Zero; } }
    internal static void SetGuid(IntPtr instance, Guid key, Guid value) => Check(Method<SetGuidValue>(instance, 24)(instance, ref key, ref value));
    internal static void SetUInt32(IntPtr instance, Guid key, uint value) => Check(Method<SetUInt32Value>(instance, 21)(instance, ref key, value));
    internal static void SetUInt64(IntPtr instance, Guid key, ulong value) => Check(Method<SetUInt64Value>(instance, 22)(instance, ref key, value));
    internal static ulong GetUInt64(IntPtr instance, Guid key) { Check(Method<GetUInt64Value>(instance, 8)(instance, ref key, out ulong value)); return value; }
}
