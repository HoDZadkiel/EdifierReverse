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
            Assert.True(rows["anc:3"].IsEnabled);
            rows["mute"].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(new[] { "mute" }, invoked);
            Assert.False(menu.IsVisible);              // 點選後自動收起

            menu.Populate(TrayMenuModel.Build(new DeviceState(), null, false, false), null);
            Assert.False(FindButtons(menu.Content).First(b => (string)b.Tag == "anc:2").IsEnabled);  // 未連線：停用
            menu.Close();
        });
    }
}
