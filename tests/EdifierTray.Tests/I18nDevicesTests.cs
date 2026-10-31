using System.Text.RegularExpressions;
using EdifierTray.Protocol;
using EdifierTray.Service;
using EdifierTray.Ui;
using Xunit;

// 介面語言是全域狀態（Strings.Language），會改語言的測試不能和其他測試同時跑
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace EdifierTray.Tests;

public class I18nTests : IDisposable
{
    public void Dispose() => Strings.SetLanguage(Strings.Default);

    [Fact] public void Every_language_has_exactly_the_same_keys()
    {
        var baseKeys = Strings.TableFor("zh-TW").Keys.ToHashSet();
        foreach (var code in Strings.Codes)
            Assert.True(baseKeys.SetEquals(Strings.TableFor(code).Keys), $"{code} 的鍵與繁中不同");
    }

    [Fact] public void Placeholders_match_across_languages()
    {
        // 譯文漏掉或寫錯 {參數} 會在畫面上出現亂碼；與 Python 版 test_placeholders_match_across_languages 同一份資料
        static string[] Ph(string t) => Regex.Matches(t, @"\{(\w+)\}").Select(m => m.Groups[1].Value).OrderBy(x => x).ToArray();
        foreach (var (key, text) in Strings.TableFor("zh-TW"))
            foreach (var code in Strings.Codes)
                Assert.Equal(Ph(text), Ph(Strings.TableFor(code)[key]));
    }

    [Fact] public void T_formats_falls_back_and_keeps_missing_arguments()
    {
        Strings.SetLanguage("en");
        Assert.Equal("Battery 16%　·　X", Strings.T("m.header_detail", new { b = 16, mode = "X" }));
        Assert.Equal("no.such.key", Strings.T("no.such.key"));            // 缺字不讓畫面出錯
        Assert.Equal("zh-TW", Strings.SetLanguage("fr"));                  // 未知語言退回預設
        Assert.Equal("靜音", Strings.T("m.mute"));
        Assert.Equal("音量 +{n}%", Strings.T("m.vol_up"));                  // 缺參數時保留原文而不是丟例外
    }

    [Fact] public void Anc_labels_and_service_errors_follow_the_language()
    {
        var st = new DeviceState(true, "X", "1", 80, new AncState(16, 2));
        Assert.Equal("舒適降噪", st.AncLabel);
        Strings.SetLanguage("ja");
        Assert.Equal("コンフォートノイズキャンセリング", st.AncLabel);
        Assert.Contains("未検証", new DeviceState(true, "X", "1", 80, new AncState(18, 1)).AncLabel);
        Strings.SetLanguage("zh-CN");
        Assert.Contains("尚未连接", new ServiceException("e.not_connected").Message);
        Strings.SetLanguage("en");
        Assert.Equal("Level must be between 1 and 12", new ServiceException("e.strength_range", new { lo = 1, hi = 12 }).Message);
    }

    [Fact] public void Font_stack_names_the_windows_11_font_of_each_language_after_segoe_ui_variable()
    {
        Assert.StartsWith("Segoe UI Variable Text, Microsoft JhengHei UI", Strings.FontFamilyFor("zh-TW"));
        Assert.Contains("Microsoft YaHei UI", Strings.FontFamilyFor("zh-CN"));
        Assert.Contains("Yu Gothic UI", Strings.FontFamilyFor("ja"));
        Assert.DoesNotContain("MingLiU", Strings.FontFamilyFor("zh-TW"));   // 不能落到有襯線的細明體
    }

    [Fact] public void Short_names_exist_for_every_language() =>
        Assert.All(Strings.Codes, c => Assert.True(Strings.Short.ContainsKey(c)));
}

