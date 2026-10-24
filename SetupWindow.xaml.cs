using System.Windows;

namespace VideoPlayer;

/// <summary>Shown while libVLC is unpacked on the first run.</summary>
public partial class SetupWindow : Window
{
    double _shown;

    public SetupWindow() => InitializeComponent();

    /// <summary>Thread-safe progress update (0–1); throttled to visible steps.</summary>
    public void Report(double fraction)
    {
        if (fraction - _shown < 0.01 && fraction < 1) return;
        _shown = fraction;
        Dispatcher.BeginInvoke(() => Bar.Value = fraction);
    }
}
