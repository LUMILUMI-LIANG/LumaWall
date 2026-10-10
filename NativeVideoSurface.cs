using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LumaWall;

// Video-only WM_PAINT path. Screenshots, image wallpapers and transitions still
// use the control's regular GDI+ OnPaint implementation through the base class.
class NativeVideoSurface : Control
{
    const int PaintRequest = 0x8361;
    readonly object scheduleGate = new();
    HighResolutionDelay? deferred;
    NativePaintBuffer? paintBuffer;
    IntPtr window;
    bool enabled, pending, closed, displayable = true;
    int fpsLimit = FrameRateOptions.Maximum;
    long nextPaint, requestedVersion, lastPaintedId = -1, renderedFrameId = -1;
    long paintedFrames, presentedFrames, totalPaintTicks, maximumPaintTicks;
    double completePaintMilliseconds;
    internal int NativeBufferAllocations { get; private set; }

    protected virtual bool NativeRenderingAllowed => true;
    // An already decoded image that covers every visible pixel can be copied
    // directly in one operation. Composite backgrounds use the scratch buffer.
    protected virtual bool NativeFrameCoversViewport => false;
    protected virtual long NativeFrameId => -1;
    protected virtual bool TryRenderNative(IntPtr dc, Size viewport, Rectangle paintClip) => false;
    internal long NativePaintedFrames => Interlocked.Read(ref paintedFrames);
    internal long NativePresentedFrames => Interlocked.Read(ref presentedFrames);
    internal double NativeCompletePaintMilliseconds => Volatile.Read(ref completePaintMilliseconds);
    internal double NativeTotalPaintMilliseconds => Interlocked.Read(ref totalPaintTicks) * 1000.0 / Stopwatch.Frequency;
    internal double NativeMaximumPaintMilliseconds => Interlocked.Read(ref maximumPaintTicks) * 1000.0 / Stopwatch.Frequency;

    protected int NativeFpsLimit
    {
        get { lock (scheduleGate) return fpsLimit; }
        set
        {
            lock (scheduleGate)
            {
                fpsLimit = FrameRateOptions.Limit(value);
                nextPaint = 0;
            }
            ScheduleNativePaint();
        }
    }

    protected void EnableNativeRendering(bool value)
    {
        lock (scheduleGate)
        {
            if (closed) return;
            enabled = value; pending = false; nextPaint = 0;
            deferred?.Cancel();
        }
        if (value) ScheduleNativePaint();
    }

    // Safe on the decoder thread: never access Control.Handle or call any
    // WinForms method here. The HWND is captured by its owner thread below.
    protected void ScheduleNativePaint()
    {
        lock (scheduleGate)
        {
            if (closed || !enabled || !displayable || window == IntPtr.Zero) return;
            requestedVersion++;
            if (pending) return;
            pending = true;
            PostOrDefer();
        }
    }

    void PostOrDefer()
    {
        long remaining = nextPaint - Stopwatch.GetTimestamp();
        if (remaining > 0)
        {
            // Keep the latest request until its deadline. Dropping an early
            // 60fps notification would otherwise turn minor jitter into 30fps.
            (deferred ??= new HighResolutionDelay(PostDeferred)).Arm(remaining);
        }
        else if (!PostMessage(window, PaintRequest, IntPtr.Zero, IntPtr.Zero)) pending = false;
    }

    void PostDeferred()
    {
        lock (scheduleGate)
        {
            if (closed || !enabled || !displayable || !pending || window == IntPtr.Zero) return;
            PostOrDefer();
        }
    }