public class SettingsTests
{
    [Fact] public void Roundtrip_missing_corrupt_and_unknown_values()
    {
        var dir = Path.Combine(Path.GetTempPath(), "EdifierTrayTest_" + Guid.NewGuid().ToString("N"));
        var p = Path.Combine(dir, "settings.json");
        try
        {
            Assert.Equal("system", Settings.Load(p).Theme);                  // 沒有檔案
            var s = new Settings { Theme = "dark", Language = "ja", Selected = "AA:BB:CC:11:22:33" };
            s.Remember(new KnownDevice { A2dp = "AA:BB:CC:11:22:33", Ble = "AA:BB:CC:11:22:34", Version = 2, Encryption = 16, Name = "EDIFIER ES850NB" });
            s.Save(p);
            var back = Settings.Load(p);
            Assert.Equal(("dark", "ja", "AA:BB:CC:11:22:33"), (back.Theme, back.Language, back.Selected));
            Assert.Equal("EDIFIER ES850NB", back.Devices.Single().Name);
            File.WriteAllText(p, "{壞掉");
            Assert.Empty(Settings.Load(p).Devices);                 // 壞檔不能讓程式起不來
            File.WriteAllText(p, "{\"theme\":\"neon\",\"devices\":[{\"a2dp\":\"AA\"},{\"a2dp\":\"\"}]}");
            var odd = Settings.Load(p);
            Assert.Equal("system", odd.Theme);
            Assert.Equal(new[] { "AA" }, odd.Devices.Select(d => d.A2dp));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact] public void Remember_keeps_a_known_name_when_the_scan_has_none()
    {
        var s = new Settings();
        s.Remember(new KnownDevice { A2dp = "AA:AA:AA:AA:AA:01", Name = "EDIFIER ES850NB" });
        s.Remember(new KnownDevice { A2dp = "aa:aa:aa:aa:aa:01", Ble = "BLE1" });   // 位址不分大小寫
        Assert.Single(s.Devices);
        Assert.Equal(("EDIFIER ES850NB", "BLE1"), (s.Devices[0].Name, s.Devices[0].Ble));
    }

    [Fact] public void Selected_device_missing_from_the_list_falls_back_to_the_first()
    {
        var s = new Settings { Selected = "DE:AD:BE:EF:00:00" };
        s.Devices.Add(new KnownDevice { A2dp = "AA:AA:AA:AA:AA:01" });
        Assert.EndsWith("01", s.CurrentDevice()!.A2dp);
    }
}

public class CatalogTests
{
    const string Es850Uuid = "00002101-0000-1000-8000-00805f9b34fb";
    static readonly IReadOnlyDictionary<ushort, byte[]> RealMfr =
        new Dictionary<ushort, byte[]> { [0x07E0] = Convert.FromHexString("aabbcc1122330210") };   // 實機掃描 ES850NB 的廣播

    [Fact] public void Catalog_is_well_formed_and_search_uuids_are_unique()
    {
        Assert.True(Catalog.Models.Count >= 100);
        Assert.Equal(Catalog.Models.Count, Catalog.Models.Select(m => m.Search).Distinct().Count());
        Assert.Equal(Catalog.Models.Count, Catalog.Models.Select(m => m.Model).Distinct().Count());
    }

    [Fact] public void Known_models_match_what_was_verified_on_the_real_devices()
    {
        Assert.True(Catalog.ByName["ES850NB"].Spp);                       // 實機驗證：走 SPP
        Assert.Equal(Es850Uuid, Catalog.ByName["ES850NB"].Search);
        Assert.False(Catalog.ByName["MP330"].Spp);                        // 靜態分析：走 BLE
        Assert.True(Catalog.UsesSpp("NoSuchModel"));                      // 不在目錄的先當作有 SPP
    }

    [Fact] public void Identify_by_search_uuid_names_the_model() =>
        Assert.Equal("ES850NB", Catalog.Identify(new[] { "0000180F-0000-1000-8000-00805F9B34FB", Es850Uuid.ToUpperInvariant() }, RealMfr)!.Model);

    [Fact] public void Identify_by_company_id_alone_gives_a_generic_edifier_device()
    {
        var m = Catalog.Identify(new[] { "0000fff0-0000-1000-8000-00805f9b34fb" }, RealMfr)!;
        Assert.Equal((Catalog.GenericModel, true), (m.Model, m.Spp));
    }

