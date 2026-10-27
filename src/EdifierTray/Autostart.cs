using Microsoft.Win32;

namespace EdifierTray;

/// <summary>
/// 開機自動啟動：在目前使用者的 Run 機碼寫一筆（只在使用者從選單切換時才改，不需要系統管理員權限）。
/// 機碼路徑可注入，測試用獨立路徑，不會動到真正的開機項目。
/// </summary>
public static class Autostart
{
    public const string DefaultKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "EdifierTray";

    public static bool IsEnabled(string key = DefaultKey, string name = ValueName)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(key);
            return k?.GetValue(name) is string;
        }
        catch { return false; }
    }

    /// <summary>啟用時寫入目前這支 exe 的路徑（加引號，路徑含空白也安全）；程式啟動後本來就是常駐托盤。</summary>
    public static void Set(bool enabled, string key = DefaultKey, string name = ValueName, string? exePath = null)
    {
        using var k = Registry.CurrentUser.CreateSubKey(key);
        if (enabled)
        {
            var path = exePath ?? Environment.ProcessPath ?? throw new InvalidOperationException("找不到執行檔路徑");
            k.SetValue(name, $"\"{path}\"");
        }
        else k.DeleteValue(name, throwOnMissingValue: false);
    }
}
