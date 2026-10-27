using EdifierTray.Service;

namespace EdifierTray.Ui;

public enum EntryKind { Header, Label, Item, Separator, Segmented }

/// <param name="Glyph">Segoe Fluent Icons 的字元（空字串＝沒有圖示）。</param>
/// <param name="Detail">右側的次要文字，例如目前音量。</param>
/// <param name="Id">動作代號：anc:1..5、mute、vol+、vol-、dev:&lt;位址&gt;、rescan、theme:system|light|dark、lang:&lt;代碼&gt;、open、reconnect、autostart、quit。</param>
/// <param name="Options">分段按鈕：(代號, 文字, 是否選中)。</param>
public sealed record MenuEntry(EntryKind Kind, string Text = "", string Glyph = "", string Detail = "",
                               bool Checked = false, bool Enabled = true, string Id = "",
                               IReadOnlyList<(string Id, string Text, bool Selected)>? Options = null);

/// <summary>右鍵選單的內容。純資料，不碰 UI，方便測試；TrayMenuWindow 負責畫出來。項目與代號和 Python 版（menu.py）一致。</summary>
public static class TrayMenuModel
{
    public const int VolumeStep = 10;

    /// <param name="devices">(位址, 顯示名稱, 是否為目前選用) 的清單。</param>
    public static IReadOnlyList<MenuEntry> Build(DeviceState s, int? volume, bool muted, bool autostart,
                                                 IReadOnlyList<(string A2dp, string Label, bool Selected)>? devices = null,
                                                 string theme = "system", string language = "zh-TW", bool scanning = false)
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
        var detail = s.Connected ? T("m.header_detail", new { b = s.Battery, mode = modeName }) : T("m.not_connected");
        list.Add(new MenuEntry(EntryKind.Header, s.Name == "" ? "ES850NB" : s.Name, Detail: detail));
        list.Add(Sep());

        list.Add(new MenuEntry(EntryKind.Label, T("m.anc")));
        foreach (var mode in new[] { 1, 2, 3, 4, 5 })
            list.Add(new MenuEntry(EntryKind.Item, T($"anc.s.{mode}"), "", Checked: selectable && s.Anc?.Mode == mode,
                                   Enabled: canAnc, Id: $"anc:{mode}"));
        list.Add(Sep());

        list.Add(new MenuEntry(EntryKind.Item, muted ? T("m.unmute") : T("m.mute"), muted ? "" : "",
                               Detail: hasVolume ? $"{volume}%" : "", Enabled: hasVolume, Id: "mute"));
        list.Add(new MenuEntry(EntryKind.Item, T("m.vol_up", new { n = VolumeStep }), "", Enabled: hasVolume, Id: "vol+"));
        list.Add(new MenuEntry(EntryKind.Item, T("m.vol_down", new { n = VolumeStep }), "", Enabled: hasVolume, Id: "vol-"));
        list.Add(Sep());

        list.Add(new MenuEntry(EntryKind.Label, T("m.devices")));
        foreach (var (a2dp, label, selected) in devices)
            list.Add(new MenuEntry(EntryKind.Item, label, "", Checked: selected, Id: $"dev:{a2dp}"));
        if (devices.Count == 0) list.Add(new MenuEntry(EntryKind.Item, T("m.no_devices"), "", Enabled: false));
        list.Add(new MenuEntry(EntryKind.Item, scanning ? T("m.scanning") : T("m.rescan"), "", Enabled: !scanning, Id: "rescan"));
        list.Add(Sep());

        list.Add(new MenuEntry(EntryKind.Label, T("m.appearance")));
        list.Add(new MenuEntry(EntryKind.Segmented, Options: new[] { "system", "light", "dark" }
            .Select(t => ($"theme:{t}", T($"m.theme.{t}"), t == theme)).ToList()));
        list.Add(new MenuEntry(EntryKind.Label, T("m.language")));
        list.Add(new MenuEntry(EntryKind.Segmented, Options: Strings.Languages
            .Select(l => ($"lang:{l.Code}", Strings.Short[l.Code], l.Code == language)).ToList()));
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
