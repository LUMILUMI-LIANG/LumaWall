using System.Diagnostics;

namespace LumaWall;

// Both decoders follow this elapsed media time. Rendering or decoding a late
// frame must never stretch the timeline and turn the clip into slow motion.
sealed class PlaybackClock
{
    readonly object gate = new();
    readonly Stopwatch elapsed = new();
    bool started, paused;

    public PlaybackClock(bool paused = false) => this.paused = paused;
    public bool IsPaused { get { lock (gate) return paused; } }
    public bool IsStarted { get { lock (gate) return started; } }
    public long PositionTicks
    {
        get { lock (gate) return (long)(elapsed.ElapsedTicks * (10_000_000.0 / Stopwatch.Frequency)); }
    }
    public void Start()
    {
        lock (gate)
        {
            if (started) return;
            started = true;
            if (!paused) elapsed.Start();
        }
    }
    public void SetPaused(bool value)
    {
        lock (gate)
        {
            paused = value;
            if (paused) elapsed.Stop(); else if (started) elapsed.Start();
        }
    }
    public void Restart()
    {
        lock (gate)
        {
            elapsed.Reset(); started = true;
            if (!paused) elapsed.Start();
        }
    }
}
