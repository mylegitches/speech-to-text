using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SpeechToText.UI;

/// <summary>
/// Small floating status pill at the bottom of the screen. It never takes focus and clicks
/// pass through it, so the app you are dictating into keeps the caret.
/// </summary>
public partial class RecordingPill : Window
{
    const int BarCount = 5;
    readonly Rectangle[] _bars = new Rectangle[BarCount];
    readonly float[] _history = new float[BarCount];
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    // Brand tokens: recording = danger, note = accent-text, busy = warning, done = success (always with an icon + label).
    static Brush B(string key) => (Brush)Application.Current.FindResource(key);
    static Brush Red => B("Danger");
    static Brush Amber => B("Warning");
    static Brush Green => B("Success");
    static Brush Blue => B("AccentText");

    public RecordingPill()
    {
        InitializeComponent();
        for (int i = 0; i < BarCount; i++)
        {
            _bars[i] = new Rectangle { Width = 3, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = B("Text"),
                                       Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center };
            Bars.Children.Add(_bars[i]);
        }
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); Hide(); };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE)
                | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT);
        };
    }

    public void ShowListening(bool note)
    {
        _hideTimer.Stop();
        Glyph.Text = note ? "" : ""; // note / mic
        Glyph.Foreground = note ? Blue : Red;
        Bars.Visibility = Visibility.Visible;
        Label.Text = note ? "Note" : "Listening";
        Array.Clear(_history);
        foreach (var b in _bars) b.Height = 3;
        Present();
    }

    public void ShowBusy()
    {
        Glyph.Foreground = Amber;
        Glyph.Text = ""; // sync
        Bars.Visibility = Visibility.Collapsed;
        Label.Text = "Transcribing…";
    }

    public void Flash(string text, bool ok = true)
    {
        Glyph.Text = ok ? "" : ""; // check / error
        Glyph.Foreground = ok ? Green : Red;
        Bars.Visibility = Visibility.Collapsed;
        Label.Text = text;
        Present();
        _hideTimer.Start();
    }

    /// <summary>Feed mic peak level (0..1). Safe to call from any thread.</summary>
    public void PushLevel(float level) => Dispatcher.BeginInvoke(() =>
    {
        Array.Copy(_history, 1, _history, 0, BarCount - 1);
        _history[^1] = MathF.Min(1, MathF.Sqrt(level) * 1.4f); // sqrt: speech sits low on a linear scale
        for (int i = 0; i < BarCount; i++) _bars[i].Height = 3 + _history[i] * 15;
    });

    void Present()
    {
        if (IsVisible) return;
        Opacity = 0;
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 24;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }

    const int GWL_EXSTYLE = -20, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TRANSPARENT = 0x20;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
}