    [Fact] public void Identify_rejects_non_edifier_advertisements()
    {
        Assert.Null(Catalog.Identify(Array.Empty<string>(), new Dictionary<ushort, byte[]>()));
        Assert.Null(Catalog.Identify(new[] { "0000180f-0000-1000-8000-00805f9b34fb" }, new Dictionary<ushort, byte[]> { [0x004C] = new byte[8] }));
        Assert.Null(Catalog.Identify(Array.Empty<string>(), new Dictionary<ushort, byte[]> { [0x07E0] = new byte[5] }));  // 資料太短
    }

    [Fact] public void IdentifyAdvert_extracts_address_session_and_model()
    {
        var f = Discovery.IdentifyAdvert(new[] { Es850Uuid }, RealMfr, "AA:BB:CC:11:22:34", -60)!;
        Assert.Equal(("AA:BB:CC:11:22:33", "ES850NB", new Session(2, 16)), (f.A2dpAddress, f.Model, f.Session));
        Assert.Null(Discovery.IdentifyAdvert(new[] { Es850Uuid }, new Dictionary<ushort, byte[]>()));   // 有 UUID 但沒廠商資料：取不到位址
    }
}

public class DirectoryTests
{
    static readonly (string, string)[] Two = { ("AA:AA:AA:AA:AA:01", "B1"), ("AA:AA:AA:AA:AA:02", "B2") };

    static (DeviceDirectory dir, List<int> saves) Make(params (string A2dp, string Ble)[] found) => Make(null, found);

    /// <param name="unpaired">視為沒配對的位址；其餘都視為已配對。</param>
    static (DeviceDirectory dir, List<int> saves) Make(HashSet<string>? unpaired, params (string A2dp, string Ble)[] found) =>
        Make(unpaired, null, found);

    /// <param name="paired">Windows 已配對的 (位址, 名稱)，預設空（不碰真實系統）。</param>
    static (DeviceDirectory dir, List<int> saves) Make(HashSet<string>? unpaired, List<(string A2dp, string Name)>? paired, params (string A2dp, string Ble)[] found)
    {
        var saves = new List<int>();
        var dir = new DeviceDirectory(new Settings(), _ => saves.Add(1), (t, ct) => Task.FromResult(found.Select(f =>
            new Discovery.Found(f.A2dp, new Session(2, 16), "ES850NB", f.Ble, -60)).ToList()),
            a => Task.FromResult(!(unpaired?.Contains(a) ?? false)),
            () => Task.FromResult(paired ?? new List<(string A2dp, string Name)>()));
        return (dir, saves);
    }

    [Fact] public async Task Unpaired_devices_found_nearby_are_hidden()
    {
        var (d, _) = Make(new HashSet<string> { Two[1].Item1 }, Two);
        var seen = await d.ScanAsync(TimeSpan.Zero);
        Assert.Equal(new[] { Two[0].Item1 }, seen.Select(x => x.A2dp));   // 沒配對的連不上，不列出
        Assert.Equal(new[] { Two[0].Item1 }, d.Devices.Select(x => x.A2dp));
    }

    [Fact] public async Task A_device_that_is_no_longer_paired_is_dropped_and_the_selection_cleared()
    {
        var unpaired = new HashSet<string>();
        var (d, _) = Make(unpaired, Two);
        await d.ScanAsync(TimeSpan.Zero);
        d.Select(Two[1].Item1);
        unpaired.Add(Two[1].Item1);                                        // 使用者在系統裡移除了配對
        await d.PruneUnpairedAsync();
        Assert.Equal(new[] { Two[0].Item1 }, d.Devices.Select(x => x.A2dp));
        Assert.Equal(Two[0].Item1, d.Current!.A2dp);                       // 選擇回到仍存在的第一台
    }

