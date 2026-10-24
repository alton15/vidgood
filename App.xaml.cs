using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using LibVLCSharp.Shared;

namespace VideoPlayer;

/// <summary>
/// Owns the VLC engine and every player window, and coordinates what spans windows:
/// moving panes between them and deciding which panes make sound.
/// </summary>
public partial class App : Application
{
    readonly List<MainWindow> _windows = new();
    PlayerPane? _activePane;
    int _windowCounter;

    static readonly Dictionary<int, LibVLC> RotatedEngines = new();

    /// <summary>Shared VLC engine; every pane creates its own MediaPlayer from it.</summary>
    public static LibVLC LibVLC { get; private set; } = null!;

    /// <summary>
    /// libVLC 3 only applies video filters per engine (media options are ignored for them),
    /// so each rotation angle gets its own lazily created engine.
    /// </summary>
    public static LibVLC EngineFor(int rotation)
    {
        if (rotation == 0) return LibVLC;
        if (!RotatedEngines.TryGetValue(rotation, out var engine))
        {
            engine = new LibVLC("--no-video-title-show", "--video-filter=transform", $"--transform-type={rotation}");
            AttachDiagnosticLog(engine);
            RotatedEngines[rotation] = engine;
        }
        return engine;
    }

    static StreamWriter? _diagnosticLog;
    static readonly System.Diagnostics.Stopwatch DiagnosticClock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Writes an app-side marker into the diagnostic log, if enabled.</summary>
    public static void Diag(string message)
    {
        var log = _diagnosticLog;
        if (log == null) return;
        lock (log) log.WriteLine($"{DiagnosticClock.ElapsedMilliseconds,8} [APP] {message}");
    }

    /// <summary>Set VP_VLC_LOG to a file path to capture libVLC's debug log (for troubleshooting playback).</summary>
    static void AttachDiagnosticLog(LibVLC engine)
    {
        var path = Environment.GetEnvironmentVariable("VP_VLC_LOG");
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            _diagnosticLog ??= new StreamWriter(path) { AutoFlush = true };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var log = _diagnosticLog;
        engine.Log += (_, e) =>
        {
            lock (log) log.WriteLine($"{DiagnosticClock.ElapsedMilliseconds,8} [{e.Level}] {e.Module}: {e.Message}");
        };
    }

    public static bool IsShuttingDown { get; set; }

    public static new App Current => (App)Application.Current;

    public IReadOnlyList<MainWindow> PlayerWindows => _windows;

