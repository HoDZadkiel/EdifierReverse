using EdifierTray.Protocol;
using EdifierTray.Service;
using EdifierTray.Transport;
using Xunit;

namespace EdifierTray.Tests;

/// <summary>離線模擬裝置，對應 Python 版 sim.py：回應查詢、可模擬「設定間隔不足就靜默忽略」與主動推送。</summary>
sealed class FakeDevice : ITransport
{
    readonly Session _session;
    readonly TimeSpan _minSetInterval;
    readonly bool _push;
    readonly List<byte> _rx = new();
    long _lastSet = long.MinValue / 2;
    public byte[] Anc = { 16, 1 };
    public int IgnoredSets;
    public readonly List<byte[]> Written = new();
    public bool Connected = true;
    public byte? PowerSave;   // null＝不支援省電模式：不回應（像 ES850NB）；0 關、1 開
    public readonly HashSet<byte> Silent = new();   // 模擬「這個機種不支援該查詢」：不回應

    public event Action<byte[]>? DataReceived;
    public event Action? Closed;

    public FakeDevice(Session session, TimeSpan? minSetInterval = null, bool push = false, byte group = 16)
    {
        _session = session; _minSetInterval = minSetInterval ?? TimeSpan.Zero; _push = push; Anc = new byte[] { group, 1 };
    }

    public Task WriteAsync(byte[] data)
    {
        if (!Connected) throw new IOException("模擬裝置已斷線");
        Written.Add(data);
        var req = PacketCodec.Parse(_session, data, new byte[] { 0xAA });
        if (Silent.Contains(req.Cmd)) return Task.CompletedTask;
        byte[]? reply = req.Cmd switch
        {
            Cmd.BatteryQuery => new byte[] { 80 },
            Cmd.VersionQuery => new byte[] { 1, 0, 2 },
            Cmd.NameQuery => "SIM-DEVICE"u8.ToArray(),
            Cmd.AncQuery => Anc,
            Cmd.AncSet => HandleSet(req.Payload),
            Cmd.SavingModeQuery => PowerSave is byte ps ? new[] { ps } : null,
            Cmd.SavingModeSet => PowerSave is null ? null : (PowerSave = req.Payload[0]) is byte v ? new[] { v } : null,
            _ => null,
        };
        if (reply is null) return Task.CompletedTask;
        var frames = new List<byte[]> { PacketCodec.Build(_session, req.Cmd, reply, 0xBB) };
        if (req.Cmd == Cmd.AncSet && _push) frames.Add(PacketCodec.Build(_session, Cmd.AncQuery, Anc, 0xBB));
        Task.Run(() => { foreach (var f in frames) DataReceived?.Invoke(f); });
        return Task.CompletedTask;
    }

    byte[]? HandleSet(byte[] payload)
    {
        var now = Environment.TickCount64;
        if (now - _lastSet < _minSetInterval.TotalMilliseconds) { IgnoredSets++; return null; }
        _lastSet = now;
        Anc = payload;
        return payload;
    }

    public void Push(params byte[] anc) =>
        DataReceived?.Invoke(PacketCodec.Build(_session, Cmd.AncQuery, anc, 0xBB));

    public void Disconnect() { Connected = false; Closed?.Invoke(); }
    public void Dispose() { }
    public int SetCount => Written.Count(w => w[2] == Cmd.AncSet);
}

public class ServiceTests
{
    static readonly Session S = new(2, 16);
    static readonly TimeSpan[] FastBackoff = { TimeSpan.FromMilliseconds(10) };

    static (List<FakeDevice> devices, Connector connector) Make(TimeSpan? minSet = null, bool push = false, byte group = 16)
    {
        var devices = new List<FakeDevice>();
        Connector c = _ =>
        {
            var d = new FakeDevice(S, minSet, push, group);
            devices.Add(d);
            return Task.FromResult(((ITransport)d, S));
        };
        return (devices, c);
    }

    static async Task<DeviceService> Started(Connector c, TimeSpan? minSetInterval = null, TimeSpan? setTimeout = null)
    {
        var svc = new DeviceService(c, minSetInterval: minSetInterval, setTimeout: setTimeout, backoff: FastBackoff);
        svc.Start();
        await svc.WaitConnectedAsync(TimeSpan.FromSeconds(3));
        return svc;
    }

