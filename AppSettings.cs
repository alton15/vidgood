using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoPlayer;

public sealed class AppSettings
{
    public static readonly int[] SeekOptions = { 3, 5, 10, 15, 30 };

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoPlayer", "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; } = Load();

    /// <summary>Raised after the settings dialog saves, so open panes can refresh.</summary>
    public static event Action? Changed;

    public int SeekSeconds { get; set; } = 5;
    public int Volume { get; set; } = 80;
    public bool AutoPlayNext { get; set; } = true;
    public bool LoadFolderAsPlaylist { get; set; } = true;
    public bool ResumePlayback { get; set; } = true;

    /// <summary>Jump to the nearest keyframe instead of decoding up to the exact time (instant, less precise).</summary>
    public bool FastSeek { get; set; }

    /// <summary>With several panes open, only the selected pane plays sound and it follows selection.</summary>
    public bool AudioFollowsActive { get; set; }

    /// <summary>Playlist order when Explorer's order can't be used (or isn't followed).</summary>
    public PlaylistSort PlaylistSort { get; set; } = PlaylistSort.DateModified;
    public bool PlaylistDescending { get; set; }
    /// <summary>Use the sort order of the Explorer window the file was opened from, when there is one.</summary>
    public bool FollowExplorerSort { get; set; } = true;

    /// <summary>Shortcut overrides: action name → keys like "Ctrl+T". Actions not listed use their defaults.</summary>
    public Dictionary<string, List<string>> KeyBindings { get; set; } = new();

    /// <summary>File path → last position in milliseconds.</summary>
    public Dictionary<string, long> ResumePositions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (loaded != null)
                {
                    loaded.ResumePositions = new Dictionary<string, long>(loaded.ResumePositions, StringComparer.OrdinalIgnoreCase);
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings: fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // Settings are a convenience; never crash the player over them.
        }
    }

    public static void RaiseChanged() => Changed?.Invoke();
}