    public IEnumerable<PlayerPane> AllPanes => _windows.SelectMany(w => w.Panes);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (!await VlcBootstrap.InitializeAsync())
        {
            Shutdown(1);
            return;
        }
        try
        {
            LibVLC = new LibVLC("--no-video-title-show");
        }
        catch (VLCException ex)
        {
            // Tell the user instead of exiting silently (e.g. a broken engine folder).
            MessageBox.Show($"재생 엔진을 시작하지 못했습니다.\n\n{ex.Message}", "VidGood",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        AttachDiagnosticLog(LibVLC);

        var window = OpenWindow();
        if (e.Args.Length > 0) window.OpenWhenReady(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var engine in RotatedEngines.Values) engine.Dispose();
        (LibVLC as IDisposable)?.Dispose(); // null if startup was aborted
        base.OnExit(e);
    }

    // ───────────── Windows ─────────────

    /// <param name="pane">Existing pane to host (moved from another window), or null for a new empty one.</param>
    /// <param name="screenPoint">Where the window should appear, in device pixels (e.g. the drop point).</param>
    public MainWindow OpenWindow(PlayerPane? pane = null, Point? screenPoint = null)
    {
        var previous = _windows.LastOrDefault();
        var window = new MainWindow(pane ?? CreatePane());
        _windowCounter++;
        window.Title = _windowCounter == 1 ? "VidGood" : $"VidGood {_windowCounter}";

        if (screenPoint is Point point && previous != null)
        {
            var dip = PresentationSource.FromVisual(previous)?.CompositionTarget?.TransformFromDevice.Transform(point) ?? point;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Width = 860;
            window.Height = 520;
            window.Left = dip.X - 160;
            window.Top = dip.Y - 20;
        }
        else if (previous != null)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Width = previous.ActualWidth;
            window.Height = previous.ActualHeight;
            window.Left = previous.Left + 40;
            window.Top = previous.Top + 40;
        }

        _windows.Add(window);
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (_windows.Count == 0)
            {
                AppSettings.Current.Save();
                Shutdown();
            }
            else
            {
                RefreshPanes();
            }
        };
        window.Show();
        window.SetActive(window.Panes.First());
        RefreshPanes();
        return window;
    }

    public MainWindow? OwnerOf(PlayerPane pane) => _windows.FirstOrDefault(w => w.Contains(pane));

    /// <summary>Creates a pane and routes its events to whichever window holds it at the time.</summary>
    public PlayerPane CreatePane()
    {
        var pane = new PlayerPane();
        pane.ActivateRequested += (_, _) => OwnerOf(pane)?.SetActive(pane);
        pane.CloseRequested += (_, _) => OwnerOf(pane)?.ClosePane(pane);
        pane.FullscreenToggleRequested += (_, _) => OwnerOf(pane)?.ToggleFullscreen();
        pane.OverlayKeyDown += (_, e) => OwnerOf(pane)?.HandleKey(e);
        pane.LayoutMenuRequested += (_, target) => OwnerOf(pane)?.ShowLayoutMenu(pane, target);
        pane.PaneDropped += (_, e) => MovePane(e.Source, pane, e.Side);
        pane.TearOffRequested += (_, _) => TearOff(pane);
        pane.AudioToggledByUser += (_, _) => SetAudioFollowsActive(false);
        if (AppSettings.Current.AudioFollowsActive) pane.AudioEnabled = false;
        return pane;
    }

    /// <summary>Called after any layout change in any window.</summary>
    public void RefreshPanes()
    {
        bool several = AllPanes.Skip(1).Any();
        foreach (var window in _windows) window.RefreshChrome(several);
    }

    // ───────────── Moving panes ─────────────

    /// <summary>Drag &amp; drop result: dock <paramref name="source"/> beside <paramref name="target"/>, or swap on Center.</summary>
    public void MovePane(PlayerPane source, PlayerPane target, DockSide side)
    {
        var from = OwnerOf(source);
        var to = OwnerOf(target);
        if (from == null || to == null || source == target) return;

        if (side == DockSide.Center)
        {
            if (from == to)
            {
                from.Layout.SwapPanes(source, target);
            }
            else
            {
                from.Layout.ReplacePane(source, target);
                to.Layout.ReplacePane(target, source);
            }
        }
        else
        {
            from.Layout.Remove(source);
            to.Layout.Add(source, target, side);
        }

        if (from != to)
        {
            from.RebuildLayout();
            to.RebuildLayout();
            // Their native video windows changed parent windows.
            source.ReattachVideo();
            if (side == DockSide.Center) target.ReattachVideo();
        }
        else
        {
            to.RebuildLayout();
        }
        if (from.Layout.Count == 0) from.Close();
        to.SetActive(source);
    }

    public void MoveToWindow(PlayerPane pane, MainWindow destination)
    {
        var from = OwnerOf(pane);
        if (from == null || from == destination) return;

        from.Layout.Remove(pane);
        destination.Layout.Add(pane, destination.ActivePane, DockSide.Right);
        from.RebuildLayout();
        destination.RebuildLayout();
        pane.ReattachVideo();
        if (from.Layout.Count == 0) from.Close();
        destination.SetActive(pane);
        destination.Activate();
    }

    public void MoveToNewWindow(PlayerPane pane, Point? screenPoint = null)
    {
        var from = OwnerOf(pane);
        if (from == null || from.Layout.Count <= 1) return;

        from.Layout.Remove(pane);
        from.RebuildLayout();
        OpenWindow(pane, screenPoint);
        pane.ReattachVideo();
    }

    /// <summary>A pane dragged out and released over no player window becomes its own window.</summary>
    void TearOff(PlayerPane pane)
    {
        GetCursorPos(out var cursor);
        foreach (var window in _windows)
        {
            GetWindowRect(new WindowInteropHelper(window).Handle, out var r);
            if (cursor.X >= r.Left && cursor.X < r.Right && cursor.Y >= r.Top && cursor.Y < r.Bottom) return;
        }
        MoveToNewWindow(pane, new Point(cursor.X, cursor.Y));
    }

    // ───────────── Sound routing ─────────────

    public string AudioSummary
    {
        get
        {
            if (AppSettings.Current.AudioFollowsActive) return "소리: 선택한 화면 따라감";
            var panes = AllPanes.ToList();
            int on = panes.Count(p => p.AudioEnabled);
            if (on == panes.Count) return "소리: 모든 화면";
            if (on == 0) return "소리: 꺼짐";
            return $"소리: {on}개 화면";
        }
    }

    public void OnPaneActivated(PlayerPane pane)
    {
        _activePane = pane;
        if (AppSettings.Current.AudioFollowsActive) RouteAudioTo(pane);
    }

    public void SetAllAudio(bool enabled)
    {
        SetAudioFollowsActive(false);
        foreach (var pane in AllPanes) pane.AudioEnabled = enabled;
        RefreshPanes();
    }

    public void SoloAudio(PlayerPane pane)
    {
        SetAudioFollowsActive(false);
        RouteAudioTo(pane);
    }

    public void SetAudioFollowsActive(bool enabled)
    {
        if (AppSettings.Current.AudioFollowsActive != enabled)
        {
            AppSettings.Current.AudioFollowsActive = enabled;
            AppSettings.Current.Save();
        }
        if (enabled && _activePane != null) RouteAudioTo(_activePane);
        else RefreshPanes();
    }

    void RouteAudioTo(PlayerPane pane)
    {
        foreach (var p in AllPanes) p.AudioEnabled = p == pane;
        RefreshPanes();
    }

    // ───────────── Native helpers ─────────────

    [StructLayout(LayoutKind.Sequential)]
    struct NativePoint { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Switches the native title bar to the Windows 10/11 dark style.</summary>
    public static void UseDarkTitleBar(Window window)
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        var handle = new WindowInteropHelper(window).Handle;
        int enabled = 1;
        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
    }
}
