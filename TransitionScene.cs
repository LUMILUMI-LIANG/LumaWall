using System.Drawing.Imaging;
using System.Diagnostics;

namespace LumaWall;

// Blend a snapshot of the previous wallpaper over the incoming live scene.
// The same GDI surface works in the preview and Explorer's layered child window.
class TransitionScene : Scene
{
    readonly System.Windows.Forms.Timer fadeTimer = new() { Interval = 25 };
    Bitmap? previous;
    double duration, elapsed;
    long lastTick;
    protected override bool NativeRenderingAllowed => base.NativeRenderingAllowed && previous == null;
    internal double TransitionProgress => previous == null ? 1 : Math.Clamp(elapsed / duration, 0, 1);
    public TransitionScene()
    {
        fadeTimer.Tick += (_, _) =>
        {
            long now = Stopwatch.GetTimestamp();
            if (!Paused) elapsed += Stopwatch.GetElapsedTime(lastTick, now).TotalSeconds;
            lastTick = now;
            if (elapsed >= duration) ClearTransition();
            Invalidate();
        };
    }
    public void BeginTransition(Bitmap snapshot, double seconds)
    {
        ClearTransition();
        if (seconds <= 0) { snapshot.Dispose(); return; }
        previous = snapshot; duration = Math.Clamp(seconds, .05, 10); elapsed = 0;
        lastTick = Stopwatch.GetTimestamp(); fadeTimer.Start(); Invalidate();
    }
    internal void ReplacePrepared(TransitionScene prepared, Bitmap? snapshot, double seconds)
    {
        ClearTransition(); AdoptPreparedScene(prepared);
        if (snapshot != null) BeginTransition(snapshot, seconds);
    }
    void ClearTransition() { fadeTimer.Stop(); previous?.Dispose(); previous = null; ScheduleNativePaint(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var render = Stopwatch.StartNew();
        base.OnPaint(e);
        if (previous == null) return;
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = (float)(1 - TransitionProgress) });
        e.Graphics.DrawImage(previous, ClientRectangle, 0, 0, previous.Width, previous.Height, GraphicsUnit.Pixel, attributes);
        AccountRenderTime(render.Elapsed.TotalMilliseconds);
        fadeTimer.Interval = Math.Max(25, (int)Math.Ceiling(LastRenderMilliseconds) + 2);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { ClearTransition(); fadeTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
