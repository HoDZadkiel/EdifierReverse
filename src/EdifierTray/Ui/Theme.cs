using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace EdifierTray.Ui;

/// <summary>Windows 11 深淺色與系統強調色。顏色放進 Application.Resources，XAML 以 DynamicResource 引用。</summary>
public static class Theme
{
    /// <summary>驗證用：強制深色或淺色（正常執行時為 null，跟隨系統）。</summary>
    public static bool? DarkOverride;

    /// <summary>使用者選的外觀：system（跟隨系統）、light（日間）、dark（夜間）。</summary>
    public static string Mode = "system";

    /// <summary>目前實際該用深色還是淺色：命令列驗證用的強制值 > 使用者選的外觀 > 系統設定。</summary>
    public static bool IsDark()
    {
        if (DarkOverride is bool o) return o;
        return Mode switch { "dark" => true, "light" => false, _ => SystemIsDark() };
    }

    /// <summary>外觀切換後：重寫顏色資源，並讓已開過的視窗重新套用圓角、深淺色與背景材質。</summary>
    public static void Refresh(params System.Windows.Window[] windows)
    {
        bool dark = IsDark(), acrylic = false;
        foreach (var w in windows)
            if (new System.Windows.Interop.WindowInteropHelper(w).Handle != 0) acrylic |= WindowEffects.Apply(w, dark);
        Apply(dark, acrylic ? 0.82 : 1.0);
    }

    public static bool SystemIsDark()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (k?.GetValue("AppsUseLightTheme") as int? ?? 0) == 0;
        }
        catch { return true; }
    }

    static Color Hex(string s) => (Color)ColorConverter.ConvertFromString(s);

    static Color SystemAccent(bool dark)
    {
        try
        {
            var c = new UISettings().GetColorValue(dark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
            return Color.FromRgb(c.R, c.G, c.B);
        }
        catch { return dark ? Hex("#60CDFF") : Hex("#005FB8"); }
    }

    public static void Apply(bool dark, double backdropAlpha = 1.0)
    {
        var r = Application.Current.Resources;
        void Set(string key, Color c) => r[key] = new SolidColorBrush(c);
        var bg = dark ? Hex("#202020") : Hex("#F3F3F3");
        bg.A = (byte)Math.Round(255 * backdropAlpha);  // 有 Mica／Acrylic 時略透明，讓背景材質透出來
        Set("BgBrush", bg);
        Set("SurfaceBrush", dark ? Hex("#2D2D2D") : Hex("#FFFFFF"));
        Set("HoverBrush", dark ? Hex("#383838") : Hex("#F0F0F0"));
        Set("BorderBrush2", dark ? Hex("#404040") : Hex("#E0E0E0"));
        Set("TextBrush", dark ? Hex("#FFFFFF") : Hex("#1B1B1B"));
        Set("SubBrush", dark ? Hex("#B0B0B0") : Hex("#5E5E5E"));
        var accent = SystemAccent(dark);
        Set("AccentBrush", accent);
        var luminance = 0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B;
        Set("AccentTextBrush", luminance > 140 ? Colors.Black : Colors.White);
        Set("TrackBrush", dark ? Hex("#6B6B6B") : Hex("#8A8A8A"));
        Set("DisabledBrush", dark ? Hex("#5A5A5A") : Hex("#B5B5B5"));
        Set("ErrorBrush", dark ? Hex("#FF99A4") : Hex("#C42B1C"));
    }
}

