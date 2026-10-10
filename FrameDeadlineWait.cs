using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace LumaWall;

// Wait on the decoding thread itself. Millisecond timeout waits can wake a
// 120fps sample a full 15.6ms late; this timer uses the media's 100ns deadline.
sealed class FrameDeadlineWait : IDisposable
{
    sealed class TimerHandle : WaitHandle
    {
        public TimerHandle(IntPtr handle) => SafeWaitHandle = new SafeWaitHandle(handle, true);
    }
    readonly CancellationToken token;
    readonly TimerHandle? timer;
    readonly WaitHandle[] signals;

    public FrameDeadlineWait(CancellationToken token, WaitHandle changed)
    {
        this.token = token;
        IntPtr handle = CreateWaitableTimerEx(IntPtr.Zero, null, 2, 0x001f0003);
        if (handle == IntPtr.Zero) handle = CreateWaitableTimerEx(IntPtr.Zero, null, 0, 0x001f0003);
        if (handle != IntPtr.Zero) timer = new(handle);
        signals = timer == null ? [token.WaitHandle, changed] : [token.WaitHandle, changed, timer];
    }

    public void Wait(long remainingTicks)
    {
        token.ThrowIfCancellationRequested();
        long due = -Math.Clamp(remainingTicks, 1, 500000);
        int result;
        if (timer != null && SetWaitableTimer(timer.SafeWaitHandle, ref due, 0, IntPtr.Zero, IntPtr.Zero, false))
        {
            result = WaitHandle.WaitAny(signals);
            if (result != 2) CancelWaitableTimer(timer.SafeWaitHandle);
        }
        else result = WaitHandle.WaitAny(signals, (int)Math.Clamp((-due + 9999) / 10000, 1, 50));
        if (result == 0) token.ThrowIfCancellationRequested();
    }

    public void Dispose() => timer?.Dispose();
    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerEx(IntPtr attributes, string? name, uint flags, uint access);
    [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(SafeWaitHandle timer, ref long due, int period, IntPtr completion, IntPtr argument, bool resume);
    [DllImport("kernel32.dll")] static extern bool CancelWaitableTimer(SafeWaitHandle timer);
}
