using EdifierTray.Service;
using EdifierTray.Ui;
using Microsoft.Win32;
using Xunit;

namespace EdifierTray.Tests;

public class MenuModelTests
{
    static DeviceState Conn(int mode = 1, int group = 16) =>
        new(true, "EDIFIER ES850NB", "1.0.1", 16, new AncState(group, mode));

    static MenuEntry Item(IReadOnlyList<MenuEntry> m, string id) => m.Single(e => e.Id == id);

    [Fact] public void Connected_menu_has_all_the_quick_actions_and_marks_the_current_mode()
    {
        var m = TrayMenuModel.Build(Conn(mode: 3), 50, false, false);
        Assert.Equal(new[] { "anc:1", "anc:2", "anc:3", "anc:4", "anc:5", "mute", "vol+", "vol-", "rescan", "open", "reconnect", "autostart", "quit" },
                     m.Where(e => e.Id != "").Select(e => e.Id));
        Assert.Equal(new[] { "anc:3" }, m.Where(e => e.Id.StartsWith("anc:") && e.Checked).Select(e => e.Id));
        Assert.All(m.Where(e => e.Kind == EntryKind.Item && e.Id != ""), e => Assert.True(e.Enabled));  // 沒有裝置時的提示列本來就是停用的
        Assert.Contains("電量 16%", m[0].Detail);
        Assert.Contains("防風噪", m[0].Detail);
    }

    [Fact] public void Mute_item_flips_its_label_and_icon_and_shows_the_volume()
    {
        var on = Item(TrayMenuModel.Build(Conn(), 35, false, false), "mute");
        var off = Item(TrayMenuModel.Build(Conn(), 35, true, false), "mute");
        Assert.Equal(("靜音", "35%"), (on.Text, on.Detail));
        Assert.Equal("取消靜音", off.Text);
        Assert.NotEqual(on.Glyph, off.Glyph);
    }

    [Fact] public void Disconnected_disables_anc_and_volume_but_keeps_reconnect_and_quit()
    {
        var m = TrayMenuModel.Build(new DeviceState(), null, false, false);
        Assert.All(new[] { "anc:1", "anc:5", "mute", "vol+", "vol-" }, id => Assert.False(Item(m, id).Enabled));
        Assert.All(new[] { "open", "reconnect", "autostart", "quit" }, id => Assert.True(Item(m, id).Enabled));
        Assert.Equal("未連線", m[0].Detail);
        Assert.DoesNotContain(m, e => e.Checked && e.Id.StartsWith("anc:"));
    }

    [Fact] public void Unverified_group_keeps_anc_disabled_and_unchecked_but_volume_still_works()
    {
        var m = TrayMenuModel.Build(Conn(group: 18), 50, false, false);
        Assert.False(Item(m, "anc:2").Enabled);
        Assert.DoesNotContain(m, e => e.Checked && e.Id.StartsWith("anc:"));
        Assert.True(Item(m, "vol+").Enabled);
    }

    [Fact] public void Missing_volume_endpoint_disables_only_the_volume_items()
    {
        var m = TrayMenuModel.Build(Conn(), null, false, false);
        Assert.False(Item(m, "mute").Enabled);
        Assert.True(Item(m, "anc:2").Enabled);
    }

    [Fact] public void Autostart_item_reflects_the_setting() =>
        Assert.True(Item(TrayMenuModel.Build(Conn(), 50, false, true), "autostart").Checked);

    [Fact] public void Every_entry_with_an_id_has_a_handler_in_the_host_contract()
    {
        // TrayHost.OnMenuItem 處理的代號；新增選單項目卻忘了接動作時，這裡會提醒
        var handled = new HashSet<string> { "mute", "vol+", "vol-", "rescan", "open", "reconnect", "autostart", "quit" };
        foreach (var e in TrayMenuModel.Build(Conn(), 50, false, false).Where(e => e.Id != "" && !e.Id.StartsWith("anc:") && !e.Id.StartsWith("dev:")))
            Assert.Contains(e.Id, handled);
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
