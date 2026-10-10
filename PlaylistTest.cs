using System.Text.Json;

namespace LumaWall;

static class PlaylistTest
{
    sealed class ManualClock
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Read() => Now;
        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);
    }
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    static PlaylistOptions Options(bool shuffle = false) => new() { Keys = ["a", "b", "c"], IntervalSeconds = 60, Shuffle = shuffle, FadeSeconds = .75 };

    public static void Run()
    {
        var normalized = new PlaylistOptions { Keys = [" a ", "A", "", "b", "c"], IntervalSeconds = 1, FadeSeconds = double.NaN }.Normalize(["a", "b"]);
        Check(normalized.Keys.SequenceEqual(["a", "b"]) && normalized.IntervalSeconds == 5 && normalized.FadeSeconds == 1, "Normalize must filter, deduplicate and validate options.");
        var bounds = new PlaylistOptions { IntervalSeconds = int.MaxValue, FadeSeconds = 20 }.Normalize();
        Check(bounds.IntervalSeconds == 86400 && bounds.FadeSeconds == 10, "Option upper limits must be enforced.");
        var source = Options(); var snapshot = source.Normalize(); source.Keys.Clear();
        Check(snapshot.Keys.Count == 3, "Normalized keys must be detached from mutable source options.");
        var persisted = JsonSerializer.Deserialize<PlaylistOptions>(JsonSerializer.Serialize(snapshot))!.Normalize();
        Check(persisted.Keys.SequenceEqual(["a", "b", "c"]) && persisted.IntervalSeconds == 60 && persisted.FadeSeconds == .75, "Playlist configuration must survive JSON persistence.");

        TestSequenceAndLoading(); TestPause(); TestShuffle(); TestReplacementAndRejection();
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "playlist-test.txt"),
            "PASS: option validation/availability/detached copies; JSON persistence; preferred and sequential playback; commit-based countdown; duplicate-load prevention; stale completion protection; pause/resume including loading; shuffle bags and cycle-boundary repeat prevention; one-item playlist; member replacement/deletion; interval updates; rejection and stop.");
    }

    static void TestSequenceAndLoading()
    {
        var clock = new ManualClock(); var controller = new PlaylistController(Options(), clock.Read);
        Check(controller.Start("b") == "b" && controller.Running && controller.Loading && controller.CurrentKey == null, "Start must request preferred member without committing it.");
        clock.Advance(200);
        Check(controller.Tick() == null && controller.Next() == null && controller.Start() == null && controller.RemainingSeconds == 60, "A pending load must not count down or start another load.");
        controller.Commit("a"); Check(controller.Loading, "Stale/nonpending completion must be ignored.");
        controller.Commit("B"); Check(controller.CurrentKey == "b" && !controller.Loading, "Commit must use canonical key case-insensitively.");
        clock.Advance(20); Check(controller.RemainingSeconds == 40 && controller.Tick() == null, "Committed interval must count down.");
        clock.Advance(40); Check(controller.Tick() == "c" && controller.Tick() == null, "Exactly one automatic next request must occur when due.");
        clock.Advance(200); Check(controller.Tick() == null, "Slow external load must not produce more requests.");
        controller.Commit("c"); Check(controller.RemainingSeconds == 60 && controller.Next() == "a", "Commit must restart full interval and sequential next must wrap.");
        controller.Stop(); controller.Commit("a");
        Check(!controller.Running && !controller.Loading && controller.CurrentKey == null && controller.PendingKey == null && controller.RemainingSeconds == 0, "Stop must clear state and ignore in-flight completion.");
        Check(new PlaylistController(new()).Start() == null, "Empty playlist must be a harmless no-op.");
    }

    static void TestPause()
    {
        var clock = new ManualClock(); var controller = new PlaylistController(Options(), clock.Read);
        controller.Commit(controller.Start()!); clock.Advance(17); controller.Pause(true); clock.Advance(200);
        Check(controller.Paused && controller.RemainingSeconds == 43 && controller.Tick() == null, "Pause must freeze the exact remaining interval.");
        controller.Pause(false); clock.Advance(42); Check(controller.Tick() == null, "Resume must preserve remaining countdown.");
        clock.Advance(1); Check(controller.Tick() == "b", "Resumed deadline must request next at the correct instant.");
        controller.Pause(true); controller.Commit("b"); clock.Advance(200);
        Check(controller.CurrentKey == "b" && controller.RemainingSeconds == 60 && controller.Tick() == null, "Commit while paused must remain paused for a full interval.");
        controller.Pause(false); clock.Advance(60); Check(controller.Tick() == "c", "Resuming a committed pending load must start its full interval.");
    }

    static void TestShuffle()
    {
        foreach (int seed in Enumerable.Range(0, 20))
        {
            var options = Options(true); options.Keys.Add("d");
            var controller = new PlaylistController(options, random: new Random(seed));
            var played = new List<string>();
            for (int i = 0; i < 24; i++)
            {
                string key = (i == 0 ? controller.Start("c") : controller.Next())!;
                if (i > 0) Check(key != played[^1], "Shuffle must not repeat at adjacent positions or bag boundaries.");
                played.Add(key); controller.Commit(key);
            }
            for (int i = 0; i < played.Count; i += 4)
                Check(played.Skip(i).Take(4).ToHashSet().SetEquals(options.Keys), "Every shuffle cycle must contain each member exactly once.");
        }
        var single = new PlaylistController(new() { Keys = ["only"], Shuffle = true });
        single.Commit(single.Start()!); Check(single.Next() == "only", "Single-member shuffle must remain playable.");
    }

    static void TestReplacementAndRejection()
    {
        var clock = new ManualClock(); var controller = new PlaylistController(Options(), clock.Read);
        controller.Start(); controller.Reject(); Check(!controller.Running && !controller.Loading, "Initial load rejection must stop cleanly.");
        controller.Commit(controller.Start()!); clock.Advance(20);
        controller.UpdateOptions(new() { Keys = ["a", "c", "d"] });
        Check(controller.CurrentKey == "a" && controller.RemainingSeconds == 40 && controller.Next() == "c", "Member edits must preserve current/countdown and respect updated order.");
        controller.Reject(); Check(controller.CurrentKey == "a" && controller.RemainingSeconds == 60 && controller.Tick() == null, "Rejected next item must retain current wallpaper and defer retry.");
        controller.Next(); controller.UpdateOptions(new() { Keys = ["a", "d"] }); controller.Commit("c");
        Check(!controller.Loading && controller.CurrentKey == "a" && controller.RemainingSeconds == 60, "Removing pending member must cancel it and ignore stale completion.");
        Check(controller.Next() == "d", "A cleaned playlist must select a surviving member.");
        controller.UpdateOptions(new() { Keys = ["d"] }); controller.Commit("d");
        Check(controller.CurrentKey == "d" && controller.Running, "Removing current member must not cancel a surviving pending member.");
        controller.UpdateOptions(new() { Keys = ["d"], IntervalSeconds = 10 });
        Check(controller.RemainingSeconds == 10, "Changing interval must restart countdown using the new interval.");
        controller.Pause(true); controller.UpdateOptions(new() { Keys = ["b"], IntervalSeconds = 10 });
        Check(controller.CurrentKey == null && controller.Tick() == null && controller.Paused, "Deleting current member during pause must preserve pause.");
        controller.Pause(false); Check(controller.Tick() == "b", "Removing current member must request a replacement upon resume.");
        controller.UpdateOptions(new() { Keys = ["b"] }, []); controller.Commit("b");
        Check(!controller.Running && !controller.Loading && controller.CurrentKey == null, "Removing all available members must stop and ignore late completion.");

        var immutable = new PlaylistController(Options()); var exposed = immutable.Options; exposed.Keys.Clear();
        Check(immutable.Options.Keys.Count == 3, "Exposed options must not allow accidental controller mutation.");
        var shuffled = new PlaylistController(Options(true), random: new Random(7)); shuffled.Commit(shuffled.Start()!);
        string current = shuffled.CurrentKey!;
        shuffled.UpdateOptions(new() { Keys = [current, "new"], Shuffle = true });
        Check(shuffled.Next() == "new", "Shuffle replacement must include newly added members without replaying current.");
    }
}
