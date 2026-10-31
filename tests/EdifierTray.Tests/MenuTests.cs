using EdifierTray.Service;
using EdifierTray.Ui;
using Microsoft.Win32;
using Xunit;

namespace EdifierTray.Tests;

/// <summary>選單的大項各有子選單；多數測試只關心「有哪些項目」，把子項目攤平來看。</summary>
public static class MenuTestUtil
{
    public static List<MenuEntry> Flat(IEnumerable<MenuEntry> m) =>
        m.SelectMany(e => new[] { e }.Concat(e.Children ?? Array.Empty<MenuEntry>())).ToList();
}

public class MenuModelTests
{
    static DeviceState Conn(int mode = 1, int group = 16) =>
        new(true, "EDIFIER ES850NB", "1.0.1", 16, new AncState(group, mode));

    static MenuEntry Item(IReadOnlyList<MenuEntry> m, string id) => m.Single(e => e.Id == id);

    [Fact] public void Output_devices_are_listed_with_the_default_one_checked_and_hands_free_endpoints_are_not_offered()
    {
        var outs = new[] { new AudioOutput("{0}.{A}", "喇叭 (EDIFIER MP330)", true), new AudioOutput("{0}.{B}", "耳機 (EDIFIER ES850NB)", false) };
        var m = MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, false, false, outputs: outs));
        Assert.Equal(new[] { "out:{0}.{A}", "out:{0}.{B}" }, m.Where(e => e.Id.StartsWith("out:")).Select(e => e.Id));
        Assert.Equal(new[] { "out:{0}.{A}" }, m.Where(e => e.Id.StartsWith("out:") && e.Checked).Select(e => e.Id));
        Assert.DoesNotContain(MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, false, false)), e => e.Id.StartsWith("out:"));   // 沒有輸出裝置資料就不顯示這一區
        Assert.False(AudioOutputs.IsListable("Headset (EDIFIER ES850NB Hands-Free)"));   // 藍牙通話端點音質低，不放進選單
        Assert.True(AudioOutputs.IsListable("Headphones (EDIFIER ES850NB Stereo)"));
        Assert.Equal(34, AudioOutputs.Shorten(new string('x', 80)).Length);
    }

    [Fact] public void Keep_awake_item_shows_only_when_there_is_a_current_device_and_reflects_its_state()
    {
        Assert.DoesNotContain(MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, false, false)), e => e.Id == "keepawake");
        Assert.False(Item(MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, false, false, keepAwake: false)), "keepawake").Checked);
        Assert.True(Item(MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, false, false, keepAwake: true)), "keepawake").Checked);
    }

    [Fact] public void Device_without_anc_hides_the_anc_items_and_shows_only_the_battery()
    {
        // MP330 沒有降噪：連上時 ANC 查詢沒有回應 → HasAnc=false，選單不放降噪項目，標頭也不顯示「未知」模式
        var m = MenuTestUtil.Flat(TrayMenuModel.Build(new DeviceState(true, "EDIFIER MP330", "1.0.1", 100, null, HasAnc: false), 50, false, false));
        Assert.DoesNotContain(m, e => e.Id.StartsWith("anc:"));
        Assert.Equal("電量 100%", m[0].Detail);
        Assert.Equal(new[] { "mute", "vol+", "vol-" }, m.Where(e => e.Id != "" && e.Kind != EntryKind.Section).Select(e => e.Id).Take(3));
        Assert.DoesNotContain(m.Zip(m.Skip(1)), p => p.First.Kind == EntryKind.Separator && p.Second.Kind == EntryKind.Separator);
    }

    [Fact] public void Connected_menu_has_all_the_quick_actions_and_marks_the_current_mode()
    {
        var m = MenuTestUtil.Flat(TrayMenuModel.Build(Conn(mode: 3), 50, false, false));
        Assert.Equal(new[] { "anc:1", "anc:2", "anc:3", "anc:4", "anc:5", "mute", "vol+", "vol-", "rescan", "open", "reconnect", "autostart", "quit" },
                     m.Where(e => e.Id != "" && e.Kind != EntryKind.Section).Select(e => e.Id));
        Assert.Equal(new[] { "anc:3" }, m.Where(e => e.Id.StartsWith("anc:") && e.Checked).Select(e => e.Id));
        Assert.All(m.Where(e => e.Kind == EntryKind.Item && e.Id != ""), e => Assert.True(e.Enabled));  // 沒有裝置時的提示列本來就是停用的
        Assert.Contains("電量 16%", m[0].Detail);
        Assert.Contains("防風噪", m[0].Detail);
    }

    [Fact] public void Mute_item_flips_its_label_and_icon_and_shows_the_volume()
    {
        var on = Item(MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 35, false, false)), "mute");
        var off = Item(MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 35, true, false)), "mute");
        Assert.Equal(("靜音", "35%"), (on.Text, on.Detail));
        Assert.Equal("取消靜音", off.Text);
        Assert.NotEqual(on.Glyph, off.Glyph);
    }

    [Fact] public void Disconnected_disables_anc_and_volume_but_keeps_reconnect_and_quit()
    {
        var m = MenuTestUtil.Flat(TrayMenuModel.Build(new DeviceState(), null, false, false));
        Assert.All(new[] { "anc:1", "anc:5", "mute", "vol+", "vol-" }, id => Assert.False(Item(m, id).Enabled));
        Assert.All(new[] { "open", "reconnect", "autostart", "quit" }, id => Assert.True(Item(m, id).Enabled));
        Assert.Equal("未連線", m[0].Detail);
        Assert.DoesNotContain(m, e => e.Checked && e.Id.StartsWith("anc:"));
    }

    [Fact] public void Unverified_group_keeps_anc_disabled_and_unchecked_but_volume_still_works()
    {
        var m = MenuTestUtil.Flat(TrayMenuModel.Build(Conn(group: 18), 50, false, false));
        Assert.False(Item(m, "anc:2").Enabled);
        Assert.DoesNotContain(m, e => e.Checked && e.Id.StartsWith("anc:"));
        Assert.True(Item(m, "vol+").Enabled);
    }

    [Fact] public void Missing_volume_endpoint_disables_only_the_volume_items()
    {
        var m = MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), null, false, false));
        Assert.False(Item(m, "mute").Enabled);
        Assert.True(Item(m, "anc:2").Enabled);
    }

    [Fact] public void Autostart_item_reflects_the_setting() =>
        Assert.True(Item(MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, false, true)), "autostart").Checked);

    static readonly AudioOutput[] Outs = { new("{A}", "喇叭 (EDIFIER MP330)", true), new("{B}", "耳機 (EDIFIER ES850NB)", false) };

    /// <summary>主選單項目加上各大項的子項目（子選單的內容）。</summary>
    static List<MenuEntry> Flat(IEnumerable<MenuEntry> m) =>
        m.SelectMany(e => new[] { e }.Concat(e.Children ?? Array.Empty<MenuEntry>())).ToList();

    [Fact] public void Groups_are_submenus_that_pop_out_so_the_top_level_stays_short()
    {
        var devs = new[] { ("AA", "MP330 · A3:3D", true), ("BB", "ES850NB · 78:A7", false) };
        var m = TrayMenuModel.Build(Conn(mode: 3), 50, false, false, devs, outputs: Outs, keepAwake: true);
        Assert.Equal(new[] { "sec:anc", "sec:devices", "sec:output", "sec:power", "sec:look" }, m.Where(e => e.Kind == EntryKind.Section).Select(e => e.Id));
        // 主選單只有大項標題、音量與底部動作；子項目都在 Children 裡，不會把主選單撐長
        Assert.DoesNotContain(m, e => e.Id.StartsWith("anc:") || e.Id.StartsWith("dev:") || e.Id.StartsWith("out:") || e.Id is "keepawake" or "rescan");
        Assert.DoesNotContain(m, e => e.Kind == EntryKind.Segmented);
        Assert.True(m.Count <= 18, $"主選單有 {m.Count} 列");
        Assert.Contains(m, e => e.Id == "mute");
        Assert.Contains(m, e => e.Id == "quit");
        var kids = (string id) => m.Single(e => e.Id == id).Children!.Select(e => e.Id).Where(x => x != "").ToList();
        Assert.Equal(new[] { "anc:1", "anc:2", "anc:3", "anc:4", "anc:5" }, kids("sec:anc"));
        Assert.Equal(new[] { "dev:AA", "dev:BB", "rescan" }, kids("sec:devices"));
        Assert.Equal(new[] { "out:{A}", "out:{B}" }, kids("sec:output"));
        Assert.Equal(new[] { "keepawake" }, kids("sec:power"));   // 保持開機在「電源」底下，不在裝置清單裡
        Assert.Contains(m.Single(e => e.Id == "sec:look").Children!, e => e.Kind == EntryKind.Segmented);
        Assert.DoesNotContain(m.Zip(m.Skip(1)), p => p.First.Kind == EntryKind.Separator && p.Second.Kind == EntryKind.Separator);
    }

    [Fact] public void Section_titles_summarize_the_current_state_so_it_is_visible_without_opening_them()
    {
        var devs = new[] { ("AA", "MP330 · A3:3D", true), ("BB", "ES850NB · 78:A7", false) };
        var m = TrayMenuModel.Build(Conn(mode: 3), 50, false, false, devs, outputs: Outs, keepAwake: true);
        string Summary(string id) => m.Single(e => e.Id == id).Detail;
        Assert.Equal("防風噪", Summary("sec:anc"));
        Assert.Equal("MP330 · A3:3D", Summary("sec:devices"));
        Assert.Equal("喇叭 (EDIFIER MP330)", Summary("sec:output"));
        Assert.Equal("開", Summary("sec:power"));
        Assert.Equal("", Summary("sec:look"));
    }

    [Fact] public void Power_section_shows_the_current_power_saving_mode_with_a_check_item_and_the_native_switch()
    {
        var mp = new DeviceState(true, "EDIFIER MP330", "1.0.1", 100, null, HasAnc: false, PowerSave: true);
        var m = TrayMenuModel.Build(mp, 50, false, false, keepAwake: false);
        var power = m.Single(e => e.Id == "sec:power");
        Assert.Equal("省電模式：開", power.Detail);   // 不用打開子選單就看得到目前是不是省電模式
        Assert.Equal(new[] { "powercheck", "powersave" }, power.Children!.Select(e => e.Id));
        Assert.Equal("開", power.Children![0].Detail);
        Assert.False(power.Children![1].Checked);     // 省電模式開著＝防自動關機沒開
        Assert.DoesNotContain(MenuTestUtil.Flat(m), e => e.Id == "keepawake");   // 有原生做法就不放「保持開機」

        var off = TrayMenuModel.Build(mp with { PowerSave = false }, 50, false, false, keepAwake: false);
        Assert.Equal("省電模式：關", off.Single(e => e.Id == "sec:power").Detail);
        Assert.True(off.Single(e => e.Id == "sec:power").Children![1].Checked);   // 省電模式關＝防自動關機已開

        // ES850NB：查不到省電模式 → 退回「保持開機」
        var es = TrayMenuModel.Build(Conn(), 50, false, false, keepAwake: false);
        Assert.Equal(new[] { "keepawake" }, es.Single(e => e.Id == "sec:power").Children!.Select(e => e.Id));
    }

    [Fact] public void Sections_that_have_nothing_to_offer_are_left_out()
    {
        var m = TrayMenuModel.Build(Conn(), 50, false, false);   // 沒有輸出裝置資料、沒有目前裝置
        Assert.DoesNotContain(m, e => e.Id is "sec:output" or "sec:power");
        var noAnc = TrayMenuModel.Build(new DeviceState(true, "EDIFIER MP330", "1.0.1", 100, null, HasAnc: false), 50, false, false);
        Assert.DoesNotContain(noAnc, e => e.Id == "sec:anc");
    }

    [Fact] public void Every_entry_with_an_id_has_a_handler_in_the_host_contract()
    {
        // TrayHost.OnMenuItem 處理的代號；新增選單項目卻忘了接動作時，這裡會提醒
        var handled = new HashSet<string> { "sec", "mute", "vol+", "vol-", "rescan", "open", "reconnect", "autostart", "quit" };
        foreach (var e in MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, false, false)).Where(e => e.Id != "" && !e.Id.StartsWith("anc:") && !e.Id.StartsWith("dev:")))
            Assert.Contains(e.Kind == EntryKind.Section ? "sec" : e.Id, handled);
    }

    [Theory]
    [InlineData(2400, 2085, 1.5, 0, 0, 2560, 1392)]   // 工作列在下方：游標在螢幕底部
    [InlineData(300, 10, 1.0, 0, 40, 1920, 1080)]     // 工作列在上方：游標在頂部
    [InlineData(1910, 1070, 1.0, 0, 0, 1920, 1040)]   // 游標在最右下角
    [InlineData(0, 500, 1.0, 0, 0, 1920, 1080)]       // 最左邊
    public void Placement_stays_inside_the_work_area(double cx, double cy, double scale, double l, double t, double r, double b)
    {
        var (left, top) = TrayMenuModel.Place(cx, cy, 272, 552, scale, l, t, r, b);
        Assert.InRange(left, l, r - 272);
        Assert.InRange(top, t, b - 552);
    }

    [Fact] public void Placement_prefers_above_the_cursor_unless_there_is_no_room()
    {
        var (_, above) = TrayMenuModel.Place(1000, 1000, 272, 400, 1.0, 0, 0, 1920, 1080);
        Assert.True(above < 1000);
        var (_, below) = TrayMenuModel.Place(1000, 20, 272, 400, 1.0, 0, 0, 1920, 1080);
        Assert.True(below > 20);
    }

    [Fact] public void Placement_converts_device_pixels_to_dips_with_the_screen_scale()
    {
        var (left, _) = TrayMenuModel.Place(1500, 1500, 272, 100, 1.5, 0, 0, 2560, 1392);
        Assert.Equal(1500 / 1.5 - 8, left, 3);
    }
}

public class AutostartTests
{
    [Fact] public void Enable_writes_a_quoted_path_and_disable_removes_it()
    {
        var root = $"Software\\EdifierTrayTest_{Guid.NewGuid():N}";  // 獨立測試路徑，不碰真正的開機項目
        var key = root + "\\Run";
        try
        {
            Assert.False(Autostart.IsEnabled(key));
            Autostart.Set(true, key, "T", "C:\\Program Files\\x y\\E.exe");
            Assert.True(Autostart.IsEnabled(key, "T"));
            using (var k = Registry.CurrentUser.OpenSubKey(key))
                Assert.Equal("\"C:\\Program Files\\x y\\E.exe\"", k!.GetValue("T"));  // 路徑含空白，必須加引號
            Autostart.Set(false, key, "T");
            Assert.False(Autostart.IsEnabled(key, "T"));
            Autostart.Set(false, key, "T");  // 重複停用不能出錯
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(root, false); }
    }

    [Fact] public void The_default_key_is_the_current_user_run_key() =>
        Assert.Equal("Software\\Microsoft\\Windows\\CurrentVersion\\Run", Autostart.DefaultKey);
}
