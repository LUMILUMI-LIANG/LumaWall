using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace LumaWall;

internal sealed record LifecycleSnapshot(bool Locked, bool Suspended,
    IReadOnlyList<Rectangle> FullscreenDisplays, bool RecoveryRequested)
{
    public bool ShouldPause(Rectangle displayBounds, bool pauseOnFullscreen = true) =>
        Locked || Suspended || (pauseOnFullscreen && FullscreenDisplays.Contains(displayBounds));
}

// Observes system state only. It never activates, resizes, or changes another application's window.
internal sealed class DesktopLifecycleMonitor : IDisposable
{
    readonly Form owner;
    readonly Func<IEnumerable<Rectangle>> wallpaperBounds;
    readonly Action<LifecycleSnapshot> callback;
    readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    volatile bool locked, suspended;
    int recoveryPending;
    volatile bool disposed;
    bool initialized;
    (IntPtr Program, uint Process, IntPtr Icons) shell;
    LifecycleSnapshot? previous;

    public DesktopLifecycleMonitor(Form owner, Func<IEnumerable<Rectangle>> wallpaperBounds,
        Action<LifecycleSnapshot> callback)
    {
        this.owner = owner;
        this.wallpaperBounds = wallpaperBounds;
        this.callback = callback;
        SystemEvents.SessionSwitch += SessionChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        timer.Tick += (_, _) => Refresh();
        timer.Start();
    }

    void SessionChanged(object sender, SessionSwitchEventArgs args)
    {
        if (args.Reason == SessionSwitchReason.SessionLock) locked = true;
        else if (args.Reason == SessionSwitchReason.SessionUnlock)
        {
            locked = false;
            Interlocked.Exchange(ref recoveryPending, 1);
        }
        RequestRefresh();
    }

    void PowerChanged(object sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend) suspended = true;
        else if (args.Mode == PowerModes.Resume)
        {
            suspended = false;
            Interlocked.Exchange(ref recoveryPending, 1);
        }
        RequestRefresh();
    }

    void DisplaysChanged(object? sender, EventArgs args)
    {
        Interlocked.Exchange(ref recoveryPending, 1);
        RequestRefresh();
    }

    void RequestRefresh()
    {
        if (disposed || owner.IsDisposed || !owner.IsHandleCreated) return;
        try { owner.BeginInvoke((Action)Refresh); }
        catch (InvalidOperationException) { }
    }

    // Call on the owner's UI thread when a new wallpaper has been applied.
    public void Refresh()
    {
        if (disposed || owner.IsDisposed || owner.Disposing) return;
        var currentShell = ReadShell();
        bool recover = Interlocked.Exchange(ref recoveryPending, 0) != 0;
        if (initialized && shell != currentShell) recover = true;
        shell = currentShell;
        initialized = true;
        var displays = DetectFullscreenDisplays(wallpaperBounds().Distinct().ToArray());
        var snapshot = new LifecycleSnapshot(locked, suspended, displays, recover);
        if (recover || previous == null || previous.Locked != snapshot.Locked ||
            previous.Suspended != snapshot.Suspended || !previous.FullscreenDisplays.SequenceEqual(displays))
        {
            previous = snapshot;
            callback(snapshot);
        }
    }

    internal static bool CoversDisplay(Rectangle window, Rectangle display, int tolerance = 2) =>
        window.Width > 0 && window.Height > 0 && display.Width > 0 && display.Height > 0 &&
        window.Left <= display.Left + tolerance && window.Top <= display.Top + tolerance &&
        window.Right >= display.Right - tolerance && window.Bottom >= display.Bottom - tolerance;

    static Rectangle[] DetectFullscreenDisplays(Rectangle[] displays)
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || !IsWindowVisible(foreground) || IsIconic(foreground)) return [];
        GetWindowThreadProcessId(foreground, out uint pid);
        if (pid == Environment.ProcessId) return [];
        var className = new StringBuilder(128);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return [];
        if (!GetWindowRect(foreground, out var bounds)) return [];
        var rectangle = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        return displays.Where(display => CoversDisplay(rectangle, display)).ToArray();
    }

    static (IntPtr, uint, IntPtr) ReadShell()
    {
        var program = FindWindow("Progman", null);
        if (program == IntPtr.Zero) return (IntPtr.Zero, 0, IntPtr.Zero);
        GetWindowThreadProcessId(program, out uint pid);
        var icons = FindWindowEx(program, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (icons == IntPtr.Zero)
            EnumWindows((window, _) =>
            {
                icons = FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null);
                return icons == IntPtr.Zero;
            }, IntPtr.Zero);
        return (program, pid, icons);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        timer.Dispose();
        SystemEvents.SessionSwitch -= SessionChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
    }

    delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [StructLayout(LayoutKind.Sequential)] struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out NativeRectangle bounds);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr window, StringBuilder name, int maximum);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
}

// Keep this guard alive for the entire message loop and dispose it on the startup thread.
// Its product/session identity is independent of the executable's version or directory.
internal sealed class SingleInstanceGuard : IDisposable
{
    readonly Mutex mutex;
    readonly string pipeName;
    readonly CancellationTokenSource lifetime = new();
    readonly object gate = new();
    NamedPipeServerStream? activeServer;
    readonly Task? listener;
    bool disposed;
    public bool IsPrimary { get; }
    // Raised on the pipe listener thread; the application must marshal its UI action.
    public event Action? ActivationRequested;

    public SingleInstanceGuard(string? instanceName = null)
    {
        string identity = instanceName ?? DefaultIdentity();
        string suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        pipeName = "LumaWall-" + suffix;
        mutex = new Mutex(false, "Local\\" + pipeName);
        try { IsPrimary = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsPrimary = true; }
        if (IsPrimary) listener = Task.Run(ListenAsync);
    }

    static string DefaultIdentity()
    {
        using var user = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        return "LumaWall:" + user.User?.Value + ":" + process.SessionId;
    }

    public bool NotifyExisting(int timeoutMilliseconds = 1500)
    {
        if (IsPrimary || disposed) return false;
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(Math.Max(1, timeoutMilliseconds));
            using var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            writer.WriteLine("activate");
            return true;
        }
        catch (IOException) { return false; }
        catch (TimeoutException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    async Task ListenAsync()
    {
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                lock (gate)
                {
                    if (disposed) return;
                    activeServer = server;
                }
                await server.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(server, Encoding.UTF8, false, leaveOpen: true);
                if (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false) == "activate" && !lifetime.IsCancellationRequested)
                    ActivationRequested?.Invoke();
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { lock (gate) activeServer = null; }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            activeServer?.Dispose();
        }
        if (IsPrimary) mutex.ReleaseMutex();
        mutex.Dispose();
        // Observe completion and keep the token source alive until all pending I/O has unwound.
        if (listener != null) _ = listener.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default);
        else lifetime.Dispose();
    }
}

internal static class StartupRegistration
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "LumaWall";
    internal static string BuildCommand(string exePath)
    {
        string fullPath = Path.GetFullPath(exePath);
        if (fullPath.Contains('"')) throw new ArgumentException("启动路径不能包含引号。", nameof(exePath));
        return "\"" + fullPath + "\" --startup";
    }

    public static bool IsEnabled(string exePath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
        return key?.GetValue(ValueName) is string value && string.Equals(value, BuildCommand(exePath), StringComparison.OrdinalIgnoreCase);
    }

    // Called only in response to the user's startup preference; no default writes occur.
    public static void SetEnabled(string exePath, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, true);
        if (enabled) key.SetValue(ValueName, BuildCommand(exePath), RegistryValueKind.String);
        else key.DeleteValue(ValueName, false);
    }
}
