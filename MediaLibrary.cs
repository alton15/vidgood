using System.Runtime.InteropServices;

namespace VideoPlayer;

public static class MediaLibrary
{
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".flv", ".m4v", ".mpg", ".mpeg",
        ".ts", ".m2ts", ".mts", ".3gp", ".ogv", ".vob", ".asf", ".rm", ".rmvb", ".divx",
    };

    public static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".smi", ".ass", ".ssa", ".vtt", ".sub",
    };

    public const string VideoFilter =
        "동영상 파일|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.webm;*.flv;*.m4v;*.mpg;*.mpeg;*.ts;*.m2ts;*.mts;*.3gp;*.ogv;*.vob;*.asf;*.rm;*.rmvb;*.divx|모든 파일|*.*";

    public const string SubtitleFilter = "자막 파일|*.srt;*.smi;*.ass;*.ssa;*.vtt;*.sub|모든 파일|*.*";

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsSubtitle(string path) => SubtitleExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Turns opened files/folders into a playlist. A single file pulls in its sibling
    /// videos (like the Movies &amp; TV app) so next/previous work right away.
    /// The order follows the Explorer window showing that folder when possible (see <see cref="ChooseOrder"/>).
    /// </summary>
    public static List<string> BuildPlaylist(IReadOnlyList<string> paths, out int startIndex, out PlaylistOrder order)
    {
        startIndex = 0;
        order = ChooseOrder(paths.Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(Path.GetFullPath(p))).FirstOrDefault());

        if (paths.Count == 1 && File.Exists(paths[0]))
        {
            var file = Path.GetFullPath(paths[0]);
            if (!AppSettings.Current.LoadFolderAsPlaylist || !IsVideo(file))
                return new List<string> { file };

            var siblings = Sort(EnumerateVideos(Path.GetDirectoryName(file)!), order);
            startIndex = siblings.FindIndex(p => string.Equals(p, file, StringComparison.OrdinalIgnoreCase));
            if (startIndex < 0)
            {
                siblings.Insert(0, file);
                startIndex = 0;
            }
            return siblings;
        }

        var result = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                result.AddRange(EnumerateVideos(path));
            else if (File.Exists(path) && IsVideo(path))
                result.Add(Path.GetFullPath(path));
        }
        return Sort(result, order);
    }

    /// <summary>Explorer's order for <paramref name="folder"/> if it is open and followed; otherwise the saved default.</summary>
    public static PlaylistOrder ChooseOrder(string? folder)
    {
        var settings = AppSettings.Current;
        if (folder != null && settings.FollowExplorerSort && ExplorerSort.TryGet(folder, out var key, out var descending))
            return new PlaylistOrder(key, descending, FromExplorer: true);
        return new PlaylistOrder(settings.PlaylistSort, settings.PlaylistDescending, FromExplorer: false);
    }

    public static List<string> Sort(IEnumerable<string> files, PlaylistOrder order)
    {
        // Name is always the tie-breaker, like Explorer.
        var byName = files.OrderBy(p => p, ExplorerOrder.Instance);
        IOrderedEnumerable<string> sorted = order.Key switch
        {
            PlaylistSort.DateModified => Order(byName, p => SafeInfo(p)?.LastWriteTimeUtc ?? DateTime.MinValue, order.Descending),
            PlaylistSort.DateCreated => Order(byName, p => SafeInfo(p)?.CreationTimeUtc ?? DateTime.MinValue, order.Descending),
            PlaylistSort.Size => Order(byName, p => SafeInfo(p)?.Length ?? 0, order.Descending),
            _ => order.Descending ? files.OrderByDescending(p => p, ExplorerOrder.Instance) : byName,
        };
        return sorted.ToList();
    }

    static IOrderedEnumerable<string> Order<T>(IEnumerable<string> source, Func<string, T> key, bool descending) =>
        descending ? source.OrderByDescending(key) : source.OrderBy(key);

    static FileInfo? SafeInfo(string path)
    {
        try { return new FileInfo(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    static List<string> EnumerateVideos(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory).Where(IsVideo).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>Sorts "ep2" before "ep10", matching File Explorer.</summary>
    sealed class ExplorerOrder : IComparer<string>
    {
        public static readonly ExplorerOrder Instance = new();

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int StrCmpLogicalW(string a, string b);

        public int Compare(string? x, string? y) =>
            StrCmpLogicalW(Path.GetFileName(x) ?? "", Path.GetFileName(y) ?? "");
    }
}

public enum PlaylistSort { Name, DateModified, DateCreated, Size }

public readonly record struct PlaylistOrder(PlaylistSort Key, bool Descending, bool FromExplorer)
{
    public static string KeyLabel(PlaylistSort key) => key switch
    {
        PlaylistSort.Name => "이름",
        PlaylistSort.DateModified => "수정한 날짜",
        PlaylistSort.DateCreated => "만든 날짜",
        _ => "크기",
    };

    public string Label => (FromExplorer ? "탐색기 순서 · " : "") + KeyLabel(Key) + (Descending ? " ↓" : " ↑");
}

/// <summary>Reads the sort column of an open File Explorer window through the Shell automation API.</summary>
static class ExplorerSort
{
    public static bool TryGet(string folder, out PlaylistSort key, out bool descending)
    {
        key = PlaylistSort.Name;
        descending = false;
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return false;

        object? shell = null;
        try
        {
            shell = Activator.CreateInstance(shellType);
            dynamic windows = ((dynamic)shell!).Windows();
            int count = windows.Count;
            for (int i = 0; i < count; i++)
            {
                string? path, columns;
                try
                {
                    dynamic window = windows.Item(i);
                    dynamic view = window.Document;  // ShellFolderView for Explorer windows
                    path = view.Folder.Self.Path;
                    columns = view.SortColumns;      // e.g. "prop:-System.DateModified;System.ItemNameDisplay;"
                }
                catch
                {
                    continue; // browser windows, Control Panel, etc.
                }
                if (path == null || !SamePath(path, folder)) continue;
                return Parse(columns, out key, out descending);
            }
        }
        catch
        {
            // Shell automation unavailable: fall back to the saved order.
        }
        finally
        {
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
        return false;
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    static bool Parse(string? columns, out PlaylistSort key, out bool descending)
    {
        key = PlaylistSort.Name;
        descending = false;
        if (string.IsNullOrEmpty(columns)) return false;

        var first = columns.StartsWith("prop:", StringComparison.OrdinalIgnoreCase) ? columns[5..] : columns;
        first = first.Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        descending = first.StartsWith('-');
        switch (first.TrimStart('-', '+'))
        {
            case "System.ItemNameDisplay" or "System.ItemName" or "System.FileName" or "System.ItemPathDisplay":
                key = PlaylistSort.Name; return true;
            case "System.DateModified" or "System.ItemDate":
                key = PlaylistSort.DateModified; return true;
            case "System.DateCreated":
                key = PlaylistSort.DateCreated; return true;
            case "System.Size":
                key = PlaylistSort.Size; return true;
            default:
                return false; // a column we can't reproduce (type, duration, …)
        }
    }
}
