using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using MediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoPlayer;

public enum RepeatMode { None, All, One }

public sealed class PaneDropEventArgs : EventArgs
{
    public PaneDropEventArgs(PlayerPane source, DockSide side) => (Source, Side) = (source, side);

    public PlayerPane Source { get; }
    public DockSide Side { get; }
}

/// <summary>One independent player: its own media, playlist, speed, volume, rotation and controls.</summary>
public partial class PlayerPane : UserControl, IDisposable
{
    public static readonly float[] Rates = { 0.25f, 0.5f, 0.75f, 1f, 1.25f, 1.5f, 1.75f, 2f };

    const string PaneDragFormat = "VideoPlayer.PlayerPane";

    const string GlyphPlay = "", GlyphPause = "";
    const string GlyphVolume = "", GlyphMute = "";
    const string GlyphFullscreen = "", GlyphExitFullscreen = "";
    const string GlyphRepeatAll = "", GlyphRepeatOne = "";

    // Alpha must be non-zero: fully transparent pixels of the layered overlay window let mouse input fall through.
    static readonly System.Windows.Media.Brush NearlyTransparent =
        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(1, 0, 0, 0));

    MediaPlayer _player;
    readonly List<string> _playlist = new();
    readonly DispatcherTimer _positionTimer, _hideTimer, _osdTimer;

    int _index = -1;
    PlaylistOrder _order;
    float _rate = 1f;
    int _rotation;
    bool _muted, _audioEnabled = true;
    RepeatMode _repeat = RepeatMode.None;
    long? _pendingStart;
    bool _quietStart, _pauseAfterStart;
    bool _reopening, _reopenAgain;
    long? _seekTarget;
    DateTime _seekIssuedAt, _lastScrub;
    bool _syncingSlider, _draggingSlider, _syncingAudioCheck;
    bool _controlsVisible = true, _isFullscreen, _disposed;
    int _openMenus;
    Point _lastMouse;
    Point? _dragStart;
    Window? _overlayWindow;

    public event EventHandler? ActivateRequested;
    public event EventHandler? CloseRequested;
    public event EventHandler? FullscreenToggleRequested;
    /// <summary>Keys pressed while the video overlay window has focus.</summary>
    public event KeyEventHandler? OverlayKeyDown;
    /// <summary>Another pane was dropped onto this one.</summary>
    public event EventHandler<PaneDropEventArgs>? PaneDropped;
    /// <summary>This pane was dragged out and released outside every player window.</summary>
    public event EventHandler? TearOffRequested;
    /// <summary>The "화면 배치" button was clicked; the owning window fills the menu.</summary>
    public event EventHandler<UIElement>? LayoutMenuRequested;
    /// <summary>The user flipped this pane's sound checkbox by hand.</summary>
    public event EventHandler? AudioToggledByUser;

    public PlayerPane()
    {
        InitializeComponent();

        _player = CreatePlayer(App.LibVLC);
        VideoView.MediaPlayer = _player;

        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _positionTimer.Tick += (_, _) => SyncPosition();
        _positionTimer.Start();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _hideTimer.Tick += (_, _) => TryHideControls();

        _osdTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        _osdTimer.Tick += (_, _) =>
        {
            _osdTimer.Stop();
            Osd.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)));
        };

        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler((_, _) => _draggingSlider = true));
        SeekSlider.AddHandler(Thumb.DragDeltaEvent, new DragDeltaEventHandler((_, _) => ScrubPreview()));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _draggingSlider = false;
            SeekTo((long)SeekSlider.Value);
        }));

        VolumeSlider.Value = AppSettings.Current.Volume;
        AppSettings.Changed += OnSettingsChanged;
        OnSettingsChanged();
        UpdatePlayState();
        UpdateSpeedButton();
        UpdateRepeatButton();
        UpdateRotateButton();
        UpdateMediaState();
    }

    public bool HasMedia => _index >= 0;

    public bool IsFullscreen
    {
        get => _isFullscreen;
        set
        {
            _isFullscreen = value;
            FullscreenButton.Content = value ? GlyphExitFullscreen : GlyphFullscreen;
            if (!value) OverlayRoot.Cursor = null;
            ShowControls();
        }
    }

    public bool CanClose
    {
        set => CloseButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The per-pane sound checkbox only matters once several panes exist.</summary>
    public bool ShowAudioToggle
    {
        set => AudioCheck.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public bool IsHighlighted
    {
        set => ActiveBorder.BorderBrush = value ? (System.Windows.Media.Brush)FindResource("AccentBrush") : System.Windows.Media.Brushes.Transparent;
    }

    /// <summary>Whether this pane is allowed to make sound (independent of the mute button).</summary>
    public bool AudioEnabled
    {
        get => _audioEnabled;
        set
        {
            _audioEnabled = value;
            _syncingAudioCheck = true;
            AudioCheck.IsChecked = value;
            _syncingAudioCheck = false;
            ApplyMute();
        }
    }

    MediaPlayer CreatePlayer(LibVLC engine)
    {
        var player = new MediaPlayer(engine) { EnableKeyInput = false, EnableMouseInput = false };
        // LibVLC raises events on its own thread; marshal everything to the UI thread
        // (and never call back into the player synchronously from these callbacks).
        // Events from a player that has since been replaced (rotation) are dropped.
        player.Playing += (_, _) => Ui(player, OnPlaying);
        player.Paused += (_, _) => Ui(player, UpdatePlayState);
        player.Stopped += (_, _) => Ui(player, UpdatePlayState);
        player.EndReached += (_, _) => Ui(player, OnEndReached);
        player.EncounteredError += (_, _) => Ui(player, () => ShowOsd("재생할 수 없는 파일입니다"));
        return player;
    }

    void Ui(MediaPlayer source, Action action)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed && source == _player) action();
        });
    }

    // ───────────── Opening & playlist ─────────────

    public void OpenPaths(IReadOnlyList<string> paths)
    {
        var subtitles = paths.Where(MediaLibrary.IsSubtitle).ToList();
        if (subtitles.Count == paths.Count && HasMedia)
        {
            foreach (var sub in subtitles) AddSubtitle(sub);
            return;
        }

        var list = MediaLibrary.BuildPlaylist(paths, out var start, out var order);
        if (list.Count == 0)
        {
            ShowOsd("재생할 수 있는 영상이 없습니다");
            return;
        }

        SaveResumePosition();
        _playlist.Clear();
        _playlist.AddRange(list);
        _order = order;
        _index = -1;
        RefreshPlaylistView();
        PlayAt(start);
    }

    void RefreshPlaylistView()
    {
        PlaylistBox.ItemsSource = _playlist.Select(Path.GetFileName).ToList();
        PlaylistHeader.Text = $"재생 목록 ({_playlist.Count})";
        SortButton.Content = $"{_order.Label} ▾";
        if (_index >= 0) PlaylistBox.SelectedIndex = _index;
    }

    /// <summary>Re-sorts the playlist in place; the current video keeps playing.</summary>
    void ApplyOrder(PlaylistOrder order)
    {
        _order = order;
        var current = _index >= 0 ? _playlist[_index] : null;
        var sorted = MediaLibrary.Sort(_playlist, order);
        _playlist.Clear();
        _playlist.AddRange(sorted);
        if (current != null) _index = _playlist.IndexOf(current);
        RefreshPlaylistView();
        UpdateMediaState();
        ShowOsd($"정렬: {order.Label}");
    }

    void SortButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        var menu = NewMenu(SortButton, PlacementMode.Bottom);

        menu.Items.Add(MenuEntry("탐색기 정렬 따르기", settings.FollowExplorerSort, () =>
        {
            settings.FollowExplorerSort = !settings.FollowExplorerSort;
            settings.Save();
            if (settings.FollowExplorerSort && _index >= 0)
            {
                var order = MediaLibrary.ChooseOrder(Path.GetDirectoryName(_playlist[_index]));
                if (order.FromExplorer) ApplyOrder(order);
                else ShowOsd("이 폴더를 보여주는 탐색기 창이 없어요");
            }
        }));
        menu.Items.Add(new Separator());
        foreach (var key in Enum.GetValues<PlaylistSort>())
        {
            menu.Items.Add(MenuEntry(PlaylistOrder.KeyLabel(key), _order.Key == key, () => ChooseSort(key, _order.Descending)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuEntry("오름차순", !_order.Descending, () => ChooseSort(_order.Key, false)));
        menu.Items.Add(MenuEntry("내림차순", _order.Descending, () => ChooseSort(_order.Key, true)));
        menu.IsOpen = true;
    }

    /// <summary>An explicit choice also becomes the default for folders without an Explorer window.</summary>
    void ChooseSort(PlaylistSort key, bool descending)
    {
        var settings = AppSettings.Current;
        settings.PlaylistSort = key;
        settings.PlaylistDescending = descending;
        settings.Save();
        ApplyOrder(new PlaylistOrder(key, descending, FromExplorer: false));
    }

    public void OpenFileDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = MediaLibrary.VideoFilter, Multiselect = true, Title = "동영상 열기" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            OpenPaths(dialog.FileNames);
    }

    public void OpenFolderDialog()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "동영상 폴더 열기" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            OpenPaths(new[] { dialog.FolderName });
    }

    /// <param name="startAt">Position to start from; null uses the saved resume position.</param>
    /// <param name="restart">Re-opening the same item (e.g. after rotating): no resume message, keep pause state.</param>
    void PlayAt(int index, long? startAt = null, bool restart = false, bool startPaused = false)
    {
        if (index < 0 || index >= _playlist.Count) return;

        if (!restart) SaveResumePosition();
        _index = index;
        var path = _playlist[index];

        if (!File.Exists(path))
        {
            ShowOsd("파일을 찾을 수 없습니다");
            UpdateMediaState();
            return;
        }

        if (startAt.HasValue)
            _pendingStart = startAt;
        else if (AppSettings.Current.ResumePlayback && AppSettings.Current.ResumePositions.TryGetValue(path, out var resume))
            _pendingStart = resume;
        else
            _pendingStart = null;
        _quietStart = restart;
        _pauseAfterStart = startPaused;

        using var media = new Media(App.EngineFor(_rotation), path, FromType.FromPath);
        if (AppSettings.Current.FastSeek) media.AddOption(":input-fast-seek");
        if (_pendingStart > 0)
        {
            // Start straight at the position instead of playing from 0 and seeking once it runs.
            media.AddOption(FormattableString.Invariant($":start-time={_pendingStart.Value / 1000.0:0.###}"));
        }
        _player.Play(media);

        TitleText.Text = Path.GetFileName(path);
        if (!restart) ResetSlider();
        UpdateMediaState();
    }

    public void Next()
    {
        if (_index < _playlist.Count - 1) PlayAt(_index + 1);
        else if (_repeat == RepeatMode.All && _playlist.Count > 0) PlayAt(0);
        else ShowOsd("마지막 영상입니다");
    }

    public void Previous()
    {
        if (_index > 0) PlayAt(_index - 1);
        else if (_repeat == RepeatMode.All && _playlist.Count > 0) PlayAt(_playlist.Count - 1);
        else ShowOsd("첫 번째 영상입니다");
    }

    void OnPlaying()
    {
        _player.SetRate(_rate);
        _player.Volume = (int)VolumeSlider.Value;
        ApplyMute();

        if (_pendingStart is long start)
        {
            // The media was opened with :start-time, so playback already begins here.
            _pendingStart = null;
            if (start > 0 && !_quietStart) ShowOsd($"이어서 재생 · {FormatTime(start)}");
        }
        if (_pauseAfterStart)
        {
            _pauseAfterStart = false;
            _player.SetPause(true);
        }
        UpdatePlayState();
    }

    void OnEndReached()
    {
        if (_index >= 0) AppSettings.Current.ResumePositions.Remove(_playlist[_index]);

        if (_repeat == RepeatMode.One)
            PlayAt(_index, 0);
        else if (_index < _playlist.Count - 1 && (AppSettings.Current.AutoPlayNext || _repeat == RepeatMode.All))
            PlayAt(_index + 1);
        else if (_repeat == RepeatMode.All && _playlist.Count > 0)
            PlayAt(0);
        else
            UpdatePlayState();
    }

    void SaveResumePosition()
    {
        if (_index < 0 || _index >= _playlist.Count) return;
        long length = _player.Length, time = _player.Time;
        if (length <= 0) return;

        var map = AppSettings.Current.ResumePositions;
        var path = _playlist[_index];
        map.Remove(path);
        if (time > 10_000 && time < length - 15_000)
        {
            map[path] = time;
            while (map.Count > 300) map.Remove(map.Keys.First());
        }
    }

    // ───────────── Transport ─────────────

    public void TogglePlay()
    {
        if (!HasMedia)
        {
            OpenFileDialog();
            return;
        }

        if (_player.State is VLCState.Ended or VLCState.Stopped or VLCState.Error or VLCState.NothingSpecial)
            PlayAt(_index, _player.State == VLCState.Ended ? 0 : null);
        else
            _player.SetPause(_player.IsPlaying);
    }

    public void SeekBy(int seconds)
    {
        if (!HasMedia) return;
        long length = _player.Length;
        long target = Math.Max(0, _player.Time + seconds * 1000L);
        if (length > 0) target = Math.Min(target, Math.Max(0, length - 1000));
        SeekTo(target);
        ShowOsd(seconds > 0 ? $"+{seconds}초" : $"−{-seconds}초");
    }

    void SeekTo(long ms)
    {
        if (!HasMedia) return;
        if (_player.State is VLCState.Ended or VLCState.Stopped)
        {
            PlayAt(_index, ms);
            return;
        }
        if (!_player.IsSeekable) return;

        App.Diag($"seek {TitleText.Text} -> {ms} ms (from {_player.Time})");
        _player.Time = ms;
        _seekTarget = ms;
        _seekIssuedAt = DateTime.UtcNow;
        _syncingSlider = true;
        SeekSlider.Value = ms;
        _syncingSlider = false;
        CurrentTimeText.Text = FormatTime(ms);
    }

    /// <summary>While the seek bar is dragged, follow it with the picture (throttled; libVLC merges queued seeks).</summary>
    void ScrubPreview()
    {
        if (!HasMedia || !_player.IsSeekable || DateTime.UtcNow - _lastScrub < TimeSpan.FromMilliseconds(150)) return;
        _lastScrub = DateTime.UtcNow;
        _player.Time = (long)SeekSlider.Value;
    }

    public void SetRate(float rate)
    {
        _rate = rate;
        _player.SetRate(rate);
        // libVLC 3 queues up to ~2 s of audio ahead, which would keep playing at the old speed.
        // Re-seeking to the current time flushes that queue so the new speed is heard at once.
        // (Skipped with fast seek, where it would jump back to the previous keyframe.)
        if (_player.IsPlaying && _player.IsSeekable && !AppSettings.Current.FastSeek)
            _player.Time = _player.Time;
        UpdateSpeedButton();
        ShowOsd($"재생 속도 {rate:0.##}x");
    }

    public void StepRate(int direction)
    {
        int i = Array.IndexOf(Rates, _rate);
        if (i < 0) i = Array.IndexOf(Rates, 1f);
        SetRate(Rates[Math.Clamp(i + direction, 0, Rates.Length - 1)]);
    }

    /// <summary>
    /// Rotates clockwise by 90°. The pane switches to a player on the engine for that angle
    /// and reopens the current item at the same position.
    /// </summary>
    public void Rotate()
    {
        _rotation = (_rotation + 90) % 360;
        UpdateRotateButton();
        ShowOsd(_rotation == 0 ? "회전 없음" : $"회전 {_rotation}°");
        _ = ReopenAsync();
    }

    /// <summary>
    /// Call after the pane has moved to another window: the native video window was re-parented,
    /// so libVLC's output is rebuilt against it.
    /// </summary>
    public void ReattachVideo() => Dispatcher.BeginInvoke(() => _ = ReopenAsync(), DispatcherPriority.Loaded);

    /// <summary>
    /// Replaces the player with a fresh one on the engine for the current rotation and resumes
    /// where playback was. Requests that arrive while one is running are folded into one more pass.
    /// </summary>
    async Task ReopenAsync()
    {
        if (_reopening)
        {
            _reopenAgain = true;
            return;
        }
        _reopening = true;
        try
        {
            do
            {
                _reopenAgain = false;
                var state = _player.State;
                bool resume = HasMedia && state is VLCState.Playing or VLCState.Paused or VLCState.Buffering or VLCState.Opening;
                long time = Math.Max(0, _player.Time);

                var old = _player;
                _player = CreatePlayer(App.EngineFor(_rotation)); // events from the old player are ignored from here on
                // A window handle takes one video output, so the old player must be fully stopped first.
                // Stop() blocks until its threads finish; keep the UI responsive meanwhile.
                await Task.Run(old.Stop);
                if (_disposed)
                {
                    old.Dispose(); // the new player is released by Shutdown()
                    return;
                }
                VideoView.MediaPlayer = null; // re-attach so the current window handle is picked up
                VideoView.MediaPlayer = _player;
                _ = Task.Run(old.Dispose);

                if (resume) PlayAt(_index, time, restart: true, startPaused: state == VLCState.Paused);
            }
            while (_reopenAgain && !_disposed);
        }
        finally
        {
            _reopening = false;
        }
    }

    public void ChangeVolume(int delta)
    {
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + delta, 0, 100);
        ShowOsd($"볼륨 {(int)VolumeSlider.Value}");
    }

    public void ToggleMute()
    {
        _muted = !_muted;
        ApplyMute();
        ShowOsd(_muted ? "음소거" : $"볼륨 {(int)VolumeSlider.Value}");
    }

    void ApplyMute()
    {
        bool silent = _muted || !_audioEnabled;
        _player.Mute = silent;
        MuteButton.Content = silent ? GlyphMute : GlyphVolume;
        MuteButton.ToolTip = !_audioEnabled ? "이 화면은 소리 꺼짐 (위쪽 '소리' 체크)" : "음소거" + Shortcuts.Hint(ShortcutAction.Mute);
    }

    void CycleRepeat()
    {
        _repeat = _repeat switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.None,
        };
        UpdateRepeatButton();
        ShowOsd(RepeatLabel());
    }

    void TogglePlaylist()
    {
        PlaylistPanel.Visibility = PlaylistPanel.IsVisible ? Visibility.Collapsed : Visibility.Visible;
        if (PlaylistPanel.IsVisible && _index >= 0) PlaylistBox.ScrollIntoView(PlaylistBox.SelectedItem);
        ShowControls();
    }

    void AddSubtitle(string path)
    {
        if (_player.AddSlave(MediaSlaveType.Subtitle, new Uri(path).AbsoluteUri, true))
            ShowOsd($"자막 추가 · {Path.GetFileName(path)}");
        else
            ShowOsd("자막을 불러오지 못했습니다");
    }

    /// <summary>Runs a pane-level shortcut action. Returns false for actions that aren't the pane's.</summary>
    public bool Execute(ShortcutAction action)
    {
        int step = AppSettings.Current.SeekSeconds;
        switch (action)
        {
            case ShortcutAction.PlayPause: TogglePlay(); break;
            case ShortcutAction.SeekBack: SeekBy(-step); break;
            case ShortcutAction.SeekForward: SeekBy(step); break;
            case ShortcutAction.PreviousVideo: Previous(); break;
            case ShortcutAction.NextVideo: Next(); break;
            case ShortcutAction.Restart: if (HasMedia) SeekTo(0); break;
            case ShortcutAction.SpeedDown: StepRate(-1); break;
            case ShortcutAction.SpeedUp: StepRate(1); break;
            case ShortcutAction.SpeedReset: SetRate(1f); break;
            case ShortcutAction.VolumeUp: ChangeVolume(5); break;
            case ShortcutAction.VolumeDown: ChangeVolume(-5); break;
            case ShortcutAction.Mute: ToggleMute(); break;
            case ShortcutAction.Repeat: CycleRepeat(); break;
            case ShortcutAction.Rotate: Rotate(); break;
            case ShortcutAction.Playlist: TogglePlaylist(); break;
            default: return false;
        }
        ShowControls();
        return true;
    }

    // ───────────── UI state ─────────────

    void SyncPosition()
    {
        long length = _player.Length;
        if (!HasMedia || length <= 0 || _draggingSlider) return;

        long time = _player.Time;
        if (_seekTarget is long target)
        {
            // libVLC keeps reporting the old time until the seek lands; hold the bar at the target meanwhile.
            if (Math.Abs(time - target) < 1500 || DateTime.UtcNow - _seekIssuedAt > TimeSpan.FromSeconds(2))
                _seekTarget = null;
            else
                time = target;
        }

        _syncingSlider = true;
        SeekSlider.Maximum = length;
        SeekSlider.Value = Math.Clamp(time, 0, length);
        _syncingSlider = false;

        CurrentTimeText.Text = FormatTime((long)SeekSlider.Value);
        TotalTimeText.Text = FormatTime(length);
    }

    void ResetSlider()
    {
        _syncingSlider = true;
        SeekSlider.Value = 0;
        SeekSlider.Maximum = 1;
        _syncingSlider = false;
        CurrentTimeText.Text = TotalTimeText.Text = "0:00";
    }

    void UpdatePlayState()
    {
        bool playing = _player.IsPlaying;
        PlayIcon.Text = playing ? GlyphPause : GlyphPlay;
        if (!playing) ShowControls();
    }

    void UpdateMediaState()
    {
        Placeholder.Visibility = HasMedia ? Visibility.Collapsed : Visibility.Visible;
        // Before anything is played the native video window is unpainted (white), so cover it.
        OverlayRoot.Background = HasMedia ? NearlyTransparent : System.Windows.Media.Brushes.Black;
        foreach (var control in new UIElement[] { PrevButton, BackButton, ForwardButton, NextButton, SeekSlider, TracksButton, PlaylistButton })
            control.IsEnabled = HasMedia;
        PrevButton.IsEnabled = HasMedia && (_index > 0 || _repeat == RepeatMode.All);
        NextButton.IsEnabled = HasMedia && (_index < _playlist.Count - 1 || _repeat == RepeatMode.All);
        if (HasMedia) PlaylistBox.SelectedIndex = _index;
        if (!HasMedia) TitleText.Text = "빈 화면";
    }

    void UpdateSpeedButton() => SpeedButton.Content = $"{_rate:0.##}x";

    void UpdateRotateButton()
    {
        RotateButton.Foreground = _rotation == 0
            ? (System.Windows.Media.Brush)FindResource("TextBrush")
            : (System.Windows.Media.Brush)FindResource("AccentBrush");
        var hint = Shortcuts.Hint(ShortcutAction.Rotate);
        RotateButton.ToolTip = _rotation == 0 ? "90° 회전" + hint : $"현재 {_rotation}° · 한 번 더 누르면 90° 회전{hint}";
    }

    void UpdateRepeatButton()
    {
        RepeatButton.Content = _repeat == RepeatMode.One ? GlyphRepeatOne : GlyphRepeatAll;
        RepeatButton.Foreground = _repeat == RepeatMode.None
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF))
            : (System.Windows.Media.Brush)FindResource("AccentBrush");
        RepeatButton.ToolTip = RepeatLabel() + Shortcuts.Hint(ShortcutAction.Repeat);
        UpdateMediaState();
    }

    string RepeatLabel() => _repeat switch
    {
        RepeatMode.All => "전체 반복",
        RepeatMode.One => "한 곡 반복",
        _ => "반복 안 함",
    };

    void OnSettingsChanged()
    {
        int step = AppSettings.Current.SeekSeconds;
        BackSecondsText.Text = ForwardSecondsText.Text = step.ToString();

        // Tooltips show the user's current shortcut keys.
        BackButton.ToolTip = $"{step}초 뒤로" + Shortcuts.Hint(ShortcutAction.SeekBack);
        ForwardButton.ToolTip = $"{step}초 앞으로" + Shortcuts.Hint(ShortcutAction.SeekForward);
        PlayButton.ToolTip = "재생/일시정지" + Shortcuts.Hint(ShortcutAction.PlayPause);
        PrevButton.ToolTip = "이전 영상" + Shortcuts.Hint(ShortcutAction.PreviousVideo);
        NextButton.ToolTip = "다음 영상" + Shortcuts.Hint(ShortcutAction.NextVideo);
        SpeedButton.ToolTip = $"재생 속도 (느리게 {Shortcuts.Describe(ShortcutAction.SpeedDown)} · 빠르게 {Shortcuts.Describe(ShortcutAction.SpeedUp)})";
        PlaylistButton.ToolTip = "재생 목록" + Shortcuts.Hint(ShortcutAction.Playlist);
        FullscreenButton.ToolTip = "전체 화면" + Shortcuts.Hint(ShortcutAction.Fullscreen);
        CloseButton.ToolTip = "이 화면 닫기" + Shortcuts.Hint(ShortcutAction.ClosePane);
        UpdateRepeatButton();
        UpdateRotateButton();
        ApplyMute();
    }

    public void ShowOsd(string text)
    {
        OsdText.Text = text;
        Osd.BeginAnimation(OpacityProperty, null);
        Osd.Opacity = 1;
        _osdTimer.Stop();
        _osdTimer.Start();
    }

    void ShowControls()
    {
        if (!_controlsVisible)
        {
            _controlsVisible = true;
            FadeControls(1);
        }
        OverlayRoot.Cursor = null;
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    void TryHideControls()
    {
        _hideTimer.Stop();
        if (!_player.IsPlaying || _openMenus > 0 || PlaylistPanel.IsVisible || ControlBar.IsMouseOver || TopBar.IsMouseOver)
            return;

        _controlsVisible = false;
        FadeControls(0);
        if (_isFullscreen) OverlayRoot.Cursor = Cursors.None;
    }

    void FadeControls(double opacity)
    {
        foreach (var element in new FrameworkElement[] { ControlBar, TopBar })
        {
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(opacity, TimeSpan.FromMilliseconds(200)));
            element.IsHitTestVisible = opacity > 0;
        }
    }

    static string FormatTime(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    // ───────────── Menus ─────────────

    /// <summary>A dark popup menu anchored above <paramref name="target"/> that keeps the controls visible while open.</summary>
    public ContextMenu NewMenu(UIElement target, PlacementMode placement = PlacementMode.Top)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = placement };
        menu.Opened += (_, _) => _openMenus++;
        menu.Closed += (_, _) =>
        {
            _openMenus--;
            ShowControls();
        };
        return menu;
    }

    public static MenuItem MenuEntry(string text, bool isChecked, Action onClick, bool enabled = true)
    {
        var item = new MenuItem { Header = text, IsChecked = isChecked, IsEnabled = enabled };
        item.Click += (_, _) => onClick();
        return item;
    }

    public static MenuItem MenuHeader(string text) => new() { Header = text, IsEnabled = false, FontWeight = FontWeights.SemiBold };

    void SpeedButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu(SpeedButton);
        foreach (var rate in Rates.Reverse())
            menu.Items.Add(MenuEntry(rate == 1f ? "1x (보통)" : $"{rate:0.##}x", rate == _rate, () => SetRate(rate)));
        menu.IsOpen = true;
    }

    void TracksButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = NewMenu(TracksButton);

        menu.Items.Add(MenuHeader("자막"));
        var subtitles = _player.SpuDescription;
        if (subtitles.Length == 0)
            menu.Items.Add(new MenuItem { Header = "자막 없음", IsEnabled = false });
        foreach (var track in subtitles)
        {
            int id = track.Id;
            menu.Items.Add(MenuEntry(id == -1 ? "사용 안 함" : track.Name, _player.Spu == id, () => _player.SetSpu(id)));
        }
        menu.Items.Add(MenuEntry("자막 파일 추가...", false, () =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = MediaLibrary.SubtitleFilter, Title = "자막 파일 선택" };
            if (_index >= 0) dialog.InitialDirectory = Path.GetDirectoryName(_playlist[_index]);
            if (dialog.ShowDialog(Window.GetWindow(this)) == true) AddSubtitle(dialog.FileName);
        }));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuHeader("오디오"));
        foreach (var track in _player.AudioTrackDescription)
        {
            int id = track.Id;
            menu.Items.Add(MenuEntry(id == -1 ? "사용 안 함" : track.Name, _player.AudioTrack == id, () => _player.SetAudioTrack(id)));
        }
        menu.IsOpen = true;
    }

    // ───────────── Moving panes (drag & drop) ─────────────

    void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(OverlayRoot);
        DragHandle.CaptureMouse();
        e.Handled = true;
    }

    void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(OverlayRoot) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance * 2 &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2)
            return;

        _dragStart = null;
        DragHandle.ReleaseMouseCapture();

        bool cancelled = false;
        QueryContinueDragEventHandler watchEscape = (_, args) => cancelled |= args.EscapePressed;
        DragHandle.QueryContinueDrag += watchEscape;
        var result = DragDrop.DoDragDrop(DragHandle, new DataObject(PaneDragFormat, this), DragDropEffects.Move);
        DragHandle.QueryContinueDrag -= watchEscape;

        if (result == DragDropEffects.None && !cancelled)
            TearOffRequested?.Invoke(this, EventArgs.Empty);
    }

    void DragHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragStart = null;
        DragHandle.ReleaseMouseCapture();
    }

    static DockSide ZoneAt(Point p, Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return DockSide.Center;
        double left = p.X / size.Width, top = p.Y / size.Height;
        var distances = new (DockSide side, double d)[]
        {
            (DockSide.Left, left), (DockSide.Right, 1 - left), (DockSide.Top, top), (DockSide.Bottom, 1 - top),
        };
        var nearest = distances.MinBy(x => x.d);
        return nearest.d < 0.25 ? nearest.side : DockSide.Center;
    }

    void ShowDropIndicator(DockSide side)
    {
        double w = OverlayRoot.ActualWidth, h = OverlayRoot.ActualHeight;
        DropIndicator.HorizontalAlignment = side switch { DockSide.Left => HorizontalAlignment.Left, DockSide.Right => HorizontalAlignment.Right, _ => HorizontalAlignment.Stretch };
        DropIndicator.VerticalAlignment = side switch { DockSide.Top => VerticalAlignment.Top, DockSide.Bottom => VerticalAlignment.Bottom, _ => VerticalAlignment.Stretch };
        DropIndicator.Width = side is DockSide.Left or DockSide.Right ? w / 2 - 6 : double.NaN;
        DropIndicator.Height = side is DockSide.Top or DockSide.Bottom ? h / 2 - 6 : double.NaN;
        DropIndicatorText.Text = side switch
        {
            DockSide.Left => "왼쪽에 놓기",
            DockSide.Right => "오른쪽에 놓기",
            DockSide.Top => "위에 놓기",
            DockSide.Bottom => "아래에 놓기",
            _ => "위치 바꾸기",
        };
        DropIndicator.Visibility = Visibility.Visible;
    }

    // ───────────── Event handlers ─────────────

    void OverlayRoot_Loaded(object sender, RoutedEventArgs e)
    {
        // The overlay lives in its own window, so keys pressed there never reach MainWindow.
        var window = Window.GetWindow(OverlayRoot);
        if (window == null || window == _overlayWindow || window is MainWindow) return;

        _overlayWindow = window;
        window.PreviewKeyDown += (_, args) => OverlayKeyDown?.Invoke(this, args);
        window.Closing += (_, args) =>
        {
            if (!_disposed && !App.IsShuttingDown) args.Cancel = true;
        };
    }

    void OverlayRoot_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;

        // A manual sound toggle must be known before activation, which may re-route audio.
        if (source != null && IsWithin(source, AudioCheck))
            AudioToggledByUser?.Invoke(this, EventArgs.Empty);

        // Clicking anywhere outside the open playlist closes it (the click still does its normal job).
        // The playlist button is left alone so it can toggle the panel itself.
        if (PlaylistPanel.IsVisible && source != null && !IsWithin(source, PlaylistPanel) && !IsWithin(source, PlaylistButton))
            PlaylistPanel.Visibility = Visibility.Collapsed;

        ActivateRequested?.Invoke(this, EventArgs.Empty);
    }

    static bool IsWithin(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (node == ancestor) return true;
            node = node is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    void OverlayRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    void OverlayRoot_MouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(OverlayRoot);
        if (position == _lastMouse) return;
        _lastMouse = position;
        ShowControls();
    }

    void OverlayRoot_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (PlaylistPanel.IsMouseOver) return;
        ChangeVolume(e.Delta > 0 ? 5 : -5);
        e.Handled = true;
    }

    void OverlayRoot_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(PaneDragFormat) is PlayerPane source)
        {
            if (source == this)
            {
                e.Effects = DragDropEffects.None;
                DropIndicator.Visibility = Visibility.Collapsed;
            }
            else
            {
                e.Effects = DragDropEffects.Move;
                ShowDropIndicator(ZoneAt(e.GetPosition(OverlayRoot), OverlayRoot.RenderSize));
            }
        }
        else
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        }
        e.Handled = true;
    }

    void OverlayRoot_DragLeave(object sender, DragEventArgs e) => DropIndicator.Visibility = Visibility.Collapsed;

    void OverlayRoot_Drop(object sender, DragEventArgs e)
    {
        DropIndicator.Visibility = Visibility.Collapsed;

        if (e.Data.GetData(PaneDragFormat) is PlayerPane source)
        {
            if (source != this)
            {
                var side = ZoneAt(e.GetPosition(OverlayRoot), OverlayRoot.RenderSize);
                // Let DoDragDrop return first; re-parenting panes inside the drop callback is fragile.
                Dispatcher.BeginInvoke(() => PaneDropped?.Invoke(this, new PaneDropEventArgs(source, side)));
            }
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            ActivateRequested?.Invoke(this, EventArgs.Empty);
            OpenPaths(files);
        }
    }

    void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingSlider) return;
        if (_draggingSlider)
            CurrentTimeText.Text = FormatTime((long)e.NewValue);
        else
            SeekTo((long)e.NewValue);
    }

    void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player == null) return;
        _player.Volume = (int)e.NewValue;
        AppSettings.Current.Volume = (int)e.NewValue;
    }

    void AudioCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingAudioCheck || _player == null) return;
        _audioEnabled = AudioCheck.IsChecked == true;
        ApplyMute();
        ShowOsd(_audioEnabled ? "이 화면 소리 켜짐" : "이 화면 소리 꺼짐");
    }

    void OpenFile_Click(object sender, RoutedEventArgs e) => OpenFileDialog();
    void OpenFolder_Click(object sender, RoutedEventArgs e) => OpenFolderDialog();
    void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();
    void PrevButton_Click(object sender, RoutedEventArgs e) => Previous();
    void NextButton_Click(object sender, RoutedEventArgs e) => Next();
    void BackButton_Click(object sender, RoutedEventArgs e) => SeekBy(-AppSettings.Current.SeekSeconds);
    void ForwardButton_Click(object sender, RoutedEventArgs e) => SeekBy(AppSettings.Current.SeekSeconds);
    void MuteButton_Click(object sender, RoutedEventArgs e) => ToggleMute();
    void RepeatButton_Click(object sender, RoutedEventArgs e) => CycleRepeat();
    void RotateButton_Click(object sender, RoutedEventArgs e) => Rotate();
    void PlaylistButton_Click(object sender, RoutedEventArgs e) => TogglePlaylist();
    void FullscreenButton_Click(object sender, RoutedEventArgs e) => FullscreenToggleRequested?.Invoke(this, EventArgs.Empty);
    void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
    void MoreButton_Click(object sender, RoutedEventArgs e) => LayoutMenuRequested?.Invoke(this, MoreButton);

    void PlaylistBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistBox.SelectedIndex >= 0) PlayAt(PlaylistBox.SelectedIndex);
        e.Handled = true;
    }

    // ───────────── Cleanup ─────────────

    /// <summary>Stops playback and releases the player. Returns the stop task so app exit can wait on it.</summary>
    public Task Shutdown()
    {
        if (_disposed) return Task.CompletedTask;
        SaveResumePosition();
        _disposed = true;

        AppSettings.Changed -= OnSettingsChanged;
        _positionTimer.Stop();
        _hideTimer.Stop();
        _osdTimer.Stop();

        var player = _player;
        VideoView.MediaPlayer = null;
        VideoView.Dispose();
        // Stop() blocks until the decoder threads finish, so keep it off the UI thread.
        return Task.Run(() =>
        {
            player.Stop();
            player.Dispose();
        });
    }

    public void Dispose() => Shutdown();
}