    protected void RecordNativeFrame(long frameId) => renderedFrameId = frameId;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        lock (scheduleGate) { window = Handle; displayable = Visible; pending = false; nextPaint = 0; }
        ScheduleNativePaint();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        lock (scheduleGate)
        {
            window = IntPtr.Zero; pending = false;
            if (!closed) deferred?.Cancel();
        }
        ReleasePaintBuffer();
        base.OnHandleDestroyed(e);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        lock (scheduleGate)
        {
            displayable = Visible; pending = false; nextPaint = 0;
            if (!closed) deferred?.Cancel();
        }
        if (displayable) ScheduleNativePaint();
    }

    bool CanRenderNative()
    {
        lock (scheduleGate) return enabled && !closed && displayable && NativeRenderingAllowed;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == PaintRequest)
        {
            if (!CanRenderNative())
            {
                lock (scheduleGate) pending = false;
            }
            else
            {
                bool repaint;
                lock (scheduleGate)
                {
                    repaint = nextPaint <= Stopwatch.GetTimestamp();
                    if (!repaint) PostOrDefer();
                }
                if (repaint)
                {
                    InvalidateRect(message.HWnd, IntPtr.Zero, false);
                    // Complete this one coalesced request now. Deferring a
                    // low-priority WM_PAINT behind further decoder/timer messages
                    // adds visible cadence gaps even when the blit itself is fast.
                    UpdateWindow(message.HWnd);
                }
            }
            message.Result = IntPtr.Zero; return;
        }
        if (message.Msg == 0x0014 && CanRenderNative())
        {
            message.Result = new IntPtr(1); return; // The paint below fills the background.
        }
        if (message.Msg != 0x000f || !CanRenderNative())
        {
            base.WndProc(ref message); return;
        }

        long started = Stopwatch.GetTimestamp(), version;
        lock (scheduleGate)
        {
            pending = true; version = requestedVersion;
            long period = (long)Math.Ceiling(Stopwatch.Frequency / (double)fpsLimit);
            if (nextPaint == 0) nextPaint = started + period;
            else if (nextPaint <= started)
            {
                // Preserve the cadence's phase instead of adding every wake-up
                // delay to the next frame. Missed slots are skipped, never replayed.
                nextPaint += ((started - nextPaint) / period + 1) * period;
            }
        }
        var dc = BeginPaint(message.HWnd, out var paint);
        bool drawn = false;
        long frameId = NativeFrameId;
        renderedFrameId = -1;
        try
        {
            if (dc != IntPtr.Zero && Width > 0 && Height > 0)
            {
                var clip = Rectangle.FromLTRB(paint.Left, paint.Top, paint.Right, paint.Bottom);
                if (clip.Width > 0 && clip.Height > 0)
                {
                    var size = ClientSize;
                    if (NativeFrameCoversViewport)
                    {
                        // The decoder's reusable DIB already contains a complete
                        // image. This path has exactly one visible StretchBlt,
                        // with no background clear before or after it.
                        drawn = TryRenderNative(dc, size, clip);
                    }
                    else
                    {
                        if (paintBuffer?.Size != size)
                        {
                            // Allocate successfully before retiring the old surface.
                            var replacement = new NativePaintBuffer(size);
                            var previous = paintBuffer; paintBuffer = replacement;
                            NativeBufferAllocations++; previous?.Dispose();
                        }
                        // Clearing the visible DC before StretchBlt can expose a
                        // black frame: intervening GDI calls flush that clear before
                        // the new image is ready. Prepare EVERYTHING off screen.
                        paintBuffer.Clear();
                        if (TryRenderNative(paintBuffer.Dc, size, new(Point.Empty, size)))
                            drawn = paintBuffer.Present(dc, clip);
                        // On failure leave the visible surface untouched.
                    }
                }
            }
        }
        finally
        {
            EndPaint(message.HWnd, ref paint);
            long elapsed = Stopwatch.GetTimestamp() - started;
            Volatile.Write(ref completePaintMilliseconds, elapsed * 1000.0 / Stopwatch.Frequency);
            if (drawn)
            {
                Interlocked.Increment(ref paintedFrames);
                Interlocked.Add(ref totalPaintTicks, elapsed);
                if (elapsed > Interlocked.Read(ref maximumPaintTicks)) Interlocked.Exchange(ref maximumPaintTicks, elapsed);
                if (renderedFrameId >= 0) frameId = renderedFrameId;
                if (frameId >= 0 && frameId != lastPaintedId)
                { lastPaintedId = frameId; Interlocked.Increment(ref presentedFrames); }
            }
            bool again;
            lock (scheduleGate) { pending = false; again = version != requestedVersion; }
            if (again) ScheduleNativePaint();
        }
        message.Result = IntPtr.Zero;
    }

    protected override void Dispose(bool disposing)
    {
        bool release = false;
        if (disposing)
        {
            lock (scheduleGate)
            {
                if (!closed) { closed = true; enabled = pending = false; window = IntPtr.Zero; release = true; }
            }
            if (release) { deferred?.Dispose(); ReleasePaintBuffer(); }
        }
        base.Dispose(disposing);
    }
    void ReleasePaintBuffer()
    {
        var previous = paintBuffer; paintBuffer = null; previous?.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)] struct PaintData
    {
        public IntPtr Dc;
        public int Erase;
        public int Left, Top, Right, Bottom;
        public int Restore, Incremental;
        public int Reserved0, Reserved1, Reserved2, Reserved3, Reserved4, Reserved5, Reserved6, Reserved7;
    }
    [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr window, out PaintData paint);
    [DllImport("user32.dll")] static extern bool EndPaint(IntPtr window, ref PaintData paint);
    [DllImport("user32.dll")] static extern bool InvalidateRect(IntPtr window, IntPtr rect, bool erase);
    [DllImport("user32.dll")] static extern bool UpdateWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    sealed class HighResolutionDelay : IDisposable
    {
        readonly Action elapsed;
        readonly ManualResetEvent stopped = new(false);
        readonly TimerWaitHandle? timer;
        readonly Task? worker;
        System.Threading.Timer? fallback;
        int disposed;
        public HighResolutionDelay(Action elapsed)
        {
            this.elapsed = elapsed;
            var handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x001f0003);
            if (handle == IntPtr.Zero) handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x001f0003);
            if (handle == IntPtr.Zero) { fallback = NewFallback(); return; }
            timer = new TimerWaitHandle(handle);
            worker = Task.Factory.StartNew(() =>
            {
                while (WaitHandle.WaitAny([stopped, timer]) == 1) elapsed();
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        System.Threading.Timer NewFallback() => new(_ => elapsed(), null, Timeout.Infinite, Timeout.Infinite);
        public void Arm(long remaining)
        {
            if (Volatile.Read(ref disposed) != 0) return;
            long due = -Math.Max(1, (long)Math.Ceiling(remaining * 10000000.0 / Stopwatch.Frequency));
            if (timer != null && SetWaitableTimer(timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)) return;
            (fallback ??= NewFallback()).Change((int)Math.Clamp(Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency), 1, 1000), Timeout.Infinite);
        }
        public void Cancel()
        {
            if (Volatile.Read(ref disposed) != 0) return;
            if (timer != null) CancelWaitableTimer(timer.SafeWaitHandle);
            fallback?.Change(Timeout.Infinite, Timeout.Infinite);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stopped.Set(); fallback?.Dispose();
            if (worker == null || worker.Wait(TimeSpan.FromSeconds(1))) { timer?.Dispose(); stopped.Dispose(); }
            else _ = worker.ContinueWith(_ => { timer?.Dispose(); stopped.Dispose(); }, TaskScheduler.Default);
        }
        sealed class TimerWaitHandle : WaitHandle
        {
            public TimerWaitHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, true);
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerEx(
            IntPtr attributes, string? name, uint flags, uint access);
        [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period,
            IntPtr completion, IntPtr argument, bool resume);
        [DllImport("kernel32.dll")] static extern bool CancelWaitableTimer(SafeWaitHandle timer);
    }
}
