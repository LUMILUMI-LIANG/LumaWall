namespace LumaWall;

class PlaylistOptions
{
    public List<string> Keys { get; set; } = [];
    public int IntervalSeconds { get; set; } = 60;
    public bool Shuffle { get; set; }
    public double FadeSeconds { get; set; } = 1;

    // Return a detached, validated snapshot; availability filtering is optional.
    public PlaylistOptions Normalize(IEnumerable<string>? availableKeys = null)
    {
        HashSet<string>? available = availableKeys == null ? null : new(
            availableKeys.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()),
            StringComparer.OrdinalIgnoreCase);
        return new()
        {
            Keys = (Keys ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(x => available == null || available.Contains(x)).ToList(),
            IntervalSeconds = Math.Clamp(IntervalSeconds, 5, 86400),
            Shuffle = Shuffle,
            FadeSeconds = double.IsFinite(FadeSeconds) ? Math.Clamp(FadeSeconds, 0, 10) : 1
        };
    }
}

// Chooses the next key without loading media. Only Commit starts a fresh interval.
class PlaylistController
{
    readonly Func<DateTimeOffset> clock;
    readonly Random random;
    readonly List<string> shuffleBag = [];
    PlaylistOptions options;
    DateTimeOffset? deadline;
    double pausedRemaining;
    static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    public PlaylistOptions Options => options.Normalize();
    public bool Running { get; private set; }
    public bool Paused { get; private set; }
    public bool Loading { get; private set; }
    public string? CurrentKey { get; private set; }
    public string? PendingKey { get; private set; }
    public double RemainingSeconds => !Running ? 0 : Loading ? options.IntervalSeconds
        : Paused ? pausedRemaining
        : deadline is DateTimeOffset due ? Math.Clamp((due - clock()).TotalSeconds, 0, options.IntervalSeconds) : 0;

    public PlaylistController(PlaylistOptions options, Func<DateTimeOffset>? clock = null, Random? random = null)
    {
        this.options = options.Normalize();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.random = random ?? Random.Shared;
    }

    public string? Start(string? preferredKey = null)
    {
        if (Loading) return null;
        Stop();
        if (options.Keys.Count == 0) return null;
        Running = true;
        if (options.Shuffle) RefillBag();
        var preferred = options.Keys.FirstOrDefault(x => KeyComparer.Equals(x, preferredKey));
        string key = preferred ?? ChooseNext();
        if (preferred != null && options.Shuffle) shuffleBag.RemoveAll(x => KeyComparer.Equals(x, preferred));
        return BeginLoad(key);
    }

    public string? Next()
    {
        if (Loading || options.Keys.Count == 0) return null;
        if (!Running) return Start();
        return BeginLoad(ChooseNext());
    }

    public string? Tick()
    {
        if (!Running || Paused || Loading || RemainingSeconds > 0) return null;
        return Next();
    }

    public void Commit(string key)
    {
        // A completion from an old or cancelled load cannot revive the playlist.
        if (!Running || !Loading || !KeyComparer.Equals(PendingKey, key)) return;
        CurrentKey = PendingKey;
        PendingKey = null; Loading = false;
        SetRemaining(options.IntervalSeconds);
    }

    public void Reject()
    {
        if (!Loading) return;
        PendingKey = null; Loading = false;
        if (CurrentKey == null) { Stop(); return; }
        // Keep the working wallpaper and avoid retrying a failed load every tick.
        SetRemaining(options.IntervalSeconds);
    }

    public void Stop()
    {
        Running = Paused = Loading = false;
        CurrentKey = PendingKey = null;
        deadline = null; pausedRemaining = 0; shuffleBag.Clear();
    }

    public void Pause(bool pause)
    {
        if (!Running || Paused == pause) return;
        if (pause)
        {
            pausedRemaining = RemainingSeconds;
            Paused = true; deadline = null;
        }
        else
        {
            Paused = false;
            if (!Loading) deadline = clock().AddSeconds(pausedRemaining);
        }
    }

    public void UpdateOptions(PlaylistOptions value, IEnumerable<string>? availableKeys = null)
    {
        var updated = value.Normalize(availableKeys);
        var previous = options;
        double remaining = RemainingSeconds;
        options = updated;
        if (options.Keys.Count == 0) { Stop(); return; }

        if (options.Shuffle && previous.Shuffle)
        {
            shuffleBag.RemoveAll(x => !options.Keys.Contains(x, KeyComparer));
            foreach (var key in options.Keys.Where(x => !previous.Keys.Contains(x, KeyComparer)))
                shuffleBag.Insert(random.Next(shuffleBag.Count + 1), key);
        }
        else
        {
            shuffleBag.Clear();
            if (options.Shuffle)
            {
                RefillBag();
                // Current/pending keys already belong to the current rotation.
                shuffleBag.RemoveAll(x => KeyComparer.Equals(x, CurrentKey) || KeyComparer.Equals(x, PendingKey));
            }
        }

        if (CurrentKey != null && !options.Keys.Contains(CurrentKey, KeyComparer)) CurrentKey = null;
        bool pendingRemoved = PendingKey != null && !options.Keys.Contains(PendingKey, KeyComparer);
        if (pendingRemoved) { PendingKey = null; Loading = false; }
        if (!Running || Loading) return;
        if (CurrentKey == null) SetRemaining(0);
        else SetRemaining(previous.IntervalSeconds != options.IntervalSeconds || pendingRemoved ? options.IntervalSeconds : remaining);
    }

    string BeginLoad(string key)
    {
        PendingKey = key; Loading = true; deadline = null; pausedRemaining = options.IntervalSeconds;
        return key;
    }

    string ChooseNext()
    {
        if (!options.Shuffle)
        {
            int index = options.Keys.FindIndex(x => KeyComparer.Equals(x, CurrentKey));
            return options.Keys[(index + 1) % options.Keys.Count];
        }
        if (shuffleBag.Count == 0) RefillBag();
        // Reordering the first item preserves every member of the new cycle.
        if (shuffleBag.Count > 1 && KeyComparer.Equals(shuffleBag[0], CurrentKey))
        {
            int other = random.Next(1, shuffleBag.Count);
            (shuffleBag[0], shuffleBag[other]) = (shuffleBag[other], shuffleBag[0]);
        }
        string key = shuffleBag[0]; shuffleBag.RemoveAt(0); return key;
    }

    void RefillBag()
    {
        shuffleBag.Clear(); shuffleBag.AddRange(options.Keys);
        for (int i = shuffleBag.Count - 1; i > 0; i--)
        {
            int other = random.Next(i + 1);
            (shuffleBag[i], shuffleBag[other]) = (shuffleBag[other], shuffleBag[i]);
        }
    }

    void SetRemaining(double seconds)
    {
        pausedRemaining = Math.Max(0, seconds);
        deadline = Paused ? null : clock().AddSeconds(pausedRemaining);
    }
}