    [Fact] public void Fallback_name_uses_the_known_device_not_a_hardcoded_model()
    {
        Assert.Equal("Edifier", DeviceDirectory.FallbackName(null));
        Assert.Equal("MP330", DeviceDirectory.FallbackName(new KnownDevice { A2dp = "A", Model = "MP330" }));
        Assert.Equal("EDIFIER X", DeviceDirectory.FallbackName(new KnownDevice { A2dp = "A", Model = "MP330", Name = "EDIFIER X" }));
    }

    [Fact] public async Task Scan_adds_all_devices_and_does_not_duplicate_on_rescan()
    {
        var (d, saves) = Make(Two);
        Assert.Equal(2, (await d.ScanAsync(TimeSpan.Zero)).Count);
        await d.ScanAsync(TimeSpan.Zero);
        Assert.Equal(2, d.Devices.Count);
        Assert.NotEmpty(saves);
    }

    [Fact] public async Task Selection_defaults_to_the_first_device_and_follows_select()
    {
        var (d, _) = Make(Two);
        Assert.Null(d.Current);
        await d.ScanAsync(TimeSpan.Zero);
        Assert.EndsWith("01", d.Current!.A2dp);
        Assert.True(d.Select("aa:aa:aa:aa:aa:02"));
        Assert.EndsWith("02", d.Current!.A2dp);
        Assert.False(d.Select("FF:FF:FF:FF:FF:FF"));                      // 未知位址不改變選擇
        Assert.EndsWith("02", d.Current!.A2dp);
    }

    [Fact] public async Task Labels_tell_same_model_units_apart_by_address_tail()
    {
        var (d, _) = Make(Two);
        await d.ScanAsync(TimeSpan.Zero);
        d.RememberName("AA:AA:AA:AA:AA:01", "EDIFIER ES850NB");
        var labels = d.Devices.Select(DeviceDirectory.Label).ToList();
        Assert.Equal("ES850NB · AA:01", labels[0]);
        Assert.Equal("ES850NB · AA:02", labels[1]);   // 沒連過：用機種名稱
    }

    [Fact] public async Task RememberName_saves_only_when_it_changes()
    {
        var (d, saves) = Make(Two[0]);
        await d.ScanAsync(TimeSpan.Zero);
        int n = saves.Count;
        d.RememberName("AA:AA:AA:AA:AA:01", "X");
        d.RememberName("AA:AA:AA:AA:AA:01", "X");
        Assert.Equal(n + 1, saves.Count);
    }

    [Fact] public async Task A_ble_only_model_connects_over_ble_with_its_catalog_service_and_never_over_spp()
    {
        var s = new Settings();
        s.Devices.Add(new KnownDevice { A2dp = "AA:BB:B3:11:22:33", Ble = "AA:BB:B0:11:22:33", Model = "MP330" });
        (string, string)? called = null;
        var dir = new DeviceDirectory(s, _ => { }, pairedLister: NoPaired, isConnected: _ => Task.FromResult(false),
            bleConnect: (addr, svc) => { called = (addr, svc); throw new IOException("stub"); });
        await Assert.ThrowsAsync<IOException>(() => dir.MakeConnector()(CancellationToken.None));
        Assert.Equal(("AA:BB:B0:11:22:33", "48094c01-1a48-11e9-ab14-d663bd873d93"), called);
    }

    [Fact] public async Task A_ble_only_model_without_a_ble_address_is_reported_not_found()
    {
        var s = new Settings();
        s.Devices.Add(new KnownDevice { A2dp = "AA:AA:AA:AA:AA:01", Model = "MP330" });
        var ex = await Assert.ThrowsAsync<IOException>(() => new DeviceDirectory(s, _ => { }, pairedLister: NoPaired, isConnected: _ => Task.FromResult(false)).MakeConnector()(CancellationToken.None));
        Assert.Contains("MP330", ex.Message);
    }

