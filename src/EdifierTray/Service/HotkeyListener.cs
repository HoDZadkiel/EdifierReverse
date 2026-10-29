using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace EdifierTray.Service;

/// <summary>
/// 全域快捷鍵（Win32 RegisterHotKey，一律 Ctrl+Alt+按鍵）。要在 UI 執行緒建立：
/// 用一個只收訊息的隱藏視窗接 WM_HOTKEY，事件也就在 UI 執行緒觸發。
/// </summary>
public sealed class HotkeyListener : IDisposable
{
    const int WmHotkey = 0x0312, ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, HwndMessage = -3;

    [DllImport("user32.dll")] static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint hwnd, int id);

    readonly HwndSource _source = new(new HwndSourceParameters("EdifierTrayHotkeys") { ParentWindow = HwndMessage });
    readonly Dictionary<int, string> _ids = new();

    public event Action<string>? Pressed;

    /// <summary>註冊失敗（多半是被別的程式佔用）的名稱。</summary>
    public IReadOnlyList<string> Failed { get; }

    public HotkeyListener(IReadOnlyDictionary<string, uint[]> keys)
    {
        var failed = new List<string>();
        int id = 0;
        foreach (var (name, vks) in keys)
        {
            bool ok = false;
            foreach (var vk in vks)
            {
                id++;
                if (RegisterHotKey(_source.Handle, id, ModControl | ModAlt | ModNoRepeat, vk)) { _ids[id] = name; ok = true; }
            }
            if (!ok) failed.Add(name);  // 一個鍵都註冊不了才算失敗
        }
        Failed = failed;
        _source.AddHook(Hook);
    }

    nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmHotkey && _ids.TryGetValue((int)wParam, out var name))
        {
            handled = true;
            Pressed?.Invoke(name);
        }
        return 0;
    }

    public void Dispose()
    {
        foreach (var id in _ids.Keys) UnregisterHotKey(_source.Handle, id);
        _ids.Clear();
        _source.Dispose();
    }
}
