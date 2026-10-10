using System.Diagnostics;

namespace LumaWall;

static class FrameTimingTest
{
    public static void Run()
    {
        var report = new List<string>();
        string root = Path.Combine(AppContext.BaseDirectory, "frame-timing-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var stop = new CancellationTokenSource(); using var changed = new AutoResetEvent(false);
            using (var wait = new FrameDeadlineWait(stop.Token, changed))
                foreach (long ticks in new long[] { 20000, 40000, 83333 })
                {
                    var times = new List<double>();
                    for (int i = 0; i < 30; i++) { var watch = Stopwatch.StartNew(); wait.Wait(ticks); times.Add(watch.Elapsed.TotalMilliseconds); }
                    report.Add($"Wait {ticks / 10000.0:F3}ms: median={times.Order().ElementAt(15):F3}ms; max={times.Max():F3}ms");
                }
            foreach (var size in new[] { new Size(1280, 720), new Size(1920, 1080) })
            {
            string path = Path.Combine(root, $"native120-{size.Width}.mp4");
            Task.Run(() => PerformanceFixture.Create(path, size.Width, size.Height, 120, 3, true)).GetAwaiter().GetResult();
            report.Add($"Source {size.Width}x{size.Height}/120fps");
            foreach (bool hardware in new[] { true, false })
                report.Add(Task.Run(() =>
                {
                    using var decoder = new VideoDecoder(path, 0, hardware); using var pool = new VideoFramePool();
                    var costs = new List<double>(); var timestamps = new List<string>();
                    for (int i = 0; i < 100; i++)
                    {
                        var watch = Stopwatch.StartNew(); using var frame = decoder.ReadFrame(default, pool)!;
                        costs.Add(watch.Elapsed.TotalMilliseconds);
                        if (i < 15) timestamps.Add($"{frame.Timestamp / 10000.0:F2}/{frame.Duration / 10000.0:F2}ms/{costs[^1]:F2}ms");
                    }
                    return $"Reader hardware={decoder.Accelerated}: raw decode+copy median={costs.Order().ElementAt(50):F3}ms; P95={costs.Order().ElementAt(95):F3}ms; first timestamps/durations/cost=" + string.Join(", ", timestamps);
                }).GetAwaiter().GetResult());
            using var playback = new VideoPlayback(path, true);
            playback.Ready.GetAwaiter().GetResult(); long first = playback.FrameCount;
            playback.Paused = false; var played = Stopwatch.StartNew(); Thread.Sleep(2200); playback.Paused = true;
            report.Add($"Playback: published={(playback.FrameCount - first) / played.Elapsed.TotalSeconds:F2}fps; dropped={playback.DroppedFrameCount}; lag={(playback.Clock.PositionTicks - playback.PresentedPositionTicks) / 10000.0:F2}ms; error={playback.Error}; gpu={playback.Accelerated}");
            }
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "frame-timing-test.txt"), string.Join("\n", report));
        }
        finally { TestFiles.RemoveFixture(root); }
    }
}
