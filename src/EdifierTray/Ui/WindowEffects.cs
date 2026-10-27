using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EdifierTray.Ui;

/// <summary>Windows 11 視窗效果：圓角、深淺色標題、Acrylic 背景材質。彈出面板與右鍵選單共用。</summary>
public static class WindowEffects
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);

    /// <summary>在 OnSourceInitialized 之後呼叫。回傳是否成功套用 Acrylic；失敗（舊版 Windows）就維持實心背景。</summary>
    public static bool Apply(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int corner = 2, darkMode = dark ? 1 : 0, acrylic = 3;
        DwmSetWindowAttribute(hwnd, 33, ref corner, 4);      // 圓角
        DwmSetWindowAttribute(hwnd, 20, ref darkMode, 4);    // 深淺色
        // Windows 11 22H2 以上才有系統背景材質
        return Environment.OSVersion.Version.Build >= 22621 && DwmSetWindowAttribute(hwnd, 38, ref acrylic, 4) == 0;
    }

    /// <summary>視窗所在螢幕的縮放比例（1.0 = 96 DPI）。需要已建立視窗控制代碼。</summary>
    public static double DpiScale(Window window)
    {
        var dpi = GetDpiForWindow(new WindowInteropHelper(window).Handle);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }
}
