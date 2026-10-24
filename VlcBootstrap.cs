using System.IO.Compression;
using System.Reflection;
using System.Windows;
using LibVLCSharp.Shared;

namespace VideoPlayer;

/// <summary>
/// Locates libVLC before the first engine is created. Development builds use the loose folder
/// next to the exe; the single-file release carries it as an embedded zip that is unpacked once
/// per VLC version into %LOCALAPPDATA%\VideoPlayer\vlc (so app updates don't unpack it again).
/// </summary>
static class VlcBootstrap
{
    const string ResourceName = "libvlc-win-x64.zip";

    static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoPlayer", "vlc");

    /// <summary>Returns false when VLC could not be prepared; the user has already been told why.</summary>
    public static async Task<bool> InitializeAsync()
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64", "libvlc.dll")))
        {
            Core.Initialize();
            return true;
        }

        using var zip = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (zip == null)
        {
            Core.Initialize(); // no bundled copy: let LibVLCSharp search and report
            return true;
        }

        var version = typeof(VlcBootstrap).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "VlcVersion")?.Value ?? "vlc";
        // The size changes whenever the bundled plugin set does, so it is part of the folder name.
        var target = Path.Combine(Root, $"{version}-{zip.Length}");

        if (!File.Exists(Path.Combine(target, "libvlc.dll")))
        {
            var progress = new SetupWindow();
            progress.Show();
            try
            {
                await Task.Run(() => Extract(zip, target, progress.Report));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                progress.Close();
                MessageBox.Show($"재생 엔진을 준비하지 못했습니다.\n\n{ex.Message}", "VidGood",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            progress.Close();
        }

        _ = Task.Run(() => CleanUp(target));
        Core.Initialize(target);
        return true;
    }

    static void Extract(Stream zipStream, string target, Action<double> report)
    {
        Directory.CreateDirectory(Root);
        // Unpack beside the target and rename at the end, so a half-written folder is never used
        // (and two instances starting at once don't trip over each other).
        var staging = $"{target}.partial-{Guid.NewGuid():N}";
        var stagingRoot = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;

        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true))
        {
            long total = Math.Max(1, archive.Entries.Sum(e => e.Length)), done = 0;
            foreach (var entry in archive.Entries)
            {
                var path = Path.GetFullPath(Path.Combine(staging, entry.FullName));
                if (!path.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    Directory.CreateDirectory(path);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
                done += entry.Length;
                report((double)done / total);
            }
        }

        try
        {
            Directory.Move(staging, target);
        }
        catch (IOException) when (File.Exists(Path.Combine(target, "libvlc.dll")))
        {
            TryDelete(staging); // another instance finished first
        }
    }

    /// <summary>Removes other VLC versions and the old 220 MB full extractions of earlier builds.</summary>
    static void CleanUp(string current)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(Root))
                if (!string.Equals(dir, current, StringComparison.OrdinalIgnoreCase)) TryDelete(dir);

            // Builds before 1.0.1 made the .NET host unpack everything, libVLC included, into %TEMP%\.net.
            var hostCache = Path.Combine(Path.GetTempPath(), ".net", "VideoPlayer");
            if (Directory.Exists(hostCache))
                foreach (var dir in Directory.EnumerateDirectories(hostCache))
                    if (Directory.Exists(Path.Combine(dir, "libvlc"))) TryDelete(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort; files of a still-running older instance stay until next time.
        }
    }

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