    [Fact] public async Task Connect_fills_state()
    {
        var (_, c) = Make();
        var svc = await Started(c);
        try
        {
            var s = svc.State;
            Assert.True(s.Connected);
            Assert.Equal(("SIM-DEVICE", "1.0.2", 80), (s.Name, s.Version, s.Battery));
            Assert.Equal((16, 1), (s.Anc!.Group, s.Anc.Mode));
            Assert.Equal("深度降噪", s.AncLabel);
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Back_to_back_sets_are_spaced_so_the_device_never_ignores_them()
    {
        // 實機：兩次設定間隔 ≤ 1.0 秒第二次會被靜默忽略。這裡裝置規則 0.3 秒，節流 0.35 秒
        var (devs, c) = Make(TimeSpan.FromMilliseconds(300), push: true);
        var svc = await Started(c, TimeSpan.FromMilliseconds(350));
        try
        {
            await svc.SetAncAsync(2); await svc.SetAncAsync(3); await svc.SetAncAsync(5);
            Assert.Equal(0, devs[0].IgnoredSets);
            Assert.Equal(3, devs[0].SetCount);
            Assert.Equal(5, svc.State.Anc!.Mode);
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Without_throttle_the_same_sequence_fails_which_shows_the_throttle_matters()
    {
        var (devs, c) = Make(TimeSpan.FromMilliseconds(300));
        var svc = await Started(c, TimeSpan.Zero, TimeSpan.FromMilliseconds(80));
        try
        {
            await svc.SetAncAsync(2);
            var ex = await Assert.ThrowsAsync<ServiceException>(() => svc.SetAncAsync(3));
            Assert.Contains("沒有回應", ex.Message);
            Assert.Equal(2, devs[0].IgnoredSets);  // 第一次與重送都被忽略
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task A_set_the_device_ignored_is_resent_once_and_then_succeeds()
    {
        var (devs, c) = Make(TimeSpan.FromMilliseconds(250));
        var svc = await Started(c, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(300));
        try
        {
            await svc.SetAncAsync(2);
            var now = await svc.SetAncAsync(3);  // 太快 -> 被忽略 -> 逾時 -> 已過 250ms -> 重送成功
            Assert.Equal(1, devs[0].IgnoredSets);
            Assert.Equal(3, now.Mode);
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Unverified_group_is_status_only_and_nothing_is_sent()
    {
        var (devs, c) = Make(group: 18);
        var svc = await Started(c);
        try
        {
            var ex = await Assert.ThrowsAsync<ServiceException>(() => svc.SetAncAsync(2));
            Assert.Contains("尚未驗證", ex.Message);
            Assert.Equal(0, devs[0].SetCount);
            Assert.False(svc.State.AncSupported);
            Assert.Contains("未驗證", svc.State.AncLabel);
        }
        finally { await svc.StopAsync(); }
    }

    [Theory]
    [InlineData(9, null, "沒有 mode")] [InlineData(2, 5, "只有可調增益")]
    [InlineData(4, 0, "必須在")] [InlineData(4, 13, "必須在")]
    public async Task Invalid_requests_are_rejected_before_sending(int mode, int? db, string message)
    {
        var (devs, c) = Make();
        var svc = await Started(c);
        try
        {
            var ex = await Assert.ThrowsAsync<ServiceException>(() => svc.SetAncAsync(mode, db));
            Assert.Contains(message, ex.Message);
            Assert.Equal(0, devs[0].SetCount);
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Ambient_mode_sends_db_as_third_byte()
    {
        var (devs, c) = Make();
        var svc = await Started(c, TimeSpan.Zero);
        try
        {
            await svc.SetAncAsync(4, 9);
            Assert.Equal(new byte[] { 16, 4, 9 }, devs[0].Anc);
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Pushed_anc_change_from_the_device_updates_the_state()
    {
        // 例如使用者按耳機按鍵切換 ANC：沒有對應請求，只有主動推送
        var (devs, c) = Make();
        var svc = await Started(c);
        try
        {
            devs[0].Push(16, 3);
            await Task.Delay(100);
            Assert.Equal(3, svc.State.Anc!.Mode);
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Disconnect_is_detected_and_the_service_reconnects()
    {
        var (devs, c) = Make();
        var seen = new List<bool>();
        var svc = new DeviceService(c, backoff: FastBackoff);
        svc.StateChanged += s => { lock (seen) seen.Add(s.Connected); };
        svc.Start();
        await svc.WaitConnectedAsync(TimeSpan.FromSeconds(3));
        try
        {
            devs[0].Disconnect();
            for (int i = 0; i < 100 && !(devs.Count == 2 && svc.State.Connected); i++) await Task.Delay(30);
            Assert.Equal(2, devs.Count);
            Assert.True(svc.State.Connected);
            lock (seen) Assert.Contains(false, seen);  // 中間確實有過未連線狀態
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Connect_failure_is_reported_and_retried()
    {
        int calls = 0;
        Connector c = _ =>
        {
            if (++calls < 3) throw new IOException("掃不到裝置");
            return Task.FromResult(((ITransport)new FakeDevice(S), S));
        };
        var errors = new List<string>();
        var svc = new DeviceService(c, backoff: FastBackoff);
        svc.StateChanged += s => { lock (errors) errors.Add(s.Error); };
        svc.Start();
        try
        {
            await svc.WaitConnectedAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(3, calls);
            lock (errors) { Assert.Contains(errors, e => e.Contains("掃不到裝置")); Assert.Equal("", errors[^1]); }
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Device_available_notice_cuts_the_backoff_short()
    {
        int calls = 0;
        bool deviceUp = false;
        Connector c = _ =>
        {
            calls++;
            if (!deviceUp) throw new IOException("裝置還沒開");
            return Task.FromResult(((ITransport)new FakeDevice(S), S));
        };
        var svc = new DeviceService(c, backoff: new[] { TimeSpan.FromSeconds(30) });  // 不通知的話要等 30 秒
        svc.Start();
        try
        {
            for (int i = 0; i < 100 && calls < 1; i++) await Task.Delay(10);
            await Task.Delay(100);  // 讓服務進入退避等待
            deviceUp = true;
            svc.NotifyDeviceAvailable();
            await svc.WaitConnectedAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(2, calls);
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Device_available_notice_does_not_drop_a_healthy_connection()
    {
        var (devs, c) = Make();
        var svc = new DeviceService(c, backoff: FastBackoff);
        svc.Start();
        await svc.WaitConnectedAsync(TimeSpan.FromSeconds(3));
        try
        {
            svc.NotifyDeviceAvailable();  // 已連線：必須忽略，否則會把好的連線斷掉重連
            await Task.Delay(300);
            Assert.Single(devs);
            Assert.True(svc.State.Connected);
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Set_when_not_connected_is_an_error()
    {
        var (_, c) = Make();
        var svc = new DeviceService(c);
        var ex = await Assert.ThrowsAsync<ServiceException>(() => svc.SetAncAsync(2));
        Assert.Contains("尚未連上", ex.Message);
    }
}

public class VolumeTests
{
    sealed class Fake : IEndpoint { public float Scalar { get; set; } = 0.5f; public bool Mute { get; set; } }
    internal sealed class FakeEp : IEndpoint { public float Scalar { get; set; } = 0.5f; public bool Mute { get; set; } }
    sealed class Stale : IEndpoint
    {
        public float Scalar { get => throw new InvalidOperationException("COM 端點已失效"); set => throw new InvalidOperationException(); }
        public bool Mute { get; set; }
    }

    [Fact] public void Get_returns_percent() => Assert.Equal(50, new VolumeControl("X", _ => new Fake { Scalar = 0.504f }).Get());

    [Theory] [InlineData(30, 30)] [InlineData(0, 0)] [InlineData(100, 100)] [InlineData(-5, 0)] [InlineData(250, 100)]
    public void Set_clamps_to_0_100_and_writes_the_fraction(int asked, int applied)
    {
        var ep = new Fake();
        Assert.Equal(applied, new VolumeControl("X", _ => ep).Set(asked));
        Assert.Equal(applied / 100f, ep.Scalar, 3);
    }

    [Fact] public void Endpoint_is_cached_across_calls()
    {
        int opened = 0;
        var v = new VolumeControl("X", _ => { opened++; return new Fake(); });
        v.Get(); v.Set(10); v.Muted(); v.SetMute(true);
        Assert.Equal(1, opened);  // 列舉裝置很慢，拖曳音量時不能每次重找
    }

    [Fact] public void A_stale_endpoint_is_reopened_once_then_retried()
    {
        var queue = new Queue<IEndpoint>(new IEndpoint[] { new Stale(), new Fake { Scalar = 0.7f } });
        Assert.Equal(70, new VolumeControl("X", _ => queue.Dequeue()).Get());
    }

    [Fact] public void Persistent_failure_becomes_a_service_exception()
    {
        var ex = Assert.Throws<ServiceException>(() => new VolumeControl("X", _ => new Stale()).Get());
        Assert.Contains("無法存取", ex.Message);
    }

    [Fact] public void Falls_back_to_the_default_output_when_the_headphone_endpoint_is_missing()
    {
        var fallback = new Fake { Scalar = 0.3f };
        var v = new VolumeControl("X", _ => throw new ServiceException("e.vol_endpoint", new { name = "X" }), () => fallback);
        Assert.Equal(30, v.Get());
        Assert.Equal(60, v.Set(60));
        Assert.Equal(0.6f, fallback.Scalar, 3);  // 耳機沒連上時，音量按鈕調的是目前使用中的輸出
    }

    [Fact] public void Reset_picks_the_headphone_endpoint_again_once_it_appears()
    {
        var headphone = new Fake { Scalar = 0.8f };
        bool present = false;
        var v = new VolumeControl("X", _ => present ? headphone : throw new ServiceException("e.vol_endpoint", new { name = "X" }),
                                  () => new Fake { Scalar = 0.1f });
        Assert.Equal(10, v.Get());
        present = true;
        Assert.Equal(10, v.Get());  // 端點有快取，沒 Reset 就不會換
        v.Reset();
        Assert.Equal(80, v.Get());
    }

    [Fact] public void Without_a_fallback_a_missing_endpoint_still_fails()
    {
        var v = new VolumeControl("X", _ => throw new ServiceException("e.vol_endpoint", new { name = "X" }));
        Assert.Throws<ServiceException>(() => v.Get());
    }

    [Fact] public void Mute_roundtrip()
    {
        var ep = new Fake();
        var v = new VolumeControl("X", _ => ep);
        v.SetMute(true); Assert.True(v.Muted());
        v.SetMute(false); Assert.False(v.Muted());
    }
}

public class KeepAwakeTests
{
    sealed class Probe : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    [Fact] public void Streams_follow_the_wanted_devices_and_stop_when_they_are_no_longer_wanted()
    {
        var made = new Dictionary<string, Probe>();
        using var ka = new KeepAwake(n => made[n] = new Probe());
        ka.Reconcile(new[] { "MP330", "ES850NB" });
        Assert.Equal(new[] { "ES850NB", "MP330" }, ka.Running.OrderBy(x => x));
        ka.Reconcile(new[] { "MP330" });   // 取消勾選 ES850NB：它的串流要停掉，MP330 的不能重開
        Assert.True(made["ES850NB"].Disposed);
        Assert.False(made["MP330"].Disposed);
        Assert.Equal(new[] { "MP330" }, ka.Running);
    }

    [Fact] public void A_missing_endpoint_is_retried_on_the_next_reconcile_and_failures_do_not_throw()
    {
        int tries = 0;
        using var ka = new KeepAwake(n => ++tries switch { 1 => null, 2 => throw new IOException("busy"), _ => new Probe() });
        ka.Reconcile(new[] { "MP330" }); Assert.Empty(ka.Running);   // 裝置還沒連上：沒有端點
        ka.Reconcile(new[] { "MP330" }); Assert.Empty(ka.Running);   // 端點被占用：吞掉例外
        ka.Reconcile(new[] { "MP330" }); Assert.Equal(new[] { "MP330" }, ka.Running);
    }

    [Fact] public void Endpoint_name_part_drops_the_brand_prefix_and_falls_back_to_the_model()
    {
        Assert.Equal("MP330", KeepAwake.NamePart(new KnownDevice { Name = "EDIFIER MP330", Model = "MP330" }));
        Assert.Equal("ES850NB", KeepAwake.NamePart(new KnownDevice { Model = "ES850NB" }));
        Assert.Equal("", KeepAwake.NamePart(new KnownDevice()));   // 沒名稱也不知機種：不猜
    }

    [Fact] public void Tone_level_stays_far_below_audibility()
    {
        Assert.InRange(20 * Math.Log10(KeepAwake.Amplitude), -60, -40);   // dBFS
        Assert.True(KeepAwake.Frequency < 40);
    }
}

public class PowerSaveTests
{
    static readonly Session S = new(2, 0);   // MP330：版本 2、不加密

    static (List<FakeDevice> devices, DeviceService svc) Start(byte? powerSave)
    {
        var devices = new List<FakeDevice>();
        Connector c = _ =>
        {
            var d = new FakeDevice(S) { PowerSave = powerSave };
            devices.Add(d);
            return Task.FromResult(((ITransport)d, S));
        };
        var svc = new DeviceService(c, backoff: new[] { TimeSpan.FromMilliseconds(10) });
        svc.Start();
        return (devices, svc);
    }

    static async Task<bool?> WaitPowerSave(DeviceService svc, bool? expected)
    {
        for (int i = 0; i < 100 && svc.State.PowerSave != expected; i++) await Task.Delay(30);
        return svc.State.PowerSave;
    }

    [Fact] public async Task A_device_that_answers_reports_whether_power_saving_is_on_without_blocking_the_connection()
    {
        var (_, svc) = Start(1);
        try
        {
            await svc.WaitConnectedAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(true, await WaitPowerSave(svc, true));   // 連上後在背景查到：目前是省電模式
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task A_device_that_does_not_answer_has_no_power_saving_setting_and_stays_connected()
    {
        var (_, svc) = Start(null);   // 像 ES850NB
        try
        {
            await svc.WaitConnectedAsync(TimeSpan.FromSeconds(3));
            await Task.Delay(1900);   // 超過背景查詢的 1.5 秒逾時
            Assert.Null(svc.State.PowerSave);
            Assert.True(svc.State.Connected);
            await Assert.ThrowsAsync<ServiceException>(() => svc.SetPowerSaveAsync(false));   // 不支援就不送設定
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Check_rereads_the_device_and_picks_up_a_change_made_elsewhere()
    {
        var (devices, svc) = Start(1);
        try
        {
            await svc.WaitConnectedAsync(TimeSpan.FromSeconds(3));
            await WaitPowerSave(svc, true);
            devices[0].PowerSave = 0;   // 例如在手機 App 或喇叭上改了
            Assert.False(await svc.CheckPowerSaveAsync());
            Assert.Equal(false, svc.State.PowerSave);
            Assert.DoesNotContain(devices[0].Written, w => w[2] == Cmd.SavingModeSet);   // 檢查是唯讀：沒有送出任何設定
        }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Turning_power_saving_off_sends_the_set_command_and_confirms_by_reading_back()
    {
        var (devices, svc) = Start(1);
        try
        {
            await svc.WaitConnectedAsync(TimeSpan.FromSeconds(3));
            await WaitPowerSave(svc, true);
            Assert.False(await svc.SetPowerSaveAsync(false));
            Assert.Equal((byte)0, devices[0].PowerSave);
            var sets = devices[0].Written.Where(w => w[2] == Cmd.SavingModeSet).ToList();
            Assert.Single(sets);
            Assert.Equal(new byte[] { 0 }, PacketCodec.Parse(S, sets[0], new byte[] { 0xAA }).Payload);
            Assert.Equal(false, svc.State.PowerSave);
            Assert.True(await svc.SetPowerSaveAsync(true));   // 還原
            Assert.Equal((byte)1, devices[0].PowerSave);
        }
        finally { await svc.StopAsync(); }
    }
}