    [Fact] public async Task Paired_device_without_advert_is_added_with_derived_ble_address_and_verified_session()
    {
        Assert.Equal("AA:BB:B0:11:22:33", DeviceDirectory.FallbackBleAddress("AA:BB:B3:11:22:33"));   // 格式與實機相同：第三個 byte B3 → B0（MP330）
        Assert.Equal("AA:BB:B0:44:55:66", DeviceDirectory.FallbackBleAddress("AA:BB:B3:44:55:66"));   // 格式與實機相同（ES850NB）
        var (d, saves) = Make(null, new List<(string, string)> { ("AA:BB:B3:11:22:33", "EDIFIER MP330"), ("AA:AA:AA:AA:AA:09", "EDIFIER ZZ999") });
        var got = await d.ScanAsync(TimeSpan.Zero);
        var mp = got.Single(x => x.Model == "MP330");
        Assert.Equal(("AA:BB:B0:11:22:33", 2, 0, "EDIFIER MP330"), (mp.Ble, mp.Version, mp.Encryption, mp.Name));  // MP330 實機不加密，不能套 ES850NB 的 16
        var other = got.Single(x => x.Model != "MP330");
        Assert.Equal(("Edifier", 0), (other.Model, other.Encryption));
        Assert.Equal(16, DeviceDirectory.FallbackSession("ES850NB").Encryption);
        Assert.NotEmpty(saves);
    }

    static Task<List<(string A2dp, string Name)>> NoPaired() => Task.FromResult(new List<(string A2dp, string Name)>());   // 不碰真實系統的已配對清單

    static DeviceDirectory TwoModels(string selected, HashSet<string> connected, List<string> bleCalls)
    {
        var s = new Settings { Selected = selected };
        s.Devices.Add(new KnownDevice { A2dp = "AA:BB:B3:44:55:66", Ble = "AA:BB:B0:44:55:66", Model = "ES850NB", Encryption = 16 });
        s.Devices.Add(new KnownDevice { A2dp = "AA:BB:B3:11:22:33", Ble = "AA:BB:B0:11:22:33", Model = "MP330" });
        return new DeviceDirectory(s, _ => { }, pairedLister: () => Task.FromResult(new List<(string, string)>()),
            isConnected: a => Task.FromResult(connected.Contains(a)),
            bleConnect: (addr, svc) => { bleCalls.Add(addr); throw new IOException("stub"); });
    }

    [Fact] public async Task Without_an_explicit_choice_the_device_that_is_connected_over_bluetooth_is_used()
    {
        // 使用者的實際情況：清單第一台是沒開機的 ES850NB，MP330 已連上電腦 → 應連 MP330（BLE），不去等 ES850NB 的 SPP
        var calls = new List<string>();
        var dir = TwoModels("", new HashSet<string> { "AA:BB:B3:11:22:33" }, calls);
        await Assert.ThrowsAsync<IOException>(() => dir.MakeConnector()(CancellationToken.None));
        Assert.Equal(new[] { "AA:BB:B0:11:22:33" }, calls);
    }

    [Fact] public async Task An_explicit_choice_is_kept_even_when_another_device_is_the_one_connected()
    {
        var calls = new List<string>();
        var dir = TwoModels("AA:BB:B3:11:22:33", new HashSet<string> { "AA:BB:B3:44:55:66" }, calls);   // 選了 MP330，但連著的是 ES850NB
        await Assert.ThrowsAsync<IOException>(() => dir.MakeConnector()(CancellationToken.None));
        Assert.Equal(new[] { "AA:BB:B0:11:22:33" }, calls);   // 仍只連 MP330，不擅自改選
    }

    [Fact] public async Task Fallback_does_not_override_advertised_devices()
    {
        var (d, _) = Make(null, new List<(string, string)> { (Two[0].Item1, "EDIFIER ES850NB") }, Two[0]);
        var got = await d.ScanAsync(TimeSpan.Zero);
        Assert.Single(got);
        Assert.Equal(16, got[0].Encryption);   // 有廣播就用廣播的 Session
    }
}

public class MenuSectionsTests : IDisposable
{
    public void Dispose() => Strings.SetLanguage(Strings.Default);

    static DeviceState Conn() => new(true, "EDIFIER ES850NB", "1.0.1", 16, new AncState(16, 1));

