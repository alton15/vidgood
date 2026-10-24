using System.Windows.Input;

namespace VideoPlayer;

public enum ShortcutAction
{
    PlayPause, SeekBack, SeekForward, PreviousVideo, NextVideo, Restart,
    SpeedDown, SpeedUp, SpeedReset,
    VolumeUp, VolumeDown, Mute,
    Repeat, Rotate, Playlist, Fullscreen, ExitFullscreen,
    OpenFile, OpenFolder, AddPane, NewWindow, ClosePane, NextPane, Settings, ShowShortcuts,
}

/// <summary>A key plus modifiers, e.g. Ctrl+T. Stored in settings as text like "Ctrl+Shift+O".</summary>
public readonly record struct KeyChord(Key Key, ModifierKeys Modifiers)
{
    const ModifierKeys Relevant = ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt;

    static readonly Dictionary<Key, string> Names = new()
    {
        [Key.Left] = "←", [Key.Right] = "→", [Key.Up] = "↑", [Key.Down] = "↓",
        [Key.Back] = "Backspace", [Key.Return] = "Enter", [Key.Escape] = "Esc",
        [Key.PageUp] = "PageUp", [Key.PageDown] = "PageDown",
        [Key.OemOpenBrackets] = "[", [Key.OemCloseBrackets] = "]", [Key.OemComma] = ",", [Key.OemPeriod] = ".",
        [Key.OemPipe] = "\\", [Key.OemPlus] = "=", [Key.OemMinus] = "-", [Key.OemQuestion] = "/",
        [Key.OemSemicolon] = ";", [Key.OemQuotes] = "'", [Key.OemTilde] = "`",
    };

    public static KeyChord? FromEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin or Key.System or Key.None or Key.ImeProcessed)
            return null;
        return new KeyChord(key, Keyboard.Modifiers & Relevant);
    }

    /// <summary>Text shown to the user.</summary>
    public string Display => Prefix + (Names.TryGetValue(Key, out var name) ? name
        : Key is >= Key.D0 and <= Key.D9 ? ((int)(Key - Key.D0)).ToString()
        : Key.ToString());

    /// <summary>Stable text for the settings file.</summary>
    public string Serialize() => Prefix + Enum.GetName(Key);

    public static bool TryParse(string text, out KeyChord chord)
    {
        chord = default;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        var modifiers = ModifierKeys.None;
        foreach (var part in parts[..^1])
        {
            switch (part)
            {
                case "Ctrl": modifiers |= ModifierKeys.Control; break;
                case "Shift": modifiers |= ModifierKeys.Shift; break;
                case "Alt": modifiers |= ModifierKeys.Alt; break;
                default: return false;
            }
        }
        if (!Enum.TryParse<Key>(parts[^1], out var key)) return false;
        chord = new KeyChord(key, modifiers);
        return true;
    }

    string Prefix =>
        (Modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") +
        (Modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") +
        (Modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "");

    public override string ToString() => Display;
}

public sealed record ShortcutDefinition(ShortcutAction Action, string Group, string Label, KeyChord[] Defaults);

/// <summary>Keyboard shortcuts: built-in defaults plus the user's overrides from settings.</summary>
public static class Shortcuts
{
    static KeyChord K(Key key, ModifierKeys modifiers = ModifierKeys.None) => new(key, modifiers);
    const ModifierKeys Ctrl = ModifierKeys.Control, Shift = ModifierKeys.Shift, Alt = ModifierKeys.Alt;

    public static readonly ShortcutDefinition[] Definitions =
    {
        new(ShortcutAction.PlayPause, "재생", "재생 / 일시정지", new[] { K(Key.Space), K(Key.K) }),
        new(ShortcutAction.SeekBack, "재생", "뒤로 건너뛰기", new[] { K(Key.Left), K(Key.J) }),
        new(ShortcutAction.SeekForward, "재생", "앞으로 건너뛰기", new[] { K(Key.Right), K(Key.L) }),
        new(ShortcutAction.PreviousVideo, "재생", "이전 영상", new[] { K(Key.PageUp), K(Key.Left, Ctrl) }),
        new(ShortcutAction.NextVideo, "재생", "다음 영상", new[] { K(Key.PageDown), K(Key.Right, Ctrl) }),
        new(ShortcutAction.Restart, "재생", "처음부터", new[] { K(Key.Home) }),
        new(ShortcutAction.SpeedDown, "재생", "재생 속도 느리게", new[] { K(Key.OemOpenBrackets) }),
        new(ShortcutAction.SpeedUp, "재생", "재생 속도 빠르게", new[] { K(Key.OemCloseBrackets) }),
        new(ShortcutAction.SpeedReset, "재생", "재생 속도 1x", new[] { K(Key.Back) }),
        new(ShortcutAction.VolumeUp, "소리", "볼륨 크게", new[] { K(Key.Up) }),
        new(ShortcutAction.VolumeDown, "소리", "볼륨 작게", new[] { K(Key.Down) }),
        new(ShortcutAction.Mute, "소리", "음소거", new[] { K(Key.M) }),
        new(ShortcutAction.Repeat, "화면", "반복 모드 전환", new[] { K(Key.R) }),
        new(ShortcutAction.Rotate, "화면", "90° 회전", new[] { K(Key.R, Ctrl) }),
        new(ShortcutAction.Playlist, "화면", "재생 목록", new[] { K(Key.L, Ctrl) }),
        new(ShortcutAction.Fullscreen, "화면", "전체 화면", new[] { K(Key.F11), K(Key.F), K(Key.Enter, Alt) }),
        new(ShortcutAction.ExitFullscreen, "화면", "전체 화면 나가기", new[] { K(Key.Escape) }),
        new(ShortcutAction.OpenFile, "창", "파일 열기", new[] { K(Key.O, Ctrl) }),
        new(ShortcutAction.OpenFolder, "창", "폴더 열기", new[] { K(Key.O, Ctrl | Shift) }),
        new(ShortcutAction.AddPane, "창", "화면 추가", new[] { K(Key.T, Ctrl) }),
        new(ShortcutAction.NewWindow, "창", "새 창", new[] { K(Key.N, Ctrl) }),
        new(ShortcutAction.ClosePane, "창", "선택한 화면 닫기", new[] { K(Key.W, Ctrl) }),
        new(ShortcutAction.NextPane, "창", "다음 화면 선택", new[] { K(Key.Tab) }),
        new(ShortcutAction.Settings, "창", "설정", new[] { K(Key.OemComma, Ctrl) }),
        new(ShortcutAction.ShowShortcuts, "창", "단축키 보기", new[] { K(Key.F1) }),
    };

    public static ShortcutDefinition Definition(ShortcutAction action) => Definitions.First(d => d.Action == action);

    /// <summary>Effective keys for an action in <paramref name="overrides"/> (the saved settings by default).</summary>
    public static IReadOnlyList<KeyChord> For(ShortcutAction action, IDictionary<string, List<string>>? overrides = null)
    {
        overrides ??= AppSettings.Current.KeyBindings;
        if (overrides.TryGetValue(action.ToString(), out var saved))
            return saved.Select(s => KeyChord.TryParse(s, out var c) ? c : (KeyChord?)null).OfType<KeyChord>().ToList();
        return Definition(action).Defaults;
    }

    public static ShortcutAction? Find(KeyChord chord) =>
        Definitions.Select(d => d.Action).Cast<ShortcutAction?>().FirstOrDefault(a => For(a!.Value).Contains(chord));

    /// <summary>"Space / K" style text, or "없음".</summary>
    public static string Describe(ShortcutAction action, IDictionary<string, List<string>>? overrides = null)
    {
        var keys = For(action, overrides);
        return keys.Count == 0 ? "없음" : string.Join(" / ", keys.Select(k => k.Display));
    }

    /// <summary>" (Ctrl+T)" for tooltips, using the first key; empty when unbound.</summary>
    public static string Hint(ShortcutAction action)
    {
        var keys = For(action);
        return keys.Count == 0 ? "" : $" ({keys[0].Display})";
    }
}
