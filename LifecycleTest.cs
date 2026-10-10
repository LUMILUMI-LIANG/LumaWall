namespace LumaWall;

internal static class LifecycleTest
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Run()
    {
        var primaryDisplay = new Rectangle(0, 0, 1920, 1080);
        var leftDisplay = new Rectangle(-2560, 0, 2560, 1440);
        Check(DesktopLifecycleMonitor.CoversDisplay(primaryDisplay, primaryDisplay), "Borderless fullscreen must cover its display.");
        Check(DesktopLifecycleMonitor.CoversDisplay(new(-8, -8, 1936, 1096), primaryDisplay), "Invisible resize borders must not prevent fullscreen detection.");
        Check(!DesktopLifecycleMonitor.CoversDisplay(new(0, 0, 1920, 1040), primaryDisplay), "A maximized window leaving a taskbar must not count as fullscreen.");
        Check(!DesktopLifecycleMonitor.CoversDisplay(new(1500, 0, 1920, 1080), primaryDisplay), "Partial overlap must not pause another display.");
        Check(DesktopLifecycleMonitor.CoversDisplay(leftDisplay, leftDisplay), "Negative monitor coordinates must work.");
        Check(DesktopLifecycleMonitor.CoversDisplay(new(-2560, 0, 4480, 1440), primaryDisplay) &&
            DesktopLifecycleMonitor.CoversDisplay(new(-2560, 0, 4480, 1440), leftDisplay), "A spanning fullscreen window must cover both monitors.");
        Check(!DesktopLifecycleMonitor.CoversDisplay(Rectangle.Empty, primaryDisplay), "Empty window geometry must be rejected.");
        var full = new LifecycleSnapshot(false, false, [primaryDisplay], false);
        Check(full.ShouldPause(primaryDisplay) && !full.ShouldPause(leftDisplay) && !full.ShouldPause(primaryDisplay, false), "Fullscreen pausing must be display-specific and optional.");
        Check(new LifecycleSnapshot(true, false, [], false).ShouldPause(leftDisplay, false), "Locking must pause even with fullscreen pausing disabled.");
        Check(new LifecycleSnapshot(false, true, [], false).ShouldPause(primaryDisplay, false), "Sleep must pause even with fullscreen pausing disabled.");

        string commandPath = Path.Combine(AppContext.BaseDirectory, "folder with spaces", "LumaWall.exe");
        Check(StartupRegistration.BuildCommand(commandPath) == "\"" + Path.GetFullPath(commandPath) + "\" --startup", "Startup command must quote paths and identify startup launches.");

        string instanceName = "LumaWall isolated test " + Guid.NewGuid().ToString("N");
        using var activation = new ManualResetEventSlim();
        using var primary = new SingleInstanceGuard(instanceName);
        Check(primary.IsPrimary, "The first isolated guard must own the instance mutex.");
        primary.ActivationRequested += activation.Set;
        bool secondaryPrimary = true, notified = false;
        Exception? secondaryError = null;
        // A Windows mutex is thread-owned; another thread models a separate launch without recursive acquisition.
        var secondLaunch = new Thread(() =>
        {
            try
            {
                using var secondary = new SingleInstanceGuard(instanceName);
                secondaryPrimary = secondary.IsPrimary;
                notified = secondary.NotifyExisting(3000);
            }
            catch (Exception ex) { secondaryError = ex; }
        });
        secondLaunch.Start();
        Check(secondLaunch.Join(5000), "Secondary launch notification must finish within its timeout.");
        if (secondaryError != null) throw secondaryError;
        Check(!secondaryPrimary && notified && activation.Wait(3000), "Second launch must notify the existing instance rather than become primary.");
        primary.Dispose();
        using var replacement = new SingleInstanceGuard(instanceName);
        Check(replacement.IsPrimary, "Disposing the primary must allow the next launch to acquire ownership.");

        // Real event observation is safe; tests do not trigger lock, sleep, Explorer restarts, or registry writes.
        using var owner = new Form();
        LifecycleSnapshot? observed = null;
        using var monitor = new DesktopLifecycleMonitor(owner, () => [primaryDisplay, leftDisplay], snapshot => observed = snapshot);
        monitor.Refresh();
        Check(observed != null && !observed.Locked && !observed.Suspended, "A first observation must publish its state.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "lifecycle-test.txt"),
            "PASS: fullscreen geometry; monitor-specific automatic pause; lock/suspend policy; safe system observation; startup path quoting without registry writes; session single instance and activation; released ownership.");
    }
}
