using EdifierTray.Service;
using EdifierTray.Ui;
using Xunit;

namespace EdifierTray.Tests;

/// <summary>冷卻期：使用者連按切換鍵時，介面要暫停切換，否則排隊的設定會卡住（實機回報）。</summary>
public class CooldownTests
{
    static readonly Protocol.Session S = new(2, 16);
    static readonly TimeSpan[] FastBackoff = { TimeSpan.FromMilliseconds(10) };

    static (List<FakeDevice> devs, Connector c) Make(TimeSpan? minSet = null, byte group = 16)
    {
        var devs = new List<FakeDevice>();
        Connector c = _ => { var d = new FakeDevice(S, minSet, false, group); devs.Add(d); return Task.FromResult(((Transport.ITransport)d, S)); };
        return (devs, c);
    }

    static async Task<DeviceService> Started(Connector c, TimeSpan? minSetInterval = null, TimeSpan? setTimeout = null)
    {
        var svc = new DeviceService(c, minSetInterval: minSetInterval, setTimeout: setTimeout, backoff: FastBackoff);
        svc.Start();
        await svc.WaitConnectedAsync(TimeSpan.FromSeconds(3));
        return svc;
    }

    [Fact] public async Task Cooldown_starts_with_the_first_set_and_ends_one_interval_after_the_last_without_flicker()
    {
        var (_, c) = Make();
        var svc = await Started(c, TimeSpan.FromMilliseconds(200));
        var flags = new List<bool>();
        svc.StateChanged += s => { lock (flags) flags.Add(s.Cooling); };
        try
        {
            Assert.False(svc.State.Cooling);
            var first = svc.SetAncAsync(2);
            await Task.Delay(20);
            Assert.True(svc.State.Cooling);                   // 送出後立刻進入冷卻
            var second = svc.SetAncAsync(3);                  // 冷卻中硬塞第二個：排隊，冷卻延長
            await Task.WhenAll(first, second);
            Assert.True(svc.State.Cooling);                   // 最後一個剛完成，還在冷卻
            await Task.Delay(500);
            Assert.False(svc.State.Cooling);
            lock (flags)
            {
                var transitions = flags.Where((f, i) => i == 0 || f != flags[i - 1]).ToList();
                Assert.Equal(new[] { true, false }, transitions);  // 進入一次、最後才解除
            }
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task A_rejected_request_does_not_start_a_cooldown()
    {
        var (_, c) = Make(group: 18);
        var svc = await Started(c);
        try
        {
            await Assert.ThrowsAsync<ServiceException>(() => svc.SetAncAsync(2));
            Assert.False(svc.State.Cooling);
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Cooldown_also_ends_after_a_failed_set()
    {
        var (_, c) = Make(TimeSpan.FromSeconds(5));          // 裝置一律忽略第二次：逾時失敗
        var svc = await Started(c, TimeSpan.Zero, TimeSpan.FromMilliseconds(60));
        try
        {
            await svc.SetAncAsync(2);
            await Assert.ThrowsAsync<ServiceException>(() => svc.SetAncAsync(3));
            await Task.Delay(150);
            Assert.False(svc.State.Cooling);
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public void Menu_disables_anc_items_during_cooldown_but_keeps_the_current_mode_marked()
    {
        var s = new DeviceState(true, "EDIFIER ES850NB", "1.0.1", 16, new AncState(16, 2), Cooling: true);
        var m = MenuTestUtil.Flat(TrayMenuModel.Build(s, 50, false, false));
        Assert.All(m.Where(e => e.Id.StartsWith("anc:")), e => Assert.False(e.Enabled));
        Assert.True(m.Single(e => e.Id == "anc:2").Checked);
        Assert.True(m.Single(e => e.Id == "mute").Enabled);  // 音量不受冷卻影響
    }
}
