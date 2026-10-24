using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace VideoPlayer;

/// <summary>A player window: toolbar plus a split layout of panes. Several can be open at once.</summary>
public partial class MainWindow : Window
{
    const int MaxPanesPerWindow = 8;

    PlayerPane? _active;

    bool _isFullscreen;
    WindowState _restoreState;
    WindowStyle _restoreStyle;
    ResizeMode _restoreResizeMode;

    public MainWindow(PlayerPane firstPane)
    {
        InitializeComponent();
        Layout.Add(firstPane, null, DockSide.Center);
        PanesHost.Layout = Layout;
        RebuildLayout();
        _active = firstPane;

        UpdateToolTips();
        AppSettings.Changed += UpdateToolTips;
        Closed += (_, _) => AppSettings.Changed -= UpdateToolTips;
    }

    public PaneLayout Layout { get; } = new();

    public IEnumerable<PlayerPane> Panes => Layout.Panes;

    public PlayerPane? ActivePane => _active;

    public bool Contains(PlayerPane pane) => Layout.Contains(pane);

    /// <summary>Opens files once the window is on screen (the video surface must exist before playback).</summary>
    public void OpenWhenReady(IReadOnlyList<string> paths)
    {
        void Open() => Dispatcher.BeginInvoke(() => _active?.OpenPaths(paths), DispatcherPriority.ContextIdle);
        if (IsLoaded) Open();
        else Loaded += (_, _) => Open();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        App.UseDarkTitleBar(this);
    }

    // ───────────── Panes ─────────────

    public PlayerPane? AddPane(DockSide? side = null, PlayerPane? nextTo = null)
    {
        if (Layout.Count >= MaxPanesPerWindow)
        {
            _active?.ShowOsd($"한 창에는 화면을 {MaxPanesPerWindow}개까지 열 수 있어요 · 새 창을 이용하세요");
            return null;
        }

        nextTo ??= _active;
        // Split along the longer edge so new panes stay reasonably shaped.
        var dock = side ?? (nextTo != null && nextTo.ActualHeight > nextTo.ActualWidth ? DockSide.Bottom : DockSide.Right);
        var pane = App.Current.CreatePane();
        Layout.Add(pane, nextTo, dock);
        RebuildLayout();
        SetActive(pane);
        return pane;
    }

    public void ClosePane(PlayerPane pane)
    {
        if (Layout.Count <= 1) return;
        Layout.Remove(pane);
        pane.Shutdown();
        RebuildLayout();
        if (_active == pane) SetActive(Panes.First());
    }

    /// <summary>Re-positions panes after any structural change.</summary>
    public void RebuildLayout()
    {
        PanesHost.Sync();
        foreach (var pane in Panes) pane.IsFullscreen = _isFullscreen;
        if (_active == null || !Layout.Contains(_active)) _active = Panes.FirstOrDefault();
        App.Current.RefreshPanes();
    }

    /// <summary>Updates close buttons, highlight, sound toggles and the toolbar hint.</summary>
    public void RefreshChrome(bool showAudioToggles)
    {
        int count = Layout.Count;
        foreach (var pane in Panes)
        {
            pane.CanClose = count > 1;
            pane.IsHighlighted = count > 1 && pane == _active;
            pane.ShowAudioToggle = showAudioToggles;
        }
        AddPaneButton.IsEnabled = count < MaxPanesPerWindow;
        PaneHint.Text = count > 1
            ? $"화면 {count}개 · 제목을 끌어 위치 이동 · 경계선을 끌어 크기 조절 · Tab으로 선택 전환"
            : "";
        SoundButtonText.Text = App.Current.AudioSummary;
    }

    public void SetActive(PlayerPane pane)
    {
        if (!Layout.Contains(pane)) return;
        _active = pane;
        App.Current.OnPaneActivated(pane);
        RefreshChrome(App.Current.AllPanes.Skip(1).Any());
    }

    void CycleActivePane()
    {
        var panes = Panes.ToList();
        if (panes.Count < 2 || _active == null) return;
        SetActive(panes[(panes.IndexOf(_active) + 1) % panes.Count]);
    }

