using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VideoPlayer;

public partial class SettingsWindow : Window
{
    /// <summary>Working copy of the shortcut overrides; written back only on 저장.</summary>
    readonly Dictionary<string, List<string>> _bindings;
    readonly Dictionary<ShortcutAction, Button> _keyButtons = new();
    ShortcutAction? _capturing;

    public SettingsWindow()
    {
        InitializeComponent();

        var settings = AppSettings.Current;
        foreach (var seconds in AppSettings.SeekOptions)
        {
            SeekOptionsPanel.Children.Add(new RadioButton
            {
                Content = $"{seconds}초",
                Tag = seconds,
                GroupName = "Seek",
                Style = (Style)FindResource("SegmentButton"),
                IsChecked = seconds == settings.SeekSeconds,
            });
        }
        AutoPlayNextBox.IsChecked = settings.AutoPlayNext;
        FolderPlaylistBox.IsChecked = settings.LoadFolderAsPlaylist;
        FollowExplorerBox.IsChecked = settings.FollowExplorerSort;
        ResumeBox.IsChecked = settings.ResumePlayback;
        FastSeekBox.IsChecked = settings.FastSeek;

        _bindings = settings.KeyBindings.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
        BuildShortcutList();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        App.UseDarkTitleBar(this);
    }

    void BuildShortcutList()
    {
        foreach (var group in Shortcuts.Definitions.GroupBy(d => d.Group))
        {
            ShortcutList.Children.Add(new TextBlock
            {
                Text = group.Key,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("SubtleTextBrush"),
                Margin = new Thickness(4, ShortcutList.Children.Count == 0 ? 6 : 14, 0, 4),
            });

            foreach (var definition in group)
            {
                var action = definition.Action;
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                row.Children.Add(new TextBlock { Text = definition.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) });

                var keyButton = new Button
                {
                    Style = (Style)FindResource("SecondaryButton"),
                    MinWidth = 190,
                    Padding = new Thickness(12, 5, 12, 5),
                    ToolTip = "눌러서 키 바꾸기",
                };
                keyButton.Click += (_, _) => StartCapture(action);
                Grid.SetColumn(keyButton, 1);
                row.Children.Add(keyButton);
                _keyButtons[action] = keyButton;

                var reset = IconButton("", "기본값으로", () => ResetAction(action));
                Grid.SetColumn(reset, 2);
                row.Children.Add(reset);

                var clear = IconButton("", "단축키 없애기", () => Assign(action, Array.Empty<KeyChord>()));
                Grid.SetColumn(clear, 3);
                row.Children.Add(clear);

                ShortcutList.Children.Add(row);
            }
        }
        RefreshKeys();
    }

    Button IconButton(string glyph, string tip, Action onClick)
    {
        var button = new Button { Style = (Style)FindResource("IconButton"), Content = glyph, FontSize = 13, ToolTip = tip, Margin = new Thickness(4, 0, 0, 0) };
        button.Click += (_, _) => onClick();
        return button;
    }

    void RefreshKeys()
    {
        foreach (var (action, button) in _keyButtons)
            button.Content = _capturing == action ? "키를 누르세요…" : Shortcuts.Describe(action, _bindings);
    }

    void StartCapture(ShortcutAction action)
    {
        _capturing = action;
        StatusText.Text = "";
        RefreshKeys();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_capturing is not ShortcutAction action)
        {
            if (e.Key == Key.Escape) DialogResult = false;
            return;
        }
        e.Handled = true;
        if (KeyChord.FromEvent(e) is not KeyChord chord) return; // modifier alone: wait for the real key

        _capturing = null;
        if (chord == new KeyChord(Key.Escape, ModifierKeys.None))
        {
            RefreshKeys();
            return;
        }
        Assign(action, new[] { chord });
    }

    /// <summary>Gives <paramref name="action"/> exactly these keys, taking them away from any other action.</summary>
    void Assign(ShortcutAction action, IReadOnlyList<KeyChord> chords)
    {
        var taken = new List<string>();
        foreach (var other in Shortcuts.Definitions.Select(d => d.Action).Where(a => a != action))
        {
            var keys = Shortcuts.For(other, _bindings);
            var kept = keys.Where(k => !chords.Contains(k)).ToList();
            if (kept.Count == keys.Count) continue;
            _bindings[other.ToString()] = kept.Select(k => k.Serialize()).ToList();
            taken.Add(Shortcuts.Definition(other).Label);
        }

        var defaults = Shortcuts.Definition(action).Defaults;
        if (chords.SequenceEqual(defaults)) _bindings.Remove(action.ToString());
        else _bindings[action.ToString()] = chords.Select(k => k.Serialize()).ToList();

        StatusText.Text = taken.Count > 0 ? $"'{string.Join("', '", taken)}'에서 이 키를 뺐어요." : "";
        RefreshKeys();
    }

    void ResetAction(ShortcutAction action)
    {
        _capturing = null;
        Assign(action, Shortcuts.Definition(action).Defaults);
    }

    void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        _capturing = null;
        _bindings.Clear();
        StatusText.Text = "모든 단축키를 기본값으로 되돌렸어요.";
        RefreshKeys();
    }

    void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (GeneralPanel == null || ShortcutsPanel == null) return; // during InitializeComponent
        bool general = GeneralTab.IsChecked == true;
        GeneralPanel.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPanel.Visibility = general ? Visibility.Collapsed : Visibility.Visible;
        _capturing = null;
        RefreshKeys();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = AppSettings.Current;
        var selected = SeekOptionsPanel.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true);
        if (selected != null) settings.SeekSeconds = (int)selected.Tag;
        settings.AutoPlayNext = AutoPlayNextBox.IsChecked == true;
        settings.LoadFolderAsPlaylist = FolderPlaylistBox.IsChecked == true;
        settings.FollowExplorerSort = FollowExplorerBox.IsChecked == true;
        settings.ResumePlayback = ResumeBox.IsChecked == true;
        settings.FastSeek = FastSeekBox.IsChecked == true;
        settings.KeyBindings = _bindings;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
