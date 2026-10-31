using EdifierTray.Service;
using EdifierTray.Ui;
using Xunit;

namespace EdifierTray.Tests;

/// <summary>
/// 面板的視窗行為。WPF 視窗必須在 STA 執行緒，且每個行程只能有一個 Application，所以全部放在同一個測試裡依序跑。
/// 回歸：第一次點托盤圖示（視窗從未 Show 過就直接 Toggle）曾讓程式閃退，見 DEVLOG 2026-10-27。
/// </summary>
public class FlyoutTests
{
    static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception e) { error = e; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(30)), "面板測試逾時");
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    static IEnumerable<System.Windows.Controls.Button> FindButtons(object? root)
    {
        if (root is System.Windows.Controls.Button b) { yield return b; yield break; }
        if (root is System.Windows.Controls.Decorator d) foreach (var x in FindButtons(d.Child)) yield return x;
        if (root is System.Windows.Controls.Panel p) foreach (var c in p.Children) foreach (var x in FindButtons(c)) yield return x;
    }

    [Fact]
    public void First_toggle_on_a_never_shown_window_works_and_a_click_right_after_deactivate_does_not_reopen()
    {
        RunSta(() =>
        {
            var app = new App();
            app.InitializeComponent();  // 載入 App.xaml 的樣式
            Theme.DarkOverride = true;
            Theme.Apply(true);

            var f = new FlyoutWindow();
            f.ApplyState(new DeviceState(true, "EDIFIER ES850NB", "1.0.1", 16, new AncState(16, 4, 8)), 50, false);
            // 沒有降噪的機種（MP330）：整個降噪區塊收起，有降噪的再顯示
            var anc = (System.Windows.FrameworkElement)f.FindName("AncBox");
            Assert.Equal(System.Windows.Visibility.Visible, anc.Visibility);
            f.ApplyState(new DeviceState(true, "EDIFIER MP330", "1.0.1", 100, null, HasAnc: false), 50, false);
            Assert.Equal(System.Windows.Visibility.Collapsed, anc.Visibility);
            f.ApplyState(new DeviceState(true, "EDIFIER ES850NB", "1.0.1", 16, new AncState(16, 4, 8)), 50, false);
            Assert.Equal(System.Windows.Visibility.Visible, anc.Visibility);

            // 左下角顯示版本號，與 pyproject.toml 的 version 同一份
            Assert.Matches(@"^v\d+\.\d+\.\d+$", f.VersionLabel);
            Assert.Equal("v" + FlyoutWindow.AppVersion, f.VersionLabel);
            Assert.StartsWith("https://github.com/", FlyoutWindow.RepoUrl);

            // 冷卻期：磚塊與強度條停用，但目前模式（環境音）仍標示選中
            var cool = new DeviceState(true, "EDIFIER ES850NB", "1.0.1", 16, new AncState(16, 4, 8), Cooling: true);
            f.ApplyState(cool, 50, false);
            Assert.Equal((false, true), f.TileState(4));
            Assert.Equal((false, false), f.TileState(2));
            Assert.False(f.AmbientEnabled);
            f.ApplyState(cool with { Cooling = false }, 50, false);   // 冷卻結束：恢復
            Assert.Equal((true, true), f.TileState(4));
            Assert.Equal((true, false), f.TileState(2));
            Assert.True(f.AmbientEnabled);

            f.Toggle();                       // 第一次：視窗從未顯示過
            Assert.True(f.IsVisible);
            f.Toggle();                       // 再點一次：收起
            Assert.False(f.IsVisible);
            f.Toggle();                       // 緊接著的點擊視為「點圖示造成的收起」，不重開
            Assert.False(f.IsVisible);

            Thread.Sleep(300);
            f.Toggle();                       // 過了一小段時間才點：正常重新打開
            Assert.True(f.IsVisible);

            // 元件操作回報正確的值
            var asked = new List<int>();
            f.AncRequested += asked.Add;
            f.SimulateTile(3);
            Assert.Equal(new[] { 3 }, asked);

            f.Close();

            // ---- 右鍵選單（同一個 Application 內接著測） ----
            var menu = new TrayMenuWindow();
            var state = new DeviceState(true, "EDIFIER ES850NB", "1.0.1", 16, new AncState(16, 2));
            menu.Populate(TrayMenuModel.Build(state, 50, false, false), null);
            menu.ToggleAt(1200, 1380);                 // 第一次就直接開（視窗從未顯示過）
            Assert.True(menu.IsVisible);
            menu.ToggleAt(1200, 1380);
            Assert.False(menu.IsVisible);

            var invoked = new List<string>();
            menu.ItemInvoked += invoked.Add;
            Thread.Sleep(300);                         // 越過「剛收起不重開」的保護時間
            menu.ShowAt(1200, 1380);
            var rows = FindButtons(menu.Content).ToDictionary(b => (string)b.Tag);
            // 降噪模式在「降噪模式」大項的子選單裡（滑鼠移上去才彈出），主選單本身只有大項標題與音量等常用項目
            Assert.DoesNotContain("anc:3", rows.Keys);
            var ancRows = FindButtons(menu.BuildSubmenuContent(menu.Entries.Single(e => e.Id == "sec:anc"))).ToDictionary(b => (string)b.Tag);
            Assert.True(ancRows["anc:3"].IsEnabled);
            rows["mute"].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(new[] { "mute" }, invoked);
            Assert.False(menu.IsVisible);              // 點選後自動收起

            // 子選單：滑鼠移到大項上彈出、移到別列收起、關選單一起關（計時器用 FlushHoverTimers 立即到期）
            menu.Populate(TrayMenuModel.Build(state, 50, false, false), null);
            menu.ShowAt(1200, 1380);
            Assert.Equal("", menu.OpenSubmenu);
            menu.HoverSection("sec:anc");
            Assert.Equal("", menu.OpenSubmenu);               // 稍微延遲才彈出（斜著掃過不閃）
            menu.FlushHoverTimers();
            Assert.Equal("sec:anc", menu.OpenSubmenu);        // 移上去：子選單彈出
            menu.HoverSection("sec:devices");
            menu.FlushHoverTimers();
            Assert.Equal("sec:devices", menu.OpenSubmenu);    // 移到另一個大項：換成它的子選單
            menu.HoverOtherRow();
            menu.FlushHoverTimers();
            Assert.Equal("", menu.OpenSubmenu);               // 移到別的列：收起
            menu.HoverSection("sec:anc");
            menu.FlushHoverTimers();
            Assert.Equal("sec:anc", menu.OpenSubmenu);
            menu.HideMenu();
            Assert.Equal("", menu.OpenSubmenu);               // 主選單收起時子選單一起關

            menu.Populate(TrayMenuModel.Build(new DeviceState(), null, false, false), null);
            Assert.False(FindButtons(menu.BuildSubmenuContent(menu.Entries.Single(e => e.Id == "sec:anc"))).First(b => (string)b.Tag == "anc:2").IsEnabled);  // 未連線：停用
            menu.Close();

            // ---- OSD（同一個 Application 內接著測） ----
            var osd = new OsdWindow();
            Assert.False(osd.IsShowing);
            osd.ShowOsd("電量偏低：20%");
            Assert.True(osd.IsShowing);
            Assert.Equal("電量偏低：20%", osd.Text);
            var area = System.Windows.SystemParameters.WorkArea;
            Assert.InRange(osd.Left + osd.ActualWidth / 2, area.Left + area.Width / 2 - 1, area.Left + area.Width / 2 + 1);  // 水平置中
            Assert.True(osd.Top + osd.ActualHeight < area.Bottom);                                                          // 在工作區底緣之上
            osd.ShowOsd("音量 60%");
            Assert.Equal("音量 60%", osd.Text);                  // 連續顯示是換字，不是疊視窗
            osd.HideOsd();
            Assert.False(osd.IsShowing);
            osd.Close();
        });
    }
}
