namespace EdifierTray.Service;

/// <summary>
/// 比對連續的裝置狀態，產生該顯示的 OSD 訊息：電量掉到門檻以下、降噪模式改變（含耳機按鍵或手機切換）。
/// 純邏輯，對應 Python 版 alerts.py，兩邊用同樣的測試向量。
/// </summary>
public sealed class OsdTracker
{
    /// <summary>電量 &lt;= 這個值時告警一次；回到高於此值才會再次告警（避免在門檻上下抖動時一直跳）。</summary>
    public const int LowBattery = 20;

    bool _low;
    (int? Group, int Mode)? _mode;

    public IReadOnlyList<string> Update(DeviceState s)
    {
        var msgs = new List<string>();
        if (!s.Connected)
        {
            _mode = null;  // 重連後的第一筆是「現況」，不是「改變」
            return msgs;   // 電量告警旗標刻意不重設：充飽前反覆斷線重連不會一直跳
        }
        if (s.Battery is int b)
        {
            if (b > LowBattery) _low = false;
            else if (!_low) { _low = true; msgs.Add(Strings.T("o.low_battery", new { b })); }
        }
        if (s.Anc is { } anc)
        {
            var cur = (anc.Group, anc.Mode);
            if (_mode is not null && _mode != cur) msgs.Add(Strings.T("t.anc", new { mode = s.AncLabel }));
            _mode = cur;
        }
        return msgs;
    }
}

/// <summary>全域快捷鍵（一律 Ctrl+Alt+按鍵）：名稱與托盤選單的項目 id 一致（anc 是輪替下一個降噪模式）。</summary>
public static class Hotkeys
{
    /// <summary>名稱 -> 虛擬鍵碼（可多個，任一個按下都算）：A、↑、↓、0（靜音同時接主鍵盤與數字鍵盤的 0）。</summary>
    public static readonly IReadOnlyDictionary<string, uint[]> Keys = new Dictionary<string, uint[]>
    {
        ["anc"] = new uint[] { 0x41 }, ["vol+"] = new uint[] { 0x26 }, ["vol-"] = new uint[] { 0x28 },
        ["mute"] = new uint[] { 0x30, 0x60 },
    };

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["anc"] = "Ctrl+Alt+A", ["vol+"] = "Ctrl+Alt+↑", ["vol-"] = "Ctrl+Alt+↓", ["mute"] = "Ctrl+Alt+0",
    };

    /// <summary>
    /// 快捷鍵輪替用：目前模式的下一個（最後一個接回第一個）；目前模式不在表內就從第一個開始。
    /// 沒連線、沒有 ANC 資料或 group 未驗證（不開放設定）時回傳 null。
    /// </summary>
    public static int? NextAncMode(DeviceState s)
    {
        if (!s.Connected || !s.AncSupported) return null;
        var modes = DeviceService.AncModes[s.Anc!.Group!.Value];
        int i = modes.ToList().IndexOf(s.Anc.Mode);
        return i < 0 ? modes[0] : modes[(i + 1) % modes.Count];
    }
}
