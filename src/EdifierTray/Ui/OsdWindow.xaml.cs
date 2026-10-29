using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace EdifierTray.Ui;

/// <summary>螢幕顯示（OSD）：工作區下方置中的小提示條，顯示一下就自己消失。不搶焦點、滑鼠可穿透。</summary>
public partial class OsdWindow : Window
{
    public static readonly TimeSpan ShowTime = TimeSpan.FromMilliseconds(1800);
    const double BottomGap = 72;  // 與工作區底緣的距離（DIP）

    [DllImport("user32.dll")] static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(nint hwnd, int index, int value);

    readonly DispatcherTimer _timer = new() { Interval = ShowTime };

    public OsdWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => HideOsd();
    }

    public string Text => Message.Text;
    public bool IsShowing => Visibility == Visibility.Visible;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // 不搶焦點（NOACTIVATE）、不出現在 Alt+Tab（TOOLWINDOW）、滑鼠穿透（TRANSPARENT）
        var h = new WindowInteropHelper(this).Handle;
        SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x08000000 | 0x80 | 0x20);
    }

    public void ShowOsd(string text)
    {
        Message.Text = text;
        // 面板有 Acrylic 時 BgBrush 會略透明；OSD 沒有背景材質，用同色的實心版本
        if (FindResource("BgBrush") is SolidColorBrush b)
            Root.Background = new SolidColorBrush(Color.FromRgb(b.Color.R, b.Color.G, b.Color.B));
        Show();
        UpdateLayout();
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - BottomGap;
        _timer.Stop();
        _timer.Start();
    }

    public void HideOsd()
    {
        _timer.Stop();
        Hide();
    }
}