    public void ShowLayoutMenu(PlayerPane pane, UIElement target)
    {
        bool hasSibling = Layout.HasSibling(pane);
        var menu = pane.NewMenu(target, PlacementMode.Bottom);

        menu.Items.Add(PlayerPane.MenuHeader("화면 배치"));
        menu.Items.Add(PlayerPane.MenuEntry("오른쪽에 새 화면", false, () => AddPane(DockSide.Right, pane)));
        menu.Items.Add(PlayerPane.MenuEntry("아래에 새 화면", false, () => AddPane(DockSide.Bottom, pane)));
        menu.Items.Add(new Separator());
        menu.Items.Add(PlayerPane.MenuEntry("옆 화면과 위치 바꾸기 (A|B → B|A)", false, () =>
        {
            Layout.SwapWithSibling(pane);
            RebuildLayout();
        }, hasSibling));
        menu.Items.Add(PlayerPane.MenuEntry("나란히 ↔ 위아래 전환", false, () =>
        {
            Layout.ToggleOrientation(pane);
            RebuildLayout();
        }, hasSibling));
        menu.Items.Add(new Separator());
        menu.Items.Add(PlayerPane.MenuEntry("새 창으로 분리", false, () => App.Current.MoveToNewWindow(pane), Layout.Count > 1));
        foreach (var window in App.Current.PlayerWindows.Where(w => w != this))
        {
            var destination = window;
            menu.Items.Add(PlayerPane.MenuEntry($"'{window.Title}' 창으로 보내기", false, () => App.Current.MoveToWindow(pane, destination)));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(PlayerPane.MenuEntry("이 화면 소리만 듣기", false, () => App.Current.SoloAudio(pane)));

        menu.IsOpen = true;
    }

    void ShowSoundMenu()
    {
        var app = App.Current;
        var all = app.AllPanes.ToList();
        bool follow = AppSettings.Current.AudioFollowsActive;

        var menu = new ContextMenu { PlacementTarget = SoundButton, Placement = PlacementMode.Bottom };
        menu.Items.Add(PlayerPane.MenuEntry("모든 화면 소리 켜기", !follow && all.All(p => p.AudioEnabled), () => app.SetAllAudio(true)));
        menu.Items.Add(PlayerPane.MenuEntry("선택한 화면만 소리 켜기", false, () =>
        {
            if (_active != null) app.SoloAudio(_active);
        }));
        menu.Items.Add(PlayerPane.MenuEntry("선택하는 화면으로 소리 자동 전환", follow, () => app.SetAudioFollowsActive(!follow)));
        menu.Items.Add(PlayerPane.MenuEntry("모든 화면 소리 끄기", !follow && all.All(p => !p.AudioEnabled), () => app.SetAllAudio(false)));
        menu.Items.Add(new Separator());
        menu.Items.Add(PlayerPane.MenuHeader("화면별로는 각 화면 위쪽의 '소리' 체크박스"));
        menu.IsOpen = true;
    }

    // ───────────── Window modes ─────────────

    public void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _restoreState = WindowState;
            _restoreStyle = WindowStyle;
            _restoreResizeMode = ResizeMode;

            Toolbar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            // Re-maximizing from Normal is what makes a borderless window cover the taskbar.
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            Toolbar.Visibility = Visibility.Visible;
            WindowStyle = _restoreStyle;
            ResizeMode = _restoreResizeMode;
            WindowState = _restoreState;
        }

