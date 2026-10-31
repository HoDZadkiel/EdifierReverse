using EdifierTray.Service;

namespace EdifierTray.Ui;

public enum EntryKind { Header, Label, Item, Separator, Segmented, Section }

/// <param name="Glyph">Segoe Fluent Icons 的字元（空字串＝沒有圖示）。</param>
/// <param name="Detail">右側的次要文字，例如目前音量。</param>
/// <param name="Id">動作代號：anc:1..5、mute、vol+、vol-、dev:&lt;位址&gt;、out:&lt;端點代號&gt;、rescan、theme:system|light|dark、lang:&lt;代碼&gt;、open、reconnect、autostart、quit。</param>
/// <param name="Options">分段按鈕：(代號, 文字, 是否選中)。</param>
/// <param name="Children">Section 的子選單項目：滑鼠移到大項上時從右側彈出。</param>
public sealed record MenuEntry(EntryKind Kind, string Text = "", string Glyph = "", string Detail = "",
                               bool Checked = false, bool Enabled = true, string Id = "",
                               IReadOnlyList<(string Id, string Text, bool Selected)>? Options = null,
                               IReadOnlyList<MenuEntry>? Children = null);

/// <summary>右鍵選單的內容。純資料，不碰 UI，方便測試；TrayMenuWindow 負責畫出來。項目與代號和 Python 版（menu.py）一致。</summary>
public static class TrayMenuModel
{
    public const int VolumeStep = 10;

