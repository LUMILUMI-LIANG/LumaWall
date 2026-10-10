namespace LumaWall;

partial class Settings
{
    public bool LegacyCategoriesRecovered { get; set; }
    public Dictionary<string, string> MediaCategories { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int WindowWidth { get; set; } = 1920;
    public int WindowHeight { get; set; } = 1200;
    public int ThumbnailWidth { get; set; } = 280;
    public double InspectorWidthRatio { get; set; }
    public double PlaylistWidthRatio { get; set; }
    public bool PlaylistSidebarVisible { get; set; }
    public int TextScale { get; set; } = 100;
    public Dictionary<string, MonitorProfile> Monitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WallpaperLayoutMode> LayoutModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Favorites { get; set; } = [];
    public Dictionary<string, DateTimeOffset> ImportedAt { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? SelectedMonitorId { get; set; }
    public bool AutoPauseFullscreen { get; set; } = true;
    public bool AutoPauseLocked { get; set; } = true;
    public bool AutoPauseSleep { get; set; } = true;
    public bool AutoRecover { get; set; } = true;
    public bool RestoreOnLaunch { get; set; }
}

class MonitorProfile
{
    public string? WallpaperKey { get; set; }
    public bool PlaylistEnabled { get; set; }
    public bool Paused { get; set; }
    public int Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public PlaylistOptions Playlist { get; set; } = new();
    public Dictionary<string, WallpaperTransform> Transforms { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, WallpaperLayoutMode> LayoutModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public MonitorProfile Copy() => new()
    {
        WallpaperKey = WallpaperKey, PlaylistEnabled = PlaylistEnabled, Paused = Paused, Volume = Volume, Muted = Muted,
        Playlist = Playlist.Normalize(), Transforms = new(Transforms, StringComparer.OrdinalIgnoreCase), LayoutModes = new(LayoutModes, StringComparer.OrdinalIgnoreCase)
    };
}

sealed class MonitorRuntime(string id, Rectangle bounds, MonitorProfile profile)
{
    public string Id { get; } = id;
    public Rectangle Bounds { get; set; } = bounds;
    public MonitorProfile Profile { get; set; } = profile;
    public PlaylistController Playlist { get; set; } = new(profile.Playlist);
    public Form? Window { get; set; }
    public Scene? Scene { get; set; }
    public Wallpaper? Active { get; set; }
    public Scene? PendingScene { get; set; }
    public Wallpaper? Preparing { get; set; }
    public CancellationTokenSource? Request { get; set; }
    public bool Rotating { get; set; }
    public bool AutoPaused { get; set; }
    public bool Recovering { get; set; }
    // The surface belongs to this desktop now, but still shows the frozen outgoing frame.
    public bool HoldingPrevious { get; set; }
    public Wallpaper? HeldWallpaper { get; set; }
    public HashSet<string> FailedKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
}