        _isFullscreen = !_isFullscreen;
        foreach (var pane in Panes) pane.IsFullscreen = _isFullscreen;
    }

    void OpenSettings()
    {
        var dialog = new SettingsWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            AppSettings.Current.Save();
            AppSettings.RaiseChanged();
        }
    }

    void ShowShortcuts()
    {
        var text = new System.Text.StringBuilder();
        foreach (var group in Shortcuts.Definitions.GroupBy(d => d.Group))
        {
            text.AppendLine($"[{group.Key}]");
            foreach (var definition in group)
                text.AppendLine($"  {definition.Label}:  {Shortcuts.Describe(definition.Action)}");
            text.AppendLine();
        }
        text.AppendLine("마우스: 더블클릭 = 전체 화면 · 휠 = 볼륨");
        text.AppendLine("화면 제목을 끌어 다른 화면 가장자리에 놓으면 그쪽에 붙고, 가운데는 위치 교환, 창 밖은 새 창.");
        text.AppendLine("단축키는 설정 → 단축키에서 바꿀 수 있어요.");
        MessageBox.Show(this, text.ToString(), "단축키", MessageBoxButton.OK, MessageBoxImage.None);
    }

    // ───────────── Keyboard ─────────────

    public void HandleKey(KeyEventArgs e)
    {
        if (KeyChord.FromEvent(e) is not KeyChord chord) return;
        if (chord == new KeyChord(Key.F4, ModifierKeys.Alt))
        {
            // The overlay windows would otherwise close themselves instead of the app window.
            Close();
            e.Handled = true;
            return;
        }
        if (Shortcuts.Find(chord) is ShortcutAction action && Execute(action))
            e.Handled = true;
    }

    bool Execute(ShortcutAction action)
    {
        switch (action)
        {
            case ShortcutAction.OpenFile: _active?.OpenFileDialog(); return true;
            case ShortcutAction.OpenFolder: _active?.OpenFolderDialog(); return true;
            case ShortcutAction.AddPane: AddPane(); return true;
            case ShortcutAction.NewWindow: App.Current.OpenWindow(); return true;
            case ShortcutAction.ClosePane: if (_active != null) ClosePane(_active); return true;
            case ShortcutAction.Settings: OpenSettings(); return true;
            case ShortcutAction.ShowShortcuts: ShowShortcuts(); return true;
            case ShortcutAction.Fullscreen: ToggleFullscreen(); return true;
            case ShortcutAction.ExitFullscreen:
                if (!_isFullscreen) return false;
                ToggleFullscreen();
                return true;
            case ShortcutAction.NextPane: CycleActivePane(); return true;
            default: return _active != null && _active.Execute(action);
        }
    }

    void UpdateToolTips()
    {
        OpenFileButton.ToolTip = "파일 열기" + Shortcuts.Hint(ShortcutAction.OpenFile);
        OpenFolderButton.ToolTip = "폴더 열기" + Shortcuts.Hint(ShortcutAction.OpenFolder);
        AddPaneButton.ToolTip = "선택한 화면을 나눠 새 화면 추가" + Shortcuts.Hint(ShortcutAction.AddPane);
        NewWindowButton.ToolTip = "새 창 열기" + Shortcuts.Hint(ShortcutAction.NewWindow);
        SettingsButton.ToolTip = "설정" + Shortcuts.Hint(ShortcutAction.Settings);
        ShortcutsButton.ToolTip = "단축키 보기" + Shortcuts.Hint(ShortcutAction.ShowShortcuts);
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e) => HandleKey(e);

    // ───────────── Toolbar & window events ─────────────

    void OpenFileButton_Click(object sender, RoutedEventArgs e) => _active?.OpenFileDialog();
    void OpenFolderButton_Click(object sender, RoutedEventArgs e) => _active?.OpenFolderDialog();
    void AddPaneButton_Click(object sender, RoutedEventArgs e) => AddPane();
    void NewWindowButton_Click(object sender, RoutedEventArgs e) => App.Current.OpenWindow();
    void SoundButton_Click(object sender, RoutedEventArgs e) => ShowSoundMenu();
    void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings();
    void ShortcutsButton_Click(object sender, RoutedEventArgs e) => ShowShortcuts();
    void TopmostButton_Changed(object sender, RoutedEventArgs e) => Topmost = TopmostButton.IsChecked == true;

    void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            _active?.OpenPaths(files);
    }

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        bool lastWindow = App.Current.PlayerWindows.Count <= 1;
        if (lastWindow) App.IsShuttingDown = true;

        var stops = Panes.Select(p => p.Shutdown()).ToArray();
        if (lastWindow) Task.WaitAll(stops, TimeSpan.FromSeconds(3));
    }
}