    /// <param name="devices">(位址, 顯示名稱, 是否為目前選用) 的清單。</param>
    public static IReadOnlyList<MenuEntry> Build(DeviceState s, int? volume, bool muted, bool autostart,
                                                 IReadOnlyList<(string A2dp, string Label, bool Selected)>? devices = null,
                                                 string theme = "system", string language = "zh-TW", bool scanning = false,
                                                 string fallbackName = Catalog.GenericModel,
                                                 IReadOnlyList<AudioOutput>? outputs = null, bool? keepAwake = null)
    {
        devices ??= Array.Empty<(string, string, bool)>();
        bool selectable = s.Connected && s.AncSupported;
        bool canAnc = selectable && !s.Cooling;   // 冷卻期內裝置會忽略設定：停用，但目前模式的色條仍顯示
        bool hasVolume = volume is not null;
        var list = new List<MenuEntry>();
        MenuEntry Sep() => new(EntryKind.Separator);
        string T(string key, object? a = null) => Strings.T(key, a);

        // 標頭用簡短的模式名稱，長名稱（例如英文）會把整個選單撐寬
        var modeName = selectable && s.Anc is not null ? T($"anc.s.{s.Anc.Mode}") : s.AncLabel;
        // 沒有降噪的機種（例如 MP330）：只顯示電量，不放降噪項目
        var detail = !s.Connected ? T("m.not_connected")
                   : s.HasAnc ? T("m.header_detail", new { b = s.Battery, mode = modeName })
                   : T("m.header_battery", new { b = s.Battery });
        list.Add(new MenuEntry(EntryKind.Header, s.Name == "" ? fallbackName : s.Name, Detail: detail));
        list.Add(Sep());

        // 大項：滑鼠移到標題列上，子項目從右側彈出（標題列右側顯示目前狀態摘要）
        void Section(string key, string text, string summary, List<MenuEntry> children) =>
            list.Add(new MenuEntry(EntryKind.Section, text, Detail: summary, Id: $"sec:{key}", Children: children));

        if (s.HasAnc)
        {
            var modes = new List<MenuEntry>();
            foreach (var mode in new[] { 1, 2, 3, 4, 5 })
                modes.Add(new MenuEntry(EntryKind.Item, T($"anc.s.{mode}"), "", Checked: selectable && s.Anc?.Mode == mode,
                                        Enabled: canAnc, Id: $"anc:{mode}"));
            Section("anc", T("m.anc"), selectable && s.Anc is not null ? T($"anc.s.{s.Anc.Mode}") : "", modes);
            list.Add(Sep());
        }

        list.Add(new MenuEntry(EntryKind.Item, muted ? T("m.unmute") : T("m.mute"), muted ? "" : "",
                               Detail: hasVolume ? $"{volume}%" : "", Enabled: hasVolume, Id: "mute"));
        list.Add(new MenuEntry(EntryKind.Item, T("m.vol_up", new { n = VolumeStep }), "", Enabled: hasVolume, Id: "vol+"));
        list.Add(new MenuEntry(EntryKind.Item, T("m.vol_down", new { n = VolumeStep }), "", Enabled: hasVolume, Id: "vol-"));
        list.Add(Sep());

        // 裝置：多台 Edifier 裝置時選要控制哪一台
        var devs = devices.Select(d => new MenuEntry(EntryKind.Item, d.Label, "", Checked: d.Selected, Id: $"dev:{d.A2dp}")).ToList();
        if (devices.Count == 0) devs.Add(new MenuEntry(EntryKind.Item, T("m.no_devices"), "", Enabled: false));
        devs.Add(new MenuEntry(EntryKind.Item, scanning ? T("m.scanning") : T("m.rescan"), "", Enabled: !scanning, Id: "rescan"));
        Section("devices", T("m.devices"), devices.FirstOrDefault(d => d.Selected).Label ?? "", devs);

        // 輸出裝置：Windows 的播放端點（含預設的），選一個就設為預設輸出
        if (outputs is { Count: > 0 })
            Section("output", T("m.outputs"), AudioOutputs.Shorten(outputs.FirstOrDefault(o => o.IsDefault)?.Name ?? "", 20),
                    outputs.Select(o => new MenuEntry(EntryKind.Item, AudioOutputs.Shorten(o.Name), "", Checked: o.IsDefault, Id: $"out:{o.Id}")).ToList());

        // 電源：目前裝置的防自動關機。裝置有省電模式就顯示目前狀態、可重新檢查、可關閉（原生做法）；
        // 沒有（查詢沒回應）才退回「保持開機」（持續送出聽不到的訊號）
        if (s.PowerSave is bool ps)
        {
            string state = ps ? T("m.on") : T("m.off");
            Section("power", T("m.power"), T("m.power_state", new { state }), new()
            {
                new MenuEntry(EntryKind.Item, T("m.power_check"), "", Detail: state, Enabled: s.Connected, Id: "powercheck"),
                new MenuEntry(EntryKind.Item, T("m.no_auto_off"), "", Checked: !ps, Enabled: s.Connected, Id: "powersave"),
            });
        }
        else if (keepAwake is bool ka)
            Section("power", T("m.power"), ka ? T("m.on") : "",
                    new() { new MenuEntry(EntryKind.Item, T("m.keep_awake"), "", Checked: ka, Id: "keepawake") });

        Section("look", T("m.look_and_lang"), "", new()
        {
            new MenuEntry(EntryKind.Label, T("m.appearance")),
            new MenuEntry(EntryKind.Segmented, Options: new[] { "system", "light", "dark" }
                .Select(t => ($"theme:{t}", T($"m.theme.{t}"), t == theme)).ToList()),
            new MenuEntry(EntryKind.Label, T("m.language")),
            new MenuEntry(EntryKind.Segmented, Options: Strings.Languages
                .Select(l => ($"lang:{l.Code}", Strings.Short[l.Code], l.Code == language)).ToList()),
        });
        list.Add(Sep());

        list.Add(new MenuEntry(EntryKind.Item, T("m.open"), "", Id: "open"));
        list.Add(new MenuEntry(EntryKind.Item, T("m.reconnect"), "", Id: "reconnect"));
        list.Add(Sep());

        list.Add(new MenuEntry(EntryKind.Item, T("m.autostart"), "", Checked: autostart, Id: "autostart"));
        list.Add(new MenuEntry(EntryKind.Item, T("m.quit"), "", Id: "quit"));
        return list;
    }

    /// <summary>
    /// 選單位置：優先放在游標上方（工作列在下方時），放不下就放下方；水平方向貼著游標並夾在工作區內。
    /// 游標座標是裝置像素，其餘是 DIP；scale 是螢幕縮放（1.0 = 96 DPI）。
    /// </summary>
    public static (double Left, double Top) Place(double cursorX, double cursorY, double width, double height,
                                                  double scale, double waLeft, double waTop, double waRight,
                                                  double waBottom, double gap = 8)
    {
        double cx = cursorX / scale, cy = cursorY / scale;
        double left = Math.Min(cx - gap, waRight - width - gap);
        left = Math.Max(left, waLeft + gap);
        double top = cy - height - gap;
        if (top < waTop + gap) top = cy + gap;                     // 上方放不下（例如工作列在上緣）
        top = Math.Min(top, waBottom - height - gap);
        top = Math.Max(top, waTop + gap);
        return (left, top);
    }
}
