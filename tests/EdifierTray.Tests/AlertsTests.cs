using EdifierTray.Service;
using Xunit;

namespace EdifierTray.Tests;

/// <summary>OSD 訊息與快捷鍵輪替的純邏輯。測試向量與 Python 的 tests/test_alerts.py 一致。</summary>
public class AlertsTests : IDisposable
{
    public AlertsTests() => Strings.SetLanguage("zh-TW");
    public void Dispose() => Strings.SetLanguage(Strings.Default);

    static DeviceState St(int? battery = 80, int mode = 1, bool connected = true, int group = 16) =>
        new(connected, "EDIFIER ES850NB", "1.0.1", battery, new AncState(group, mode));

    // ---- 電量告警：只在往下穿過門檻時跳一次，回到門檻以上才重新武裝 ----
    [Fact] public void Low_battery_alerts_once_when_crossing_the_threshold()
    {
        var t = new OsdTracker();
        Assert.Empty(t.Update(St(OsdTracker.LowBattery + 1)));
        Assert.Equal(new[] { "電量偏低：20%" }, t.Update(St(OsdTracker.LowBattery)));
        Assert.Empty(t.Update(St(15)));                       // 還在低電量，不重複跳
        Assert.Empty(t.Update(St(OsdTracker.LowBattery + 1)));
        Assert.Equal(new[] { "電量偏低：20%" }, t.Update(St(OsdTracker.LowBattery)));  // 充電回到門檻以上後再掉下去，會再告警
    }

    [Fact] public void Low_battery_is_reported_when_connecting_with_an_already_low_battery() =>
        Assert.Equal(new[] { "電量偏低：8%" }, new OsdTracker().Update(St(8)));

    [Fact] public void Reconnecting_while_still_low_does_not_alert_again()
    {
        var t = new OsdTracker();
        t.Update(St(10));
        Assert.Empty(t.Update(St(10, connected: false)));
        Assert.Empty(t.Update(St(10)));                       // 斷線重連不是新的告警
    }

    [Fact] public void Unknown_battery_never_alerts() => Assert.Empty(new OsdTracker().Update(St(null)));

    // ---- 模式改變 ----
    [Fact] public void Mode_change_shows_the_new_mode_including_changes_made_on_the_headset()
    {
        var t = new OsdTracker();
        Assert.Empty(t.Update(St(mode: 1)));                  // 第一筆是現況，不是改變
        Assert.Empty(t.Update(St(mode: 1)));                  // 電量、冷卻旗標等其他更新不重複顯示
        Assert.Equal(new[] { "降噪：舒適降噪" }, t.Update(St(mode: 2)));
    }

    [Fact] public void First_state_after_reconnect_is_not_a_mode_change()
    {
        var t = new OsdTracker();
        t.Update(St(mode: 1));
        t.Update(St(mode: 1, connected: false));
        Assert.Empty(t.Update(St(mode: 3)));                  // 斷線期間耳機可能被切過，重連的現況不當成改變
        Assert.Equal(new[] { "降噪：可調增益環境音" }, t.Update(St(mode: 4)));
    }

    [Fact] public void Ambient_strength_change_alone_is_not_a_mode_change()
    {
        var t = new OsdTracker();
        t.Update(new DeviceState(true, "X", "1", 80, new AncState(16, 4, 3)));
        Assert.Empty(t.Update(new DeviceState(true, "X", "1", 80, new AncState(16, 4, 9))));
    }

    // ---- 快捷鍵輪替 ----
    [Theory]
    [InlineData(1, 2)] [InlineData(2, 3)] [InlineData(4, 5)] [InlineData(5, 1)]
    public void Next_anc_mode_cycles_and_wraps(int mode, int expect) => Assert.Equal(expect, Hotkeys.NextAncMode(St(mode: mode)));

    [Fact] public void Next_anc_mode_refuses_when_it_cannot_set()
    {
        Assert.Null(Hotkeys.NextAncMode(St(connected: false)));
        Assert.Null(Hotkeys.NextAncMode(St(group: 18)));      // 未驗證的 group 不開放設定
        Assert.Null(Hotkeys.NextAncMode(new DeviceState(true, "X", "1", 80, null)));
        Assert.Equal(1, Hotkeys.NextAncMode(St(mode: 9)));    // 不在表內（韌體新增的模式）就從頭開始
    }

    [Fact] public void Hotkeys_are_unique_virtual_keys_and_match_the_python_table()
    {
        var all = Hotkeys.Keys.Values.SelectMany(v => v).ToList();
        Assert.Equal(5, all.Distinct().Count());
        Assert.Equal(new uint[] { 0x41, 0x26, 0x28, 0x30, 0x60 }, all);   // 與 alerts.py 的 HOTKEYS 一致：A、↑、↓、0、數字鍵盤 0
        Assert.Equal(Hotkeys.Keys.Keys, Hotkeys.Labels.Keys);
    }
}