    [Fact] public void Menu_lists_devices_with_the_selected_one_checked_and_a_rescan_item()
    {
        var m = MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, false, false, new[] { ("A1", "ES850NB · 01", false), ("A2", "ES850NB · 02", true) }));
        Assert.Equal(new[] { ("dev:A1", false), ("dev:A2", true) }, m.Where(e => e.Id.StartsWith("dev:")).Select(e => (e.Id, e.Checked)));
        Assert.True(m.Single(e => e.Id == "rescan").Enabled);
        var busy = MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, false, false, scanning: true));
        Assert.False(busy.Single(e => e.Id == "rescan").Enabled);
        Assert.DoesNotContain(busy, e => e.Id.StartsWith("dev:"));     // 沒有裝置時只有停用的提示列
    }

    [Fact] public void Menu_has_theme_and_language_segments_marking_the_current_choice()
    {
        var segs = MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, false, false, theme: "dark", language: "ja")).Where(e => e.Kind == EntryKind.Segmented).ToList();
        Assert.Equal(new[] { "theme:system", "theme:light", "theme:dark" }, segs[0].Options!.Select(o => o.Id));
        Assert.Equal(new[] { "lang:zh-TW", "lang:zh-CN", "lang:en", "lang:ja" }, segs[1].Options!.Select(o => o.Id));
        Assert.Equal(new[] { "theme:dark" }, segs[0].Options!.Where(o => o.Selected).Select(o => o.Id));
        Assert.Equal(new[] { "lang:ja" }, segs[1].Options!.Where(o => o.Selected).Select(o => o.Id));
    }

    [Fact] public void Menu_text_follows_the_language()
    {
        Strings.SetLanguage("en");
        var m = MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, true, false));
        Assert.Equal("Unmute", m.Single(e => e.Id == "mute").Text);
        Assert.Equal("Quit", m.Single(e => e.Id == "quit").Text);
        Strings.SetLanguage("ja");
        Assert.Equal("パネルを開く", MenuTestUtil.Flat(TrayMenuModel.Build(Conn(), 50, false, false)).Single(e => e.Id == "open").Text);
    }
}

public class OtherModelsTests
{
    static readonly Session S = new(2, 16);

    static async Task<DeviceState> Connect(params byte[] drop)
    {
        Connector c = _ =>
        {
            var d = new FakeDevice(S);
            d.Silent.UnionWith(drop);
            return Task.FromResult(((Transport.ITransport)d, S));
        };
        var svc = new DeviceService(c, backoff: new[] { TimeSpan.FromMilliseconds(10) }, queryTimeout: TimeSpan.FromMilliseconds(150));
        svc.Start();
        try { await Task.Delay(1200); return svc.State; }
        finally { await svc.StopAsync(); }
    }

    [Fact] public async Task Device_without_anc_still_connects_with_battery_and_status()
    {
        var st = await Connect(Cmd.AncQuery);
        Assert.True(st.Connected);
        Assert.Equal(80, st.Battery);
        Assert.Null(st.Anc);
        Assert.False(st.AncSupported);
    }

    [Fact] public async Task Device_without_battery_still_connects_with_anc()
    {
        var st = await Connect(Cmd.BatteryQuery);
        Assert.True(st.Connected && st.Battery is null && st.Anc is not null);
    }

    [Fact] public async Task Device_answering_neither_battery_nor_anc_is_not_treated_as_connected() =>
        Assert.False((await Connect(Cmd.BatteryQuery, Cmd.AncQuery)).Connected);

    [Fact] public void Volume_target_switches_endpoint_by_device_name()
    {
        var opened = new List<string>();
        var v = new VolumeControl("EDIFIER", n => { opened.Add(n); return new VolumeTests.FakeEp(); });
        v.Get(); v.Get();
        v.SetTarget("EDIFIER ES850NB");   // 換成另一台：重新尋找端點
        v.Get();
        v.SetTarget("EDIFIER ES850NB");   // 同名不重找
        v.Get();
        Assert.Equal(new[] { "EDIFIER", "EDIFIER ES850NB" }, opened);
    }
}
